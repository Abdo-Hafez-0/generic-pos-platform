using System.Reflection;
using Client.Host.Hosting;
using CloudBackup.Application;
using CloudBackup.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Backup;
using Platform.Core.Modules;
using Platform.Core.Results;
using Platform.Infrastructure.Persistence;
using Security.BackupEnvelope;

namespace CloudBackup.Infrastructure;

/// <summary>
/// Configuration section "CloudBackup" (the module's own; the HTTP transport reads ServerBaseUrl):
///   Root                       - the module's folder (default: "CloudBackup" next to the database file)
///   EscrowKeys:N:KeyId/PublicKey - the vendor's escrow public keys (base64 SPKI, ECDH P-256); the last one seals new backups
///   UploadStartDelaySeconds    - first upload round after the start (default 30); then every 5 minutes, or at once when something new waits
/// </summary>
public sealed class CloudBackupConfiguration
{
    public const string SectionName = "CloudBackup";

    public string? Root { get; set; }

    public List<EscrowPublicKey> EscrowKeys { get; set; } = [];

    public int? UploadStartDelaySeconds { get; set; }
}

public sealed record CloudUploadOptions(TimeSpan StartDelay, TimeSpan Interval, string ClientVersion);

/// <summary>The transport used when no HTTP transport is registered: the cloud is simply not reachable.</summary>
public sealed class NullBackupClient : IBackupClient
{
    private static Error Unavailable() => Error.Failure(BackupClientErrorCodes.Unavailable, "No backup server is configured. The local backups are not affected.");

    public Task<Result<CloudBackupItem>> UploadAsync(string access, Stream content, string sha256, string label, string clientVersion, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<CloudBackupItem>(Unavailable()));

    public Task<Result<IReadOnlyList<CloudBackupItem>>> ListAsync(string access, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<IReadOnlyList<CloudBackupItem>>(Unavailable()));

    public Task<Result<Stream>> DownloadAsync(string access, Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<Stream>(Unavailable()));

    public Task<Result> DeleteAsync(string access, Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(Unavailable()));
}

/// <summary>Wakes the upload worker when something new waits (raised after a new encrypted copy or a new access key).</summary>
public sealed class UploadSignal : IUploadSignal
{
    private readonly SemaphoreSlim _raised = new(0, 1);

    public void Raise()
    {
        if (_raised.CurrentCount == 0) _raised.Release();
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _raised.WaitAsync(timeout, cancellationToken);
}

/// <summary>
/// Uploads the outbox in the background: a little after the start, then every 5 minutes or as soon as something new waits. Nothing it
/// meets ever stops the application; failures are recorded and retried later (see <see cref="CloudBackupService.UploadDueAsync"/>).
/// </summary>
public sealed class CloudUploadWorker(CloudBackupService cloud, UploadSignal signal, CloudUploadOptions options, ILogger<CloudUploadWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            cloud.SweepDownloads();   // a crash during a restore can leave a plain copy behind
            try
            {
                await cloud.SweepUsedRequestsAsync(stoppingToken);   // MISS-04f: a vendor restore key works once
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Used recovery requests could not be removed; they are tried again at the next start.");
            }

            await Task.Delay(options.StartDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var uploaded = await cloud.UploadDueAsync(options.ClientVersion, stoppingToken);
                    if (uploaded > 0) logger.LogInformation("{Count} cloud backup cop(y/ies) uploaded.", uploaded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The cloud upload met an unexpected problem; it tries again later.");
                }

                await signal.WaitAsync(options.Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // the application is closing
        }
    }
}

public static class CloudBackupServicesExtensions
{
    public static IServiceCollection AddCloudBackupModule(this IServiceCollection services, IConfiguration configuration)
    {
        var config = new CloudBackupConfiguration();
        configuration.GetSection(CloudBackupConfiguration.SectionName).Bind(config);

        var database = new DatabaseOptions();
        configuration.GetSection(DatabaseOptions.SectionName).Bind(database);
        var root = string.IsNullOrWhiteSpace(config.Root)
            ? Path.Combine(Path.GetDirectoryName(database.ResolveDatabasePath())!, "CloudBackup")
            : config.Root!;

        var workspace = new CloudBackupWorkspace(root);
        services.AddSingleton(workspace);
        services.AddSingleton(new CloudDownloadArea(workspace.DownloadDirectory));
        services.AddSingleton<IEscrowKeySource>(new ConfiguredEscrowKeys(config.EscrowKeys));
        services.AddSingleton(new CloudUploadOptions(
            TimeSpan.FromSeconds(Math.Clamp(config.UploadStartDelaySeconds ?? 30, 0, 3600)), TimeSpan.FromMinutes(5), ApplicationVersion()));
        services.AddCloudBackupCore();
        services.AddSingleton<IModule, CloudBackupModule>();
        services.AddHostedService<CloudUploadWorker>();
        return services;
    }

