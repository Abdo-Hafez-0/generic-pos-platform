using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Client.Desktop;

/// <summary>
/// Registers Client.Desktop WPF services into the DI container.
/// This is the Desktop shell's participation in the host composition.
///
/// Registered services:
/// - MainWindow (Transient) — WPF shell window, created once per application lifetime.
///
/// This registrar intentionally does NOT register:
/// - Business services (those belong to their respective modules, Stage 5+)
/// - Database access (Stage 3)
/// - Navigation services (Stage 5, when there are screens to navigate to)
/// </summary>
internal sealed class DesktopServicesRegistrar : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        // Register the main window as Transient so it is fully managed by DI.
        // App.xaml.cs resolves it from Services after the host starts.
        services.AddTransient<MainWindow>();
    }
}
