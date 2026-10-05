using Client.Licensing.Application;
using Client.Licensing.Infrastructure;
using Client.Security;
using Client.Updater.Application;
using Client.Updater.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Licensing;
using Platform.Application.Abstractions.Security;

namespace Integration.Tests;

/// <summary>
/// The security services wired together exactly as the desktop does it (data protection, licensing, updater, users, audit and every business
/// module), resolved with the container's strictest validation. A captive dependency (a singleton holding a scoped service, for example a
/// security-event listener holding a database context) or a missing registration would fail here instead of at a customer's till.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SecurityCompositionTests
{
    [Fact]
    public async Task The_desktop_security_composition_resolves_under_strict_scope_validation()
    {
        var previous = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Development"); // the generic host validates scopes and registrations only in Development
        var licensing = Path.Combine(Path.GetTempPath(), "genericpos-lic-" + Guid.NewGuid().ToString("N"));
        var updates = Path.Combine(Path.GetTempPath(), "genericpos-upd-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("GENERICPOS_Licensing__StorageDirectory", licensing);
        Environment.SetEnvironmentVariable("GENERICPOS_Updater__StorageDirectory", updates);
        try
        {
            await using var host = await IntegrationHost.StartAllAsync(extra:
            [
                new ClientSecurityHostingModule(), new LicensingHostingModule(), new UpdaterHostingModule()
            ]);

            // singletons straight from the root provider
            var root = host.Services;
            Assert.NotNull(root.GetRequiredService<ICurrentUser>());
            Assert.NotNull(root.GetRequiredService<ISecurityEventSink>());
            Assert.NotNull(root.GetRequiredService<ICapabilityCatalog>());
            Assert.NotNull(root.GetRequiredService<ILicenseEntitlementService>());
            Assert.NotNull(root.GetRequiredService<IUpdateService>());
            Assert.NotNull(root.GetRequiredService<IClockGuard>());
            if (OperatingSystem.IsWindows())
                Assert.NotNull(root.GetRequiredService<ISecretProtector>());

            // scoped services and the handlers that depend on them
            using var scope = root.CreateScope();
            var sp = scope.ServiceProvider;
            Assert.NotNull(sp.GetRequiredService<IAuthorizationService>());
            Assert.NotNull(sp.GetRequiredService<IPermissionProvider>());
            Assert.NotNull(sp.GetRequiredService<ActivateLicenseCommandHandler>());
            Assert.NotNull(sp.GetRequiredService<InstallUpdateCommandHandler>());
            Assert.NotNull(sp.GetRequiredService<Users.Application.Commands.SignInCommandHandler>());

            // the catalog holds the capabilities of every installed module plus licensing and updates
            var catalog = root.GetRequiredService<ICapabilityCatalog>();
            foreach (var code in new[] { "pos.sale.create", "inventory.stock.adjust", "users.manage", "audit.view", "reporting.view", "licensing.manage", "updates.manage" })
                Assert.NotNull(catalog.Find(code));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previous);
            Environment.SetEnvironmentVariable("GENERICPOS_Licensing__StorageDirectory", null);
            Environment.SetEnvironmentVariable("GENERICPOS_Updater__StorageDirectory", null);
            foreach (var directory in new[] { licensing, updates })
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
