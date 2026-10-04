using Client.Host.Hosting;
using Client.Updater.Application;
using Client.Updater.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Platform.Core.Results;
using Platform.Infrastructure.Persistence;
using Security.Es256;
using Updates.Contracts;

namespace Client.Updater.Infrastructure;

/// <summary>
/// Restore points for the operational SQLite database. Uses SQLite's online backup API (consistent even while the
/// database is open). The updater never deletes, recreates or edits the database; RestoreAsync is an explicit,
/// operator-requested action that replaces the database file with the restore point.
/// </summary>
public sealed class SqliteDataSafeguard(string databasePath, UpdateStore store) : IDataSafeguard
{
    private const string NoDatabaseMarker = "NO_DATABASE";

    public Task<Result<string>> CreateRestorePointAsync(Guid packageId, CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = Path.Combine(store.RestoreDir, packageId.ToString("N"));
            Directory.CreateDirectory(dir);

            if (!File.Exists(databasePath))
            {
                var marker = Path.Combine(dir, NoDatabaseMarker);
                File.WriteAllText(marker, "There was no database when this restore point was taken.");
                return Task.FromResult(Result.Success(marker));
            }

            var backupPath = Path.Combine(dir, Path.GetFileName(databasePath) + ".bak");
            using (var source = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
            using (var destination = new SqliteConnection($"Data Source={backupPath};Pooling=False"))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);
            }

            SqliteConnection.ClearAllPools();
            return Task.FromResult(Result.Success(backupPath));
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure<string>(Error.Failure(UpdateErrorCodes.InstallationFailed, ex.Message)));
        }
    }

    public Task<Result> RestoreAsync(string restorePoint, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Path.GetFileName(restorePoint) == NoDatabaseMarker)
                return Task.FromResult(Result.Success());

            if (!File.Exists(restorePoint))
                return Task.FromResult(Result.Failure(Error.NotFound(UpdateErrorCodes.NotFound, "The restore point no longer exists.")));

            SqliteConnection.ClearAllPools();
            foreach (var sidecar in new[] { databasePath + "-wal", databasePath + "-shm" })
                if (File.Exists(sidecar)) File.Delete(sidecar);

            File.Copy(restorePoint, databasePath, overwrite: true);
            return Task.FromResult(Result.Success());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(Result.Failure(Error.Failure(UpdateErrorCodes.RollbackFailed, ex.Message)));
        }
    }
}

