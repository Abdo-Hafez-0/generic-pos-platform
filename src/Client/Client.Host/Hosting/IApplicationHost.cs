namespace Client.Host.Hosting;

/// <summary>
/// Represents the application host lifecycle.
/// The WPF Desktop shell starts and stops the host through this abstraction.
///
/// Responsibilities:
/// - Start up the hosted services (configuration, logging, infrastructure, module system)
/// - Provide the service provider to the UI layer for dependency resolution
/// - Coordinate graceful shutdown when the application closes
/// </summary>
public interface IApplicationHost
{
    /// <summary>
    /// Starts the host. Must be called before the WPF window is shown.
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the host gracefully. Must be called when the application exits.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// The root service provider, available after StartAsync completes.
    /// Used by the UI layer to resolve registered services.
    /// DO NOT use this as a service locator inside business logic.
    /// Only the composition root (App.xaml.cs) should resolve top-level services from here.
    /// </summary>
    IServiceProvider Services { get; }
}
