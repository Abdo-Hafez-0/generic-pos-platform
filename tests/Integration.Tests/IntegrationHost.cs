using Audit.Infrastructure.Module;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Catalog.Infrastructure.Module;
using CashManagement.Infrastructure.Module;
using Client.Host.Hosting;
using Client.ModuleHost;
using Customers.Infrastructure.Module;
using Inventory.Infrastructure.Module;
using Payments.Infrastructure.Module;
using POS.Infrastructure.Module;
using Pricing.Infrastructure.Module;
using Purchasing.Infrastructure.Module;
using Reporting.Infrastructure.Module;
using Sales.Infrastructure.Module;
using Suppliers.Infrastructure.Module;
using Users.Infrastructure.Module;

namespace Integration.Tests;

/// <summary>Serialises every test that starts a real host: the host reads its database location from process-wide environment variables.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HostCollection
{
    public const string Name = "Real host";
}

/// <summary>
/// Starts the REAL application host (ApplicationHostBuilder, the real hosting modules, the real migrations) against a throw-away SQLite file.
/// Nothing is stubbed: this is the same composition the desktop app uses, minus WPF and the licensing/update modules.
/// </summary>
public sealed class IntegrationHost : IAsyncDisposable
{
    private const string Prefix = "GENERICPOS_Database__";
    private const string SubDirectory = "GenericPOS";
    private static readonly string[] Keys = ["DatabaseFolder", "CustomFolderPath", "DatabaseFileName"];

    private readonly IApplicationHost _host;
    private readonly string _folder;

    private IntegrationHost(IApplicationHost host, string folder, string fileName)
    {
        _host = host;
        _folder = folder;
        DatabasePath = Path.Combine(folder, SubDirectory, fileName);   // DatabaseOptions puts the file in <folder>/<ApplicationSubDirectory>/
    }

    public IServiceProvider Services => _host.Services;
    public string DatabasePath { get; }

    /// <summary>The folder holding the database file.</summary>
    public string Folder => _folder;

    /// <summary>When true, disposing stops the host but keeps the database folder so another host can start on it (the caller deletes it).</summary>
    public bool KeepFiles { get; set; }

    /// <summary>The stage 1-7 modules the Stage 8 modules sit next to. They never depend on a Stage 8 module.</summary>
    public static readonly IReadOnlyList<string> CoreModules = ["Catalog", "Inventory", "Sales", "POS"];

    public static readonly IReadOnlyList<string> Stage8Modules =
        ["Customers", "Suppliers", "Purchasing", "Pricing", "Payments", "Users", "Audit", "CashManagement", "Reporting"];

    /// <summary>The Stage 8 modules a module needs in order to be enabled (its manifest dependencies on other Stage 8 modules).</summary>
    public static IReadOnlyList<string> Stage8DependenciesOf(string module) => module switch
    {
        "Purchasing" => ["Suppliers"],
        _ => []
    };

    public static IHostingModule Create(string module) => module switch
    {
        "Catalog" => new CatalogHostingModule(),
        "Inventory" => new InventoryHostingModule(),
        "Sales" => new SalesHostingModule(),
        "POS" => new POSHostingModule(),
        "Customers" => new CustomersHostingModule(),
        "Suppliers" => new SuppliersHostingModule(),
        "Purchasing" => new PurchasingHostingModule(),
        "Pricing" => new PricingHostingModule(),
        "Payments" => new PaymentsHostingModule(),
        "Users" => new UsersHostingModule(),
        "Audit" => new AuditHostingModule(),
        "CashManagement" => new CashManagementHostingModule(),
        "Reporting" => new ReportingHostingModule(),
        _ => throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown module.")
    };

    /// <summary>Starts a host with the module host plus exactly the named modules, in the order given.</summary>
    public static async Task<IntegrationHost> StartAsync(IEnumerable<string> modules, string? reuseFolder = null)
    {
        var folder = reuseFolder ?? Path.Combine(Path.GetTempPath(), "genericpos-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        const string fileName = "integration.db";

        Environment.SetEnvironmentVariable(Prefix + "DatabaseFolder", "Custom");
        Environment.SetEnvironmentVariable(Prefix + "CustomFolderPath", folder);
        Environment.SetEnvironmentVariable(Prefix + "DatabaseFileName", fileName);

        try
        {
            var builder = ApplicationHostBuilder.Create().WithModule(new ModuleHostRegistrar());
            foreach (var module in modules) builder.WithModule(Create(module));

            var host = builder.Build();
            await host.StartAsync();
            return new IntegrationHost(host, folder, fileName);
        }
        catch
        {
            ClearEnvironment();
            if (reuseFolder is null) TryDelete(folder);
            throw;
        }
    }

    public static Task<IntegrationHost> StartAllAsync() => StartAsync([.. CoreModules, .. Stage8Modules]);

    public async ValueTask DisposeAsync()
    {
        try { await _host.StopAsync(); }
        finally
        {
            ClearEnvironment();
            SqliteConnection.ClearAllPools();
            if (!KeepFiles) TryDelete(_folder);
        }
    }

    public static void DeleteFolder(string folder) => TryDelete(folder);

    /// <summary>Opens the database file directly (read-only use) to inspect what the migrations really created.</summary>
    public async Task<SqliteConnection> OpenDatabaseAsync()
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await connection.OpenAsync();
        return connection;
    }

    public async Task<IReadOnlyList<string>> GetTablesAsync()
    {
        await using var connection = await OpenDatabaseAsync();
        return await QueryAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
    }

    public static async Task<IReadOnlyList<string>> QueryAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static void ClearEnvironment()
    {
        foreach (var key in Keys) Environment.SetEnvironmentVariable(Prefix + key, null);
    }

    private static void TryDelete(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (IOException) { /* a leftover temp folder is harmless */ }
        catch (UnauthorizedAccessException) { }
    }
}