/// <summary>
/// Orchestrates migrations. It executes no SQL itself: it asks the module's own IModuleMigrator (if one is registered).
/// With no migrator the migration is DEFERRED: the module's own startup initializer applies its pending migrations when the
/// new version starts (that is how all current modules migrate). A migrator can only ever touch its own module's tables.
/// </summary>
public sealed class ModuleOwnedMigrationCoordinator(
    IEnumerable<IModuleMigrator> migrators,
    ILogger<ModuleOwnedMigrationCoordinator> logger) : IMigrationCoordinator
{
    public async Task<MigrationOutcome> MigrateAsync(MigrationRequest request, CancellationToken cancellationToken = default)
    {
        var migrator = migrators.FirstOrDefault(m => m.ModuleId == request.ModuleId);
        if (migrator is null)
        {
            logger.LogInformation("No migrator registered for {Module}; migration is deferred to the module's startup initializer.", request.ModuleId);
            return new MigrationOutcome(true, false, true, "Deferred to the module's own startup initializer.");
        }

        try
        {
            var result = await migrator.MigrateAsync(request.ToSchemaVersion, cancellationToken);
            return new MigrationOutcome(result.Succeeded, result.DatabaseModified, false, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Migrator for {Module} threw.", request.ModuleId);
            return new MigrationOutcome(false, true, false, ex.Message);
        }
    }
}

/// <summary>Reads what is installed: the running modules (module registry) overlaid with versions deployed by the updater (active pointers).</summary>
public sealed class InstalledStateProvider(
    UpdateStore store,
    IModuleRegistry registry,
    ModuleVersion baselineHostVersion) : IInstalledStateProvider
{
    public ModuleVersion HostVersion
        => store.ReadActive(PackageManifest.CoreTargetId) is { } pointer && ModuleVersion.TryParse(pointer.Version, out var v) && v is not null
            ? v
            : baselineHostVersion;

    public IReadOnlyList<InstalledModule> GetInstalledModules()
    {
        var modules = new Dictionary<string, InstalledModule>(StringComparer.Ordinal);

        foreach (var module in registry.GetAll())
        {
            var m = module.Manifest;
            modules[m.ModuleId.Value] = new InstalledModule(
                m.ModuleId, m.Version, m.Dependencies, m.MinimumPlatformVersion, m.MaximumPlatformVersion, m.DatabaseSchemaVersion);
        }

        foreach (var target in store.ListInstalledTargets())
        {
            if (target == PackageManifest.CoreTargetId) continue;

            var pointer = store.ReadActive(target);
            if (pointer is null || !ModuleVersion.TryParse(pointer.Version, out var version) || version is null) continue;

            var manifest = ReadDeployedManifest(target, pointer.Version);
            var dependencies = manifest?.Dependencies
                .Select(d => new ModuleDependency(new ModuleId(d.ModuleId), ToRange(d)))
                .ToList() ?? [];

            var existing = modules.GetValueOrDefault(target);
            modules[target] = new InstalledModule(
                new ModuleId(target), version, dependencies,
                existing?.MinimumPlatformVersion ?? new ModuleVersion(0, 0, 0),
                existing?.MaximumPlatformVersion,
                manifest?.Migration?.ToSchemaVersion ?? existing?.SchemaVersion ?? 0);
        }

        return modules.Values.ToList();
    }

    private PackageManifest? ReadDeployedManifest(string target, string version)
    {
        try
        {
            var path = Path.Combine(store.VersionDir(target, version), ".package", "manifest.json");
            if (!File.Exists(path)) return null;

            var envelope = PackageManifestSerializer.TryParseEnvelope(File.ReadAllText(path));
            return envelope is null ? null : PackageManifestSerializer.TryParseManifest(Convert.FromBase64String(envelope.Manifest));
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            return null;
        }
    }

    private static VersionRange ToRange(PackageDependency d)
    {
        var version = ModuleVersion.Parse(d.Version);
        return d.Operator switch
        {
            "==" => VersionRange.Exactly(version),
            "<=" => new VersionRange(version, VersionRangeOperator.LessThanOrEqual),
            ">" => new VersionRange(version, VersionRangeOperator.GreaterThan),
            "<" => new VersionRange(version, VersionRangeOperator.LessThan),
            _ => VersionRange.AtLeast(version)
        };
    }
}

/// <summary>Bound from the "Updater" configuration section. Contains only PUBLIC key material.</summary>
public sealed class UpdaterConfiguration
{
    public const string SectionName = "Updater";

    /// <summary>Folder for downloads/staging/installed/journal/restore. Empty = %LOCALAPPDATA%\GenericPOS\Updates.</summary>
    public string? UpdateRoot { get; set; }

    /// <summary>The core version of the built-in installation (used until an update activates another).</summary>
    public string BaselineHostVersion { get; set; } = "1.0.0";

    public string TargetFramework { get; set; } = "net10.0";

    public int MaxStartupAttempts { get; set; } = 2;

    /// <summary>Trusted package-signing PUBLIC keys (rotation-ready). Empty = no package verifies (fails closed).</summary>
    public List<TrustedPublicKey> TrustedKeys { get; set; } = [];

    /// <summary>Base URL of the update server (used by Client.Updater.Http). HTTPS required except loopback.</summary>
    public string? ServerBaseUrl { get; set; }
}

/// <summary>Used when no transport is registered: the updater works locally; discovery reports "unavailable".</summary>
internal sealed class NullUpdateClient : IUpdateClient
{
    public Task<UpdateCheckResponse> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(UpdateCheckResponse.Failure(UpdateErrorCodes.ServerUnavailable, "No update transport is configured."));

    public Task<Result> DownloadAsync(Guid packageId, Stream destination, CancellationToken cancellationToken = default)
        => Task.FromResult<Result>(Error.Failure(UpdateErrorCodes.ServerUnavailable, "No update transport is configured."));
}

/// <summary>
/// Startup recovery. Purely local: it never contacts the update server and never blocks or fails application startup.
/// </summary>
internal sealed class UpdaterInitializer(IUpdateService updates, ILogger<UpdaterInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var changed = await updates.RecoverAsync(cancellationToken);
            foreach (var j in changed)
                logger.LogWarning("Update recovery: {Target} {Version} -> {State}", j.TargetId, j.Version, j.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Update recovery failed; the application continues with the current installation.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class UpdaterServicesExtensions
{
    /// <summary>Registers the client updater. Requires ILicenseEntitlementService and IModuleRegistry (register licensing and the module host first).</summary>
    public static IServiceCollection AddClientUpdater(this IServiceCollection services, IConfiguration configuration)
    {
        var config = new UpdaterConfiguration();
        configuration.GetSection(UpdaterConfiguration.SectionName).Bind(config);

        var root = string.IsNullOrWhiteSpace(config.UpdateRoot)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenericPOS", "Updates")
            : config.UpdateRoot!;

        var database = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(database);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(config);
        var store = new UpdateStore(root);
        services.AddSingleton(store);
        services.AddSingleton<IUpdateStore>(store);
        services.AddSingleton(new UpdaterOptions(config.TargetFramework, config.MaxStartupAttempts));
        services.AddSingleton(_ => new Es256Verifier(config.TrustedKeys));
        services.AddSingleton<IInstalledStateProvider>(sp => new InstalledStateProvider(
            sp.GetRequiredService<UpdateStore>(),
            sp.GetRequiredService<IModuleRegistry>(),
            ModuleVersion.Parse(config.BaselineHostVersion)));
        services.AddSingleton<IDataSafeguard>(sp => new SqliteDataSafeguard(database.ResolveDatabasePath(), sp.GetRequiredService<UpdateStore>()));
        services.AddSingleton<IMigrationCoordinator, ModuleOwnedMigrationCoordinator>();
        services.TryAddSingleton<IUpdateClient, NullUpdateClient>();
        services.AddSingleton<PackageVerifier>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<UpdateService>());
        services.AddHostedService<UpdaterInitializer>();
        return services;
    }
}

/// <summary>IHostingModule for the client updater (same composition-root pattern as licensing and the business modules).</summary>
public sealed class UpdaterHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddClientUpdater(context.Configuration);
}
