using Client.Host.Hosting;
using Client.Licensing.Application;
using Client.Licensing.Domain;
using Licensing.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Licensing;

namespace Client.Licensing.Infrastructure;

/// <summary>Bound from the "Licensing" configuration section. Contains only PUBLIC key material.</summary>
public sealed class LicensingConfiguration
{
    public const string SectionName = "Licensing";

    /// <summary>The product this installation expects its license to be for.</summary>
    public string ProductId { get; set; } = "genericpos";

    /// <summary>Folder for licensing files. Empty = %LOCALAPPDATA%\GenericPOS\Licensing.</summary>
    public string? StorageDirectory { get; set; }

    /// <summary>Trusted issuer PUBLIC keys. Empty = no license can verify (fails closed).</summary>
    public List<TrustedLicenseKey> TrustedKeys { get; set; } = [];

    /// <summary>Policy: do entitlements stay granted during the grace period?</summary>
    public bool GraceGrantsEntitlements { get; set; } = true;

    /// <summary>Base URL of the license server (used by Client.Licensing.Http). HTTPS required except loopback.</summary>
    public string? ServerBaseUrl { get; set; }
}

/// <summary>Used when no transport is registered: licensing evaluation works, activation/renewal report "not configured".</summary>
internal sealed class NullLicenseClient : ILicenseClient
{
    public Task<ActivationResponse> ActivateAsync(ActivationRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(ActivationResponse.Failure(LicenseErrorCodes.ServerUnreachable, "No license server transport is configured."));

    public Task<RenewalResponse> RenewAsync(RenewalRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(RenewalResponse.Failure(LicenseErrorCodes.ServerUnreachable, "No license server transport is configured."));
}

/// <summary>
/// Loads identity and the local license at host start. Fully offline. A missing, invalid or expired license never
/// prevents the application from starting: the state is simply exposed through ILicenseEntitlementService.
/// </summary>
internal sealed class LicensingInitializer(ILicenseService licenseService, ILogger<LicensingInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var evaluation = await licenseService.InitializeAsync(cancellationToken);
            logger.LogInformation("License state: {State}", evaluation.State);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Licensing initialization failed; the installation is treated as unlicensed.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public static class LicensingServicesExtensions
{
    /// <summary>Registers client licensing (offline evaluation). Add a transport (e.g. AddLicenseHttpClient) for activation/renewal.</summary>
    public static IServiceCollection AddClientLicensing(this IServiceCollection services, IConfiguration configuration)
    {
        var config = new LicensingConfiguration();
        configuration.GetSection(LicensingConfiguration.SectionName).Bind(config);

        var storage = string.IsNullOrWhiteSpace(config.StorageDirectory)
            ? LicensingStorageOptions.Default()
            : new LicensingStorageOptions(config.StorageDirectory!);

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(config);
        services.AddSingleton(storage);
        services.AddSingleton(new LicensingOptions(config.ProductId));
        services.AddSingleton(new LicensePolicy(config.GraceGrantsEntitlements));
        services.AddSingleton<IInstallationIdentityStore, FileInstallationIdentityStore>();
        services.AddSingleton<ILicenseStore, FileLicenseStore>();
        services.AddSingleton<ILicenseVerifier>(_ => new EcdsaLicenseVerifier(config.TrustedKeys));
        services.AddSingleton<InstallationIdentityService>();
        services.TryAddSingleton<ILicenseClient, NullLicenseClient>();
        services.AddSingleton<LicenseService>();
        services.AddSingleton<ILicenseService>(sp => sp.GetRequiredService<LicenseService>());
        services.AddSingleton<ILicenseEntitlementService>(sp => sp.GetRequiredService<LicenseService>());
        services.AddHostedService<LicensingInitializer>();
        return services;
    }
}

/// <summary>IHostingModule for client licensing (same composition-root pattern as the business modules).</summary>
public sealed class LicensingHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddClientLicensing(context.Configuration);
}
