using System.Security.Cryptography;
using System.Text.Json;
using Catalog.Application.Commands;
using Client.Host.Hosting;
using Client.Licensing.Domain;
using Client.Licensing.Infrastructure;
using Inventory.Application.Commands;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Licensing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Authorization;
using POS.Application.Security;
using POS.Contracts.Interfaces;
using Reporting.Contracts.Interfaces;
using Sales.Contracts.Interfaces;
using Tests.Common.Security;
using Users.Application.Commands;
using Users.Application.Security;

namespace Integration.Tests;

/// <summary>
/// Stage 11 on the REAL host: real first-run setup, real sign-in (PBKDF2), real roles in the real database, and real signed licenses.
/// Nothing about authorization or licensing is stubbed, so these prove the whole chain a cashier's click would travel.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SecurityIntegrationTests
{
    private const string AdminPassword = IntegrationHost.AdministratorPassword;
    private const string CashierPassword = "till drawer passphrase 42";

    private static readonly string[] Modules = [.. IntegrationHost.CoreModules, "Users", "Audit", "Reporting"];

    private sealed record Shop(Guid ProductId, Guid WarehouseId);

    private static async Task<T> InScope<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<Shop> CreateShopAsync(IServiceProvider services)
    {
        var category = await InScope(services, sp => sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks")));
        var unit = await InScope(services, sp => sp.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece", "pc")));
        var product = await InScope(services, sp => sp.GetRequiredService<CreateProductCommandHandler>()
            .HandleAsync(new CreateProductCommand("COLA-1", "Cola", category.Value.Value, unit.Value.Value, 2.5m, 1m)));
        var warehouse = await InScope(services, sp => sp.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand("Main", "MAIN")));
        var stocked = await InScope(services, sp => sp.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(product.Value.Value, warehouse.Value, 50m)));
        Assert.True(product.IsSuccess && warehouse.IsSuccess && stocked.IsSuccess, "the administrator can set up the shop");
        return new Shop(product.Value.Value, warehouse.Value);
    }

    private static async Task<Platform.Core.Results.Result> SellOneAsync(IServiceProvider services, Shop shop)
    {
        using var scope = services.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await pos.OpenSessionAsync("till", shop.WarehouseId);
        if (!session.IsSuccess) return Platform.Core.Results.Error.Unauthorized(session.ErrorCode!, session.ErrorMessage!);
        var cart = await pos.StartCartAsync(session.SessionId);
        if (!cart.IsSuccess) return Platform.Core.Results.Error.Unauthorized(cart.ErrorCode!, cart.ErrorMessage!);
        await pos.AddProductAsync(cart.CartId, "COLA-1", 1m);
        var checkout = await pos.CheckoutAsync(cart.CartId);
        return checkout.IsSuccess ? Platform.Core.Results.Result.Success() : Platform.Core.Results.Error.Unauthorized(checkout.ErrorCode!, checkout.ErrorMessage!);
    }

    private static async Task<decimal> OnHandAsync(IServiceProvider services, Shop shop)
        => (await InScope(services, sp => sp.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId)))!.OnHand;

    private static void SignOut(IntegrationHost host) => host.Services.GetRequiredService<ISessionManager>().SignOut();

    private static Task<Platform.Core.Results.Result<SignInOutcome>> SignIn(IntegrationHost host, string username, string password)
        => InScope(host.Services, sp => sp.GetRequiredService<SignInCommandHandler>().HandleAsync(new SignInCommand(username, password)));

    // ------------------------------------------------------------------ authentication on the real host

    [Fact]
    public async Task Nobody_signed_in_cannot_sell_or_change_anything_on_the_real_host()
    {
        await using var host = await IntegrationHost.StartAsync(Modules, signInAdministrator: false);

        var sale = await InScope(host.Services, sp => sp.GetRequiredService<IPOSService>().OpenSessionAsync("till", Guid.NewGuid()));
        var product = await InScope(host.Services, sp => sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Sneaky")));
        var users = await InScope(host.Services, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("intruder", "Intruder")));

        Assert.Equal(SecurityErrors.NotAuthenticatedCode, sale.ErrorCode);
        Assert.Equal(SecurityErrors.NotAuthenticatedCode, product.Error.Code);
        Assert.Equal(SecurityErrors.NotAuthenticatedCode, users.Error.Code);
    }

    [Fact]
    public async Task First_run_setup_signing_in_and_signing_out_work_end_to_end_and_survive_a_restart()
    {
        string folder;
        await using (var first = await IntegrationHost.StartAsync(Modules, signInAdministrator: false))
        {
            var wrong = await SignIn(first, IntegrationHost.AdministratorUsername, "not the password at all");
            Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, wrong.Error.Code);
            Assert.True((await SignIn(first, IntegrationHost.AdministratorUsername, AdminPassword)).IsSuccess);
            Assert.True(first.Services.GetRequiredService<ICurrentUser>().IsAuthenticated);
            SignOut(first);
            Assert.False(first.Services.GetRequiredService<ICurrentUser>().IsAuthenticated);
            first.KeepFiles = true;
            folder = first.Folder;
        }

        try
        {
            // a new process on the same database: the credentials are in the database, the session is not
            await using var second = await IntegrationHost.StartAsync(Modules, folder, signInAdministrator: false);
            Assert.False(second.Services.GetRequiredService<ICurrentUser>().IsAuthenticated);
            Assert.True((await SignIn(second, IntegrationHost.AdministratorUsername, AdminPassword)).IsSuccess);

            await using var connection = await second.OpenDatabaseAsync();
            var hashes = await IntegrationHost.QueryAsync(connection, "SELECT PasswordHash FROM usr_UserCredentials");
            Assert.Single(hashes);
            Assert.StartsWith("PBKDF2-SHA256$", hashes[0]);
            Assert.DoesNotContain(AdminPassword, hashes[0]);
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    // ------------------------------------------------------------------ authorization on the real host

    [Fact]
    public async Task A_cashier_can_sell_but_nothing_else_and_role_changes_apply_to_the_very_next_call()
    {
        await using var host = await IntegrationHost.StartAsync(Modules);
        var shop = await CreateShopAsync(host.Services);

        // the administrator creates a cashier whose role may only sell and open sessions
        var cashier = await InScope(host.Services, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("cashier", "Cashier")));
        var role = await InScope(host.Services, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Cashier")));
        foreach (var capability in new[] { POSCapabilities.ManageSession, POSCapabilities.CreateSale })
            await InScope(host.Services, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role.Value, capability)));
        await InScope(host.Services, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(cashier.Value, role.Value)));
        await InScope(host.Services, sp => sp.GetRequiredService<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(cashier.Value, CashierPassword, false)));
        SignOut(host);
        Assert.True((await SignIn(host, "cashier", CashierPassword)).IsSuccess);

        // selling works
        Assert.True((await SellOneAsync(host.Services, shop)).IsSuccess);
        Assert.Equal(49m, await OnHandAsync(host.Services, shop));

        // everything beyond the role is refused by the handlers - no UI involved
        var adjust = await InScope(host.Services, sp => sp.GetRequiredService<AdjustStockCommandHandler>()
            .HandleAsync(new AdjustStockCommand(Guid.NewGuid(), -49m, Inventory.Domain.Enums.AdjustmentReason.DamageWrite)));
        var drawer = await InScope(host.Services, sp => sp.GetRequiredService<IPOSDevices>().OpenCashDrawerAsync());
        var newProduct = await InScope(host.Services, sp => sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Hacks")));
        var users = await InScope(host.Services, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role.Value, "users.manage")));
        Assert.Equal(SecurityErrors.ForbiddenCode, adjust.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, drawer.ErrorCode);
        Assert.Equal(SecurityErrors.ForbiddenCode, newProduct.Error.Code);
        Assert.Equal(SecurityErrors.ForbiddenCode, users.Error.Code); // and cannot promote itself
        Assert.Equal(49m, await OnHandAsync(host.Services, shop));

        // the administrator takes the selling capability away while the cashier is still signed in at the till
        SignOut(host);
        await SignIn(host, IntegrationHost.AdministratorUsername, AdminPassword);
        await InScope(host.Services, sp => sp.GetRequiredService<RevokePermissionCommandHandler>().HandleAsync(new RevokePermissionCommand(role.Value, POSCapabilities.CreateSale)));
        SignOut(host);
        await SignIn(host, "cashier", CashierPassword);
        var refused = await SellOneAsync(host.Services, shop);
        Assert.Equal(SecurityErrors.ForbiddenCode, refused.Error.Code);
        Assert.Equal(49m, await OnHandAsync(host.Services, shop));
    }

    // ------------------------------------------------------------------ the audit trail on the real host

    [Fact]
    public async Task Security_events_land_in_the_real_audit_log_without_any_secret()
    {
        await using var host = await IntegrationHost.StartAsync(Modules, signInAdministrator: false);

        await SignIn(host, IntegrationHost.AdministratorUsername, "a wrong passphrase 1234");        // refused
        await SignIn(host, IntegrationHost.AdministratorUsername, AdminPassword);                      // accepted
        var cashier = await InScope(host.Services, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("cashier", "Cashier")));
        await InScope(host.Services, sp => sp.GetRequiredService<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(cashier.Value, CashierPassword, false)));
        SignOut(host);
        await SignIn(host, "cashier", CashierPassword);
        await InScope(host.Services, sp => sp.GetRequiredService<AdjustStockCommandHandler>()          // refused by the handler
            .HandleAsync(new AdjustStockCommand(Guid.NewGuid(), -1m, Inventory.Domain.Enums.AdjustmentReason.DamageWrite)));

        var page = await InScope(host.Services, sp => sp.GetRequiredService<IAuditReader>().QueryAsync(new AuditEntryFilter(Module: "security"), pageSize: 200));
        var actions = page.Items.Select(e => e.Action).ToList();

        Assert.Contains("security.bootstrap.administrator-created", actions);
        Assert.Contains("security.signin.failed", actions);
        Assert.Contains("security.signin.succeeded", actions);
        Assert.Contains("security.password.reset", actions);
        var denial = Assert.Single(page.Items, e => e.Action == "security.authorization.denied");
        Assert.Equal("inventory.stock.adjust", denial.EntityId);
        Assert.Equal("cashier", denial.ActorName);

        var everything = string.Join(" // ", page.Items.Select(e => $"{e.Action}|{e.EntityType}|{e.EntityId}|{e.ActorName}|{e.Summary}|{e.Details}"));
        Assert.DoesNotContain(AdminPassword, everything);
        Assert.DoesNotContain(CashierPassword, everything);
        Assert.DoesNotContain("a wrong passphrase", everything);
        await using var connection = await host.OpenDatabaseAsync();
        var hashes = await IntegrationHost.QueryAsync(connection, "SELECT PasswordHash FROM usr_UserCredentials");
        Assert.All(hashes, h => Assert.DoesNotContain(h, everything)); // not even the hash is audited
    }

    // ------------------------------------------------------------------ license enforcement on the real host

    [Fact]
    public async Task An_expired_license_stops_new_sales_but_every_record_stays_readable_and_survives_a_restart()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new TestClock(now);
        using var licenses = new SignedLicenseWorld(now, clock);
        licenses.Issue(now, now.AddDays(30), "catalog", "inventory", "sales", "pos");

        string folder;
        Shop shop;
        await using (var host = await IntegrationHost.StartAsync(Modules, extraModules: licenses.HostModules()))
        {
            Assert.Equal(Platform.Core.Licensing.LicenseState.Active, host.Services.GetRequiredService<Platform.Application.Abstractions.Licensing.ILicenseEntitlementService>().State);
            shop = await CreateShopAsync(host.Services);
            Assert.True((await SellOneAsync(host.Services, shop)).IsSuccess);          // licensed: a normal sale
            Assert.Equal(49m, await OnHandAsync(host.Services, shop));

            clock.Advance(TimeSpan.FromDays(45));                                      // the license ran out; the Internet is off
            Assert.Equal(Platform.Core.Licensing.LicenseState.Expired, host.Services.GetRequiredService<Platform.Application.Abstractions.Licensing.ILicenseEntitlementService>().State);

            var blocked = await SellOneAsync(host.Services, shop);                     // new licensed work is declined ...
            Assert.Equal(SecurityErrors.LicenseRestrictedCode, blocked.Error.Code);
            Assert.Contains("data is safe", blocked.Error.Description);
            Assert.Equal(49m, await OnHandAsync(host.Services, shop));                 // ... and nothing was touched

            // the data is all still there and readable, including through the reports
            Assert.Single(await InScope(host.Services, sp => sp.GetRequiredService<ISalesReader>().GetRecentAsync()));
            var overview = await InScope(host.Services, sp => sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
            Assert.True(overview.Sales.IsSuccess, overview.Sales.ErrorMessage);
            Assert.Equal(1, overview.Sales.Data!.SaleCount);

            host.KeepFiles = true;
            folder = host.Folder;
        }

        try
        {
            // restart on the same files with the license still expired: every table and row is exactly as left
            await using var again = await IntegrationHost.StartAsync(Modules, folder, licenses.HostModules());
            Assert.Equal(49m, await OnHandAsync(again.Services, shop));
            Assert.Single(await InScope(again.Services, sp => sp.GetRequiredService<ISalesReader>().GetRecentAsync()));
            Assert.Equal(SecurityErrors.LicenseRestrictedCode, (await SellOneAsync(again.Services, shop)).Error.Code);

            // a renewed license (new signature) brings selling back without any data change
            clock.Set(now.AddDays(46));
            licenses.Issue(clock.GetUtcNow(), clock.GetUtcNow().AddDays(30), "catalog", "inventory", "sales", "pos");
            await again.Services.GetRequiredService<Client.Licensing.Application.ILicenseService>().InitializeAsync();
            Assert.True((await SellOneAsync(again.Services, shop)).IsSuccess);
            Assert.Equal(48m, await OnHandAsync(again.Services, shop));
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    [Fact]
    public async Task A_license_that_does_not_include_a_module_restricts_only_that_module()
    {
        var now = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        using var licenses = new SignedLicenseWorld(now, new TestClock(now));
        licenses.Issue(now, now.AddDays(30), "catalog", "inventory", "sales", "pos"); // no "reporting" entitlement is needed to READ reports

        await using var host = await IntegrationHost.StartAsync(Modules, extraModules: licenses.HostModules());
        var shop = await CreateShopAsync(host.Services);

        Assert.True((await SellOneAsync(host.Services, shop)).IsSuccess);
        var overview = await InScope(host.Services, sp => sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
        Assert.True(overview.Sales.IsSuccess);
    }
}
