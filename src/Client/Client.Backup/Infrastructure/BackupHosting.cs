using System.Reflection;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Auditing;
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

/// <summary>
/// At start: empties the staging folder (a crash during a backup can leave a temporary copy of the shop data behind), and reports the
/// outcome of a restore that the start-up step just carried out to the audit log - once, in the RESTORED data, under the name of the
/// person who confirmed it (nobody is signed in yet).
/// </summary>
public sealed class BackupInitializer(
    BackupWorkspace workspace,
    ILogger<BackupInitializer> logger,
    IRestoreStateStore? restoreStates = null,
    IBusinessEventSink? events = null) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        SweepStaging();
        await ReportRestoreOutcomeAsync();
    }

    private async Task ReportRestoreOutcomeAsync()
    {
        try
        {
            if (restoreStates is null || await restoreStates.ReadOutcomeAsync() is not { Reported: false } outcome) return;

            await events.TryRecordAsync(BusinessEvent.Create(BackupService.AuditModule, outcome.Succeeded ? "backup.restored" : "backup.restore-failed", "backup",
                outcome.Id.ToString(), outcome.Message, $"backup={outcome.SourceFileName};made={outcome.BackupCreatedAt:yyyy-MM-dd HH:mm}")
                with { ActorId = outcome.RequestedById, ActorName = outcome.RequestedByName });
            await restoreStates.WriteOutcomeAsync(outcome with { Reported = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The outcome of the last restore could not be reported.");
        }
    }

    private void SweepStaging()
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
        services.AddSingleton<BackupGate>();
        services.AddSingleton<BackupService>();
        // MISS-04b: restore across a restart; the swap runs before anything opens the database (IStartupPreparation)
        services.AddSingleton<IRestoreStateStore, JsonRestoreStateStore>();
        services.AddSingleton<RestoreService>();
        services.AddSingleton<IStartupPreparation>(sp => new PendingRestoreStep(databasePath, workspace,
            sp.GetRequiredService<IRestoreStateStore>(), sp.GetRequiredService<IBackupHistoryStore>(), sp.GetRequiredService<IDatabaseSnapshotter>(),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<PendingRestoreStep>>()));

        services.AddSingleton<ICapabilityProvider, BackupCapabilityProvider>();
        services.AddTransient<CreateBackupCommandHandler>();
        services.AddTransient<VerifyBackupCommandHandler>();
        services.AddTransient<GetBackupHistoryQueryHandler>();
        services.AddTransient<DeleteBackupCommandHandler>();
        services.AddTransient<GetBackupSettingsQueryHandler>();
        services.AddTransient<UpdateBackupSettingsCommandHandler>();
        services.AddTransient<PrepareRestoreCommandHandler>();
        services.AddTransient<PrepareRestoreFromFileCommandHandler>();
        services.AddTransient<ConfirmRestoreCommandHandler>();
        services.AddTransient<CancelRestoreCommandHandler>();
        services.AddTransient<GetRestoreStatusQueryHandler>();
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
