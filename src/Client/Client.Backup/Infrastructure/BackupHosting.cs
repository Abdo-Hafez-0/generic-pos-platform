using System.Reflection;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Authorization;
using Platform.Infrastructure.Persistence;

namespace Client.Backup.Infrastructure;

/// <summary>
/// Configuration section "Backup". All optional:
///   Root        - the backup component's own folder (default: "Backup" next to the database file)
///   LocalFolder - a backup folder preset by an installer (until someone with backup.configure chooses one)
///   KeepLocal   - how many backups the folder keeps (default 14)
/// </summary>
public sealed class BackupConfiguration
{
    public const string SectionName = "Backup";

    public string? Root { get; set; }

    public string? LocalFolder { get; set; }

    public int? KeepLocal { get; set; }
}

/// <summary>Empties the staging folder at start: a crash during a backup can leave a temporary copy of the shop data behind.</summary>
public sealed class BackupInitializer(BackupWorkspace workspace, ILogger<BackupInitializer> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(workspace.StagingDirectory);
            foreach (var file in Directory.EnumerateFiles(workspace.StagingDirectory))
            {
                try
                {
                    File.Delete(file);
                    logger.LogInformation("Removed a leftover temporary backup file {File}.", Path.GetFileName(file));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Leftover temporary backup file {File} could not be removed.", file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Backup housekeeping must never stop the shop from starting.
            logger.LogWarning(ex, "The backup working folder could not be prepared.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class BackupServicesExtensions
{
    /// <summary>Registers local backup (MISS-04a). Works with no network, no license and no Audit module (auditing is optional).</summary>
    public static IServiceCollection AddClientBackup(this IServiceCollection services, IConfiguration configuration)
    {
        var config = new BackupConfiguration();
        configuration.GetSection(BackupConfiguration.SectionName).Bind(config);

        var database = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(database);
        var databasePath = database.ResolveDatabasePath();

        var root = string.IsNullOrWhiteSpace(config.Root)
            ? Path.Combine(Path.GetDirectoryName(databasePath)!, "Backup")
            : config.Root!;
        var keep = Math.Clamp(config.KeepLocal ?? BackupSettings.DefaultKeepLocal, BackupSettings.MinKeepLocal, BackupSettings.MaxKeepLocal);

        services.TryAddSingleton(TimeProvider.System);
        var workspace = new BackupWorkspace(root);
        services.AddSingleton(workspace);
        services.AddSingleton<IBackupWorkspace>(workspace);
        services.AddSingleton(new BackupRuntime(ApplicationVersion()));
        services.AddSingleton<IDatabaseSnapshotter>(sp => new SqliteDatabaseSnapshotter(databasePath, sp.GetRequiredService<ILogger<SqliteDatabaseSnapshotter>>()));
        services.AddSingleton<ILocalDestinationFactory, LocalDestinationFactory>();
        services.AddSingleton<IBackupHistoryStore, JsonBackupHistoryStore>();
        services.AddSingleton<IBackupSettingsStore>(sp => new JsonBackupSettingsStore(
            workspace, new BackupSettings(string.IsNullOrWhiteSpace(config.LocalFolder) ? null : config.LocalFolder, keep),
            sp.GetRequiredService<ILogger<JsonBackupSettingsStore>>()));
        services.AddSingleton<BackupService>();

        services.AddSingleton<ICapabilityProvider, BackupCapabilityProvider>();
        services.AddTransient<CreateBackupCommandHandler>();
        services.AddTransient<VerifyBackupCommandHandler>();
        services.AddTransient<GetBackupHistoryQueryHandler>();
        services.AddTransient<DeleteBackupCommandHandler>();
        services.AddTransient<GetBackupSettingsQueryHandler>();
        services.AddTransient<UpdateBackupSettingsCommandHandler>();
        services.AddHostedService<BackupInitializer>();
        return services;
    }

    private static string ApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(BackupServicesExtensions).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() : informational.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }
}

/// <summary>IHostingModule for local backup (same composition-root pattern as the updater and licensing).</summary>
public sealed class ClientBackupHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddClientBackup(context.Configuration);
}
