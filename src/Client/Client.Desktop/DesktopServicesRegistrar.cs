using Catalog.UI.Screens;
using Client.Desktop.Screens;
using Client.Desktop.Shell;
using Inventory.UI.Screens;
using Client.Host.Hosting;
using Customers.UI.Screens;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using POS.UI.Screens;
using Sales.UI.Screens;
using Suppliers.UI.Screens;
using Users.Application.Security;

namespace Client.Desktop;

/// <summary>
/// Registers Client.Desktop WPF services into the DI container.
/// This is the Desktop shell's participation in the host composition.
///
/// Registered services:
/// - MainWindow (Transient) — WPF shell window, created once per application lifetime.
/// - SignInWindow (Transient) + a factory — the Stage 11 start screen (first-run setup / sign-in / password change).
/// - DesktopSignOut — signs out through the Users flow in its own scope.
/// - The shell (FIX-01a): IUiActionRunner (one DI scope per user action), NavigationBuilder, the WPF screen factory, ShellViewModel.
/// - The screens of the module UIs (IScreenProvider), one registration per module UI.
///
/// This registrar intentionally does NOT register business services (those belong to their modules) or database access.
/// </summary>
internal sealed class DesktopServicesRegistrar : IHostingModule
{
    public void RegisterServices(HostBuilderContext context, IServiceCollection services)
    {
        services.AddTransient<MainWindow>();
        services.AddTransient<SignInWindow>();
        services.AddSingleton<Func<SignInWindow>>(sp => () => sp.GetRequiredService<SignInWindow>());
        services.AddSingleton<DesktopSignOut>();

        // FIX-01a: the shell. Singletons only: a screen's view model lives as long as the signed-in session, and reaches scoped services
        // (handlers, contexts) per action through IUiActionRunner.
        services.AddSingleton<IUiActionRunner, UiActionRunner>();
        services.AddSingleton<NavigationBuilder>();
        services.AddSingleton<IScreenFactory, WpfScreenFactory>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<IShellNavigation>(sp => sp.GetRequiredService<ShellViewModel>());

        // Screens declared by the module UIs (IScreenProvider), one per module UI.
        services.AddSingleton<IScreenProvider, DesktopScreens>(); // license screen (FIX-01e, done first)
        services.AddSingleton<IScreenProvider, PosScreens>();   // FIX-01b
        services.AddSingleton<IScreenProvider, CatalogScreens>(); // FIX-01c
        services.AddSingleton<IScreenProvider, InventoryScreens>(); // FIX-01c
        services.AddSingleton<IScreenProvider, SalesScreens>();     // FIX-01c
        services.AddSingleton<IScreenProvider, CustomersScreens>(); // FIX-01d
        services.AddSingleton<IScreenProvider, SuppliersScreens>(); // FIX-01d
    }
}

/// <summary>Signs the current user out through <see cref="InteractiveSignInService"/> (audited), in its own DI scope.</summary>
public sealed class DesktopSignOut(IServiceScopeFactory scopes)
{
    public async Task SignOutAsync()
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InteractiveSignInService>().SignOutAsync();
    }
}
