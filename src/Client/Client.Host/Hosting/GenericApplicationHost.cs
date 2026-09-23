using Client.Host.Configuration;
using Client.Host.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Client.Host.Hosting;

/// <summary>
/// Concrete implementation of IApplicationHost backed by Microsoft.Extensions.Hosting.
/// Coordinates the entire application startup sequence:
///
///   Load Configuration
///        ↓
///   Configure Logging
///        ↓
///   Register Platform Services
///        ↓
///   Register Module Host Services    ← Module host registrations injected via IHostingModule
///        ↓
///   Build Host
///        ↓
///   Start Services
///        ↓
///   Ready (WPF UI starts)
///        ↓
///   Graceful Shutdown
///
/// This class knows NOTHING about business modules. Module registration is delegated to
/// the IHostingModule registrations which are provided by Client.ModuleHost.
/// </summary>
internal sealed class GenericApplicationHost : IApplicationHost
{
    private IHost? _host;
    private readonly IReadOnlyList<IHostingModule> _hostingModules;

    public GenericApplicationHost(IReadOnlyList<IHostingModule> hostingModules)
    {
        _hostingModules = hostingModules;
    }

    /// <inheritdoc/>
    public IServiceProvider Services =>
        _host?.Services ?? throw new InvalidOperationException(
            "Services are not available before StartAsync() has been called.");

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = CreateHostBuilder();
        _host = builder.Build();

        var logger = _host.Services.GetRequiredService<ILogger<GenericApplicationHost>>();
        logger.LogInformation("Application host starting...");

        await _host.StartAsync(cancellationToken);

        logger.LogInformation("Application host started successfully.");
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_host is null)
            return;

        var logger = _host.Services.GetRequiredService<ILogger<GenericApplicationHost>>();
        logger.LogInformation("Application host stopping...");

        await _host.StopAsync(cancellationToken);
        _host.Dispose();
        _host = null;

        logger.LogInformation("Application host stopped.");
    }

    private IHostBuilder CreateHostBuilder()
    {
        return Microsoft.Extensions.Hosting.Host
            .CreateDefaultBuilder()
            .ConfigureAppConfiguration(AppConfigurationBuilder.Configure)
            .ConfigureLogging(AppLoggingBuilder.Configure)
            .ConfigureServices((context, services) =>
            {
                // Register platform services (includes DB, IUnitOfWork, etc.)
                services.AddPlatformServices(context.Configuration);

                // Allow each hosting module to register its own services.
                // This is the extensibility point for Client.ModuleHost and future modules.
                foreach (var module in _hostingModules)
                {
                    module.RegisterServices(context, services);
                }
            });
    }
}