    /// <summary>The module's services without the configuration and hosting plumbing (also used by tests).</summary>
    public static IServiceCollection AddCloudBackupCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IBackupClient, NullBackupClient>();   // the HTTP transport (Client.CloudBackup.Http) replaces it
        services.AddSingleton<IKeyRing, ProtectedKeyRing>();
        services.AddSingleton<ICloudAccessStore, ProtectedAccessStore>();
        services.AddSingleton(sp => new JsonCloudOutbox(sp.GetRequiredService<CloudBackupWorkspace>(), sp.GetRequiredService<ILogger<JsonCloudOutbox>>()));
        services.AddSingleton<ICloudOutbox>(sp => sp.GetRequiredService<JsonCloudOutbox>());
        services.AddSingleton<ICloudStatusStore, JsonCloudStatusStore>();
        services.AddSingleton<IRecoveryRequestStore, ProtectedRecoveryRequestStore>();
        services.AddSingleton<UploadSignal>();
        services.AddSingleton<IUploadSignal>(sp => sp.GetRequiredService<UploadSignal>());
        services.AddSingleton<CloudBackupService>();
        services.AddSingleton<IBackupReplica>(sp => sp.GetRequiredService<CloudBackupService>());
        services.AddSingleton<ICloudBackupNotice>(sp => sp.GetRequiredService<CloudBackupService>());

        services.AddSingleton<ICapabilityProvider, CloudBackupCapabilityProvider>();
        services.AddTransient<BeginRecoveryCodeCommandHandler>();
        services.AddTransient<ConfirmRecoveryCodeCommandHandler>();
        services.AddTransient<SetBackupAccessCommandHandler>();
        services.AddTransient<GetCloudBackupOverviewQueryHandler>();
        services.AddTransient<ListCloudBackupsQueryHandler>();
        services.AddTransient<DownloadForRestoreCommandHandler>();
        services.AddTransient<DiscardDownloadCommandHandler>();
        services.AddTransient<CreateRecoveryRequestCommandHandler>();
        services.AddTransient<DownloadWithRestoreKeyCommandHandler>();
        services.AddTransient<VendorRestoreDecidedCommandHandler>();
        return services;
    }

    private static string ApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(CloudBackupServicesExtensions).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() ?? "unknown" : informational.Split('+')[0];
    }
}

public sealed class CloudBackupHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddCloudBackupModule(context.Configuration);
}

public sealed class CloudBackupModuleManifest : IModuleManifest
{
    public static readonly CloudBackupModuleManifest Instance = new();

    private CloudBackupModuleManifest() { }

    public ModuleId ModuleId => new(CloudBackupCapabilities.Module);
    public string Name => "Cloud Backup";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;
    public IReadOnlyList<ModuleDependency> Dependencies => [];
    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures => [new Feature(new FeatureId("cloud-backup.encrypted-copies"), "Encrypted cloud copies of the backups")];
    public int DatabaseSchemaVersion => 0;

    private sealed record Feature(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}

/// <summary>Owns no tables and no start-up work: its files live next to the database, its uploads in a hosted worker.</summary>
public sealed class CloudBackupModule : IModule
{
    private ModuleRuntimeStatus _status = ModuleRuntimeStatus.Registered;

    public IModuleManifest Manifest => CloudBackupModuleManifest.Instance;

    public ModuleRuntimeStatus Status => _status;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _status = ModuleRuntimeStatus.Enabled;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _status = ModuleRuntimeStatus.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        _status = ModuleRuntimeStatus.Stopped;
        return Task.CompletedTask;
    }
}
