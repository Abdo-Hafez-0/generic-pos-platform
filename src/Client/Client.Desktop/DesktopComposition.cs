using Audit.Infrastructure.Module;
using CashManagement.Infrastructure.Module;
using Catalog.Infrastructure.Module;
using Client.Desktop.Shell;
using Client.Hardware;
using Client.Hardware.Scanner;
using Client.Host.Hosting;
using Client.Licensing.Http;
using Client.Licensing.Infrastructure;
using Client.ModuleHost;
using Client.Security;
using Client.Updater.Http;
using Client.Updater.Infrastructure;
using Customers.Infrastructure.Module;
using Inventory.Infrastructure.Module;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Payments.Infrastructure.Module;
using POS.Infrastructure.Module;
using Pricing.Infrastructure.Module;
using Purchasing.Infrastructure.Module;
using Reporting.Infrastructure.Module;
using Sales.Infrastructure.Module;
using Suppliers.Infrastructure.Module;
using Users.Infrastructure.Module;

namespace Client.Desktop;

/// <summary>
/// The hosting modules the desktop is composed of, in registration order. <see cref="App"/> builds the host from this list, and UI.Tests
/// checks the same list (FIX-01a), so a test never composes a desktop that differs from the real one.
/// </summary>
public static class DesktopComposition
{
    public static IReadOnlyList<IHostingModule> HostingModules() =>
    [
        new DesktopServicesRegistrar(),
        new ModuleHostRegistrar(),
        new ClientSecurityHostingModule(),  // Stage 11: operating-system data protection (DPAPI) for local security state
        new LicensingHostingModule(),       // Stage 6: offline license evaluation
        new LicenseHttpHostingModule(),     // Stage 6: HTTP transport to the license server
        new UpdaterHostingModule(),         // Stage 7: update verification, staging, recovery (local only)
        new UpdateHttpHostingModule(),      // Stage 7: HTTP transport to the update server
        new HardwareHostingModule(),        // Stage 10: optional peripherals (all "None" unless configured)
        new ScannerKeyboardHostingModule(), // FIX-02: the shell window's key presses reach the keyboard-wedge scanner
        new CatalogHostingModule(),         // Stage 5A: Catalog module
        new InventoryHostingModule(),       // Stage 5B: Inventory module
        new SalesHostingModule(),           // Stage 5C: Sales module
        new POSHostingModule(),             // Stage 5D: POS module
        new ReportingHostingModule(),       // Stage 8: Reporting module
        new CashManagementHostingModule(),  // Stage 8: CashManagement module
        new AuditHostingModule(),           // Stage 8: Audit module
        new UsersHostingModule(),           // Stage 8: Users module
        new PaymentsHostingModule(),        // Stage 8: Payments module
        new PricingHostingModule(),         // Stage 8: Pricing module
        new PurchasingHostingModule(),      // Stage 8: Purchasing module
        new SuppliersHostingModule(),       // Stage 8: Suppliers module
        new CustomersHostingModule(),       // Stage 8: Customers module
    ];
}

/// <summary>
/// FIX-02: connects the shell's key forwarding (<see cref="ScannerKeyboard"/>) to the keyboard-wedge decoder of Client.Hardware. The shell
/// never sees the hardware project: only this composition root names both sides. Without a keyboard-wedge scanner configured, the decoder
/// is a sink that ignores every key.
/// </summary>
public sealed class ScannerKeyboardHostingModule : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        => services.AddSingleton(sp => new ScannerKeyboard(sp.GetRequiredService<IKeyboardInputSink>().OnCharacter));
}
