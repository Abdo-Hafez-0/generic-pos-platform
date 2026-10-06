using Client.Host.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
