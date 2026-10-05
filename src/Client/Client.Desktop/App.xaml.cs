using Catalog.Infrastructure.Module;
using Inventory.Infrastructure.Module;
using Client.Licensing.Http;
using Client.Licensing.Infrastructure;
using Client.Updater.Http;
using Client.Updater.Infrastructure;
using POS.Infrastructure.Module;
using CashManagement.Infrastructure.Module;
using Audit.Infrastructure.Module;
using Users.Infrastructure.Module;
using Payments.Infrastructure.Module;
using Pricing.Infrastructure.Module;
using Purchasing.Infrastructure.Module;
using Suppliers.Infrastructure.Module;
using Customers.Infrastructure.Module;
using Sales.Infrastructure.Module;
using Client.Host.Hosting;
using Client.ModuleHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Windows;

namespace Client.Desktop;

/// <summary>
/// Application entry point and composition root for the WPF desktop shell.
///
/// Startup sequence:
///   1. WPF Application.OnStartup fires
///   2. Create the application host via ApplicationHostBuilder
///   3. Register hosting modules (Client.ModuleHost first; future modules added here in Stage 5+)
///   4. Build the host
///   5. Start the host asynchronously (DI, logging, config, services are initialized)
///   6. Resolve the main window from DI
///   7. Show the main window
///   8. On exit: stop the host gracefully
///
/// IMPORTANT rules for this class:
/// - Do NOT put business logic here.
/// - Do NOT access databases here.
/// - Do NOT query business data here.
/// - This class only creates the host and shows the main window.
/// - The main window itself must also contain no business logic.
/// </summary>
public partial class App : Application
{
    private IApplicationHost? _applicationHost;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // Build the application host.
            // Client.ModuleHost registers module discovery services.
            // Future business modules will add their own IHostingModule instances here in Stage 5+.
            _applicationHost = ApplicationHostBuilder
                .Create()
                .WithModule(new DesktopServicesRegistrar())
                .WithModule(new ModuleHostRegistrar())
                .WithModule(new LicensingHostingModule())       // Stage 6: offline license evaluation
                .WithModule(new LicenseHttpHostingModule())     // Stage 6: HTTP transport to the license server
                .WithModule(new UpdaterHostingModule())         // Stage 7: update verification, staging, recovery (local only)
                .WithModule(new UpdateHttpHostingModule())      // Stage 7: HTTP transport to the update server
                .WithModule(new CatalogHostingModule())  // Stage 5A: Catalog module
                .WithModule(new InventoryHostingModule()) // Stage 5B: Inventory module
                .WithModule(new SalesHostingModule())     // Stage 5C: Sales module
                .WithModule(new POSHostingModule())       // Stage 5D: POS module
                .WithModule(new CashManagementHostingModule())       // Stage 8: CashManagement module
                .WithModule(new AuditHostingModule())       // Stage 8: Audit module
                .WithModule(new UsersHostingModule())       // Stage 8: Users module
                .WithModule(new PaymentsHostingModule())       // Stage 8: Payments module
                .WithModule(new PricingHostingModule())       // Stage 8: Pricing module
                .WithModule(new PurchasingHostingModule())       // Stage 8: Purchasing module
                .WithModule(new SuppliersHostingModule())       // Stage 8: Suppliers module
                .WithModule(new CustomersHostingModule())       // Stage 8: Customers module
                .Build();

            // Start all hosted services (logging, configuration, module discovery, etc.)
            await _applicationHost.StartAsync();

            // Log successful startup.
            var logger = _applicationHost.Services.GetRequiredService<ILogger<App>>();
            logger.LogInformation(
                "GenericPOS application started. Environment: {Environment}",
                Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production");

            // Create and show the main window.
            // The MainWindow is resolved from DI so it can receive services via constructor injection.
            var mainWindow = _applicationHost.Services.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            // If host startup fails, show an error and exit cleanly.
            MessageBox.Show(
                $"Application failed to start:\n\n{ex.Message}",
                "Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            Shutdown(exitCode: 1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_applicationHost is not null)
        {
            try
            {
                await _applicationHost.StopAsync();
            }
            catch (Exception ex)
            {
                // Log shutdown errors to debug output since logging may already be torn down.
                System.Diagnostics.Debug.WriteLine($"Error during host shutdown: {ex.Message}");
            }
        }

        base.OnExit(e);
    }
}
