using AdminPortal.Application;
using BackupServer.Application;
using Cloud.Infrastructure.Persistence;
using Cloud.Infrastructure.Repositories;
using Cloud.Infrastructure.Storage;
using LicenseServer.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Security.Es256;
using UpdateServer.Application;

namespace Cloud.Infrastructure;

/// <summary>Where the server database is and whether this host applies its migrations at startup.</summary>
public sealed record CloudDatabaseOptions(string ConnectionString, bool MigrateOnStartup);

/// <summary>Creates the server database file's directory and applies the server's migrations (owned by this project).</summary>
internal sealed class CloudDatabaseInitializer(IDbContextFactory<CloudDbContext> factory, CloudDatabaseOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.MigrateOnStartup) return;

        var source = new SqliteConnectionStringBuilder(options.ConnectionString).DataSource;
        if (!string.IsNullOrWhiteSpace(source) && source != ":memory:" && Path.GetDirectoryName(Path.GetFullPath(source)) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // WAL lets the license, update, backup and admin hosts share the one server database file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await db.Database.MigrateAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Used only by the EF tooling to create migrations.</summary>
internal sealed class CloudDbContextDesignTimeFactory : IDesignTimeDbContextFactory<CloudDbContext>
{
    public CloudDbContext CreateDbContext(string[] args)
        => new(new DbContextOptionsBuilder<CloudDbContext>().UseSqlite("Data Source=cloud-design-time.db").Options);
}

/// <summary>
/// Composition helpers for the Stage 9 server hosts. Everything environment-specific (database, directories, keys, limits)
/// comes from configuration: nothing is hard-coded and no secret lives in source control.
/// </summary>
public static class CloudInfrastructureExtensions
{
    /// <summary>
    /// Registers the server database. Configuration: <c>CloudDatabase:ConnectionString</c> (required) and
    /// <c>CloudDatabase:MigrateOnStartup</c> (default true).
    /// </summary>
    public static IServiceCollection AddCloudDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration["CloudDatabase:ConnectionString"];
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("CloudDatabase:ConnectionString is required.");

        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContextFactory<CloudDbContext>(o => o.UseSqlite(connectionString));
        services.AddSingleton(new CloudDatabaseOptions(connectionString, configuration.GetValue("CloudDatabase:MigrateOnStartup", true)));

        // The append-only audit table is shared by every host: the vendor reads one trail (admin actions and customer-facing security facts).
        services.TryAddSingleton<IAdminAuditLog, EfAdminAuditLog>();
        services.AddHostedService<CloudDatabaseInitializer>();
        return services;
    }

    /// <summary>Durable <see cref="ILicenseRepository"/> (+ <see cref="ILicenseQuery"/>) for the license server and administration.</summary>
    public static IServiceCollection AddLicenseStore(this IServiceCollection services)
    {
        services.AddSingleton<EfLicenseRepository>();
        services.AddSingleton<ILicenseRepository>(sp => sp.GetRequiredService<EfLicenseRepository>());
        services.AddSingleton<ILicenseQuery>(sp => sp.GetRequiredService<EfLicenseRepository>());
        return services;
    }

    /// <summary>
    /// Durable package catalog + file store. Configuration: <c>UpdateServer:PackageDirectory</c> (required): the directory
    /// the administration host writes packages to and the update server reads them from.
    /// </summary>
    public static IServiceCollection AddPackageStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IPackageFileStore>(new FilePackageStore(Required(configuration, "UpdateServer:PackageDirectory")));
        services.AddSingleton<IPackageCatalog, EfPackageCatalog>();
        services.AddSingleton<IPackageRepository, CatalogPackageRepository>();
        return services;
    }

    /// <summary>Backup catalog, tokens and blob store. Configuration: <c>BackupServer:StorageDirectory</c> (required).</summary>
    public static IServiceCollection AddBackupStore(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IBackupBlobStore>(new FileBackupBlobStore(Required(configuration, "BackupServer:StorageDirectory")));
        services.AddSingleton<IBackupCatalog, EfBackupCatalog>();
        services.AddSingleton<IBackupAccessTokenStore, EfBackupAccessTokenStore>();
        return services;
    }

    /// <summary>
    /// The backup server's services: <c>BackupServer:MaxBackupBytes</c>, <c>:MaxBackupsPerLicense</c> and <c>:RequiredModule</c>
    /// (default "cloud-backup"; an explicitly empty value requires no entitlement).
    /// </summary>
    public static IServiceCollection AddBackupServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddLicenseStore();
        services.AddBackupStore(configuration);
        services.AddSingleton(BackupOptions(configuration));
        services.AddSingleton<BackupAccessService>();
        services.AddSingleton<BackupService>();
        return services;
    }

    /// <summary>
    /// The administration host's services. Configuration: <c>AdminPortal:Keys</c> (list of Name + Sha256 of the API key),
    /// <c>UpdateServer:TrustedKeys</c> (optional: KeyId + PublicKey, to reject mis-signed uploads),
    /// <c>UpdateServer:MaxPackageBytes</c>, plus everything the license, package and backup stores need.
    /// </summary>
    public static IServiceCollection AddAdminPortalServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddBackupServices(configuration);
        services.AddPackageStore(configuration);

        services.AddSingleton<ICustomerRepository, EfCustomerRepository>();
        services.AddSingleton<IModuleRegistryRepository, EfModuleRegistryRepository>();
        services.AddSingleton<IAdminAuditLog, EfAdminAuditLog>();

        services.AddSingleton(new AdminKeyAuthenticator(configuration.GetSection("AdminPortal:Keys").Get<List<AdminKeyEntry>>() ?? []));
        services.AddSingleton(new PackageSigningPolicy(configuration.GetSection("UpdateServer:TrustedKeys").Get<List<TrustedPublicKey>>() ?? []));
        services.AddSingleton(new PackageAdminOptions(configuration.GetValue("UpdateServer:MaxPackageBytes", PackageAdminOptions.DefaultMaxPackageBytes)));

        services.AddSingleton<PackageInspector>();
        services.AddSingleton<AdminAuditRecorder>();
        services.AddSingleton<CustomerAdminService>();
        services.AddSingleton<LicenseAdminService>();
        services.AddSingleton<ModuleRegistryService>();
        services.AddSingleton<PackageAdminService>();
        services.AddSingleton<AdminOperationsService>();
        return services;
    }

    private static BackupServerOptions BackupOptions(IConfiguration configuration)
        => new(
            configuration.GetValue("BackupServer:MaxBackupBytes", BackupServerOptions.DefaultMaxBackupBytes),
            configuration.GetValue("BackupServer:MaxBackupsPerLicense", BackupServerOptions.DefaultMaxBackupsPerLicense),
            configuration["BackupServer:RequiredModule"] ?? BackupServerOptions.DefaultRequiredModule);

    private static string Required(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is required.")
            : value;
    }
}
