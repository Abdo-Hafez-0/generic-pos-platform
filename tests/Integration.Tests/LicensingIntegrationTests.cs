using Catalog.Application.Commands;
using Client.Licensing.Application;
using Customers.Application.Commands;
using Inventory.Contracts.Interfaces;
using Licensing.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Licensing;
using Platform.Core.Licensing;
using POS.Contracts.Interfaces;
using Purchasing.Application.Commands;
using Reporting.Contracts.Interfaces;
using Sales.Contracts.Interfaces;
using Suppliers.Application.Commands;
using Tests.Common.Security;
using Users.Application.Commands;
using Users.Application.Security;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 13: licensing integrated with the real modules and the real security on the real host - the states Stage 11/12 did not drive
/// end to end (revoked, suspended, no license, grace period), module entitlements on WRITES, and the separation of authentication from
/// licensing. In every restricted state: the feature is unavailable, the customer's data is untouched and readable, and the customer can still
/// sign in and administer users (license-free capabilities).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class LicensingIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] Modules = [.. IntegrationHost.CoreModules, "Users", "Audit", "Reporting", "Customers", "Suppliers", "Purchasing"];
    private static readonly string[] Everything = ["catalog", "inventory", "sales", "pos", "users", "audit", "reporting", "customers", "suppliers", "purchasing"];

    private static async Task<T> InScope<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<string?> SellOneAsync(IServiceProvider services, Shop shop)
    {
        using var scope = services.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await pos.OpenSessionAsync("till", shop.WarehouseId);
        if (!session.IsSuccess) return session.ErrorCode;
        var cart = await pos.StartCartAsync(session.SessionId);
        if (!cart.IsSuccess) return cart.ErrorCode;
        var added = await pos.AddProductAsync(cart.CartId, shop.Sku, 1m);
        if (!added.IsSuccess) return added.ErrorCode;
        var checkout = await pos.CheckoutAsync(cart.CartId);
        return checkout.IsSuccess ? null : checkout.ErrorCode;
    }

    /// <summary>The database without the audit tables (a refusal is audited on purpose; nothing else may change).</summary>
    private static async Task<Dictionary<string, string>> BusinessDataAsync(IntegrationHost host)
        => (await VerticalSliceOwnershipTests.TableContentsAsync(host)).Where(t => !t.Key.StartsWith("aud_", StringComparison.Ordinal)).ToDictionary();

    public static TheoryData<LicenseStatusClaim, LicenseState> WithdrawnLicenses => new()
    {
        { LicenseStatusClaim.Revoked, LicenseState.Revoked },
        { LicenseStatusClaim.Suspended, LicenseState.Suspended }
    };

    [Theory]
    [MemberData(nameof(WithdrawnLicenses))]
    public async Task ARevokedOrSuspendedLicense_MakesLicensedWorkUnavailable_LeavesTheDataIntactAndReadable_AndAuthenticationUnaffected(LicenseStatusClaim status, LicenseState expected)
    {
        var clock = new TestClock(Now);
        using var licenses = new SignedLicenseWorld(Now, clock);
        licenses.Issue(Now, Now.AddDays(30), Everything);
        await using var host = await IntegrationHost.StartAsync(Modules, extraModules: licenses.HostModules());

        var shop = await CreateShopAsync(host.Services);
        Assert.Null(await SellOneAsync(host.Services, shop));                               // licensed: works

        // the vendor withdraws the license (it arrives signed, like a renewal would bring it)
        licenses.Issue(Now, Now.AddDays(30), status, Now.AddDays(30), Now.AddDays(30), Everything);
        await host.Services.GetRequiredService<ILicenseService>().InitializeAsync();
        Assert.Equal(expected, host.Services.GetRequiredService<ILicenseEntitlementService>().State);

        var before = await BusinessDataAsync(host);

        // feature unavailable - in every module, through the one central gate
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, await SellOneAsync(host.Services, shop));
        var category = await InScope(host.Services, sp => sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("New")));
        var customer = await InScope(host.Services, sp => sp.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand("C-1", "Customer")));
        var supplier = await InScope(host.Services, sp => sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("S-1", "Supplier")));
        Assert.All([category.Error, customer.Error, supplier.Error], e => Assert.Equal(SecurityErrors.LicenseRestrictedCode, e.Code));

        // existing customer data remains intact ...
        Assert.Equal(before, await BusinessDataAsync(host));

        // ... and readable, including the reports
        Assert.Single(await InScope(host.Services, sp => sp.GetRequiredService<ISalesReader>().GetRecentAsync()));
        Assert.Equal(9m, (await InScope(host.Services, sp => sp.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId)))!.OnHand);
        var overview = await InScope(host.Services, sp => sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
        Assert.True(overview.Sales.IsSuccess, overview.Sales.ErrorMessage);

        // authentication is not licensing: signing in still works, a wrong password is still a credentials error, and the customer can still
        // administer users (a license-free capability)
        host.Services.GetRequiredService<ISessionManager>().SignOut();
        var wrong = await InScope(host.Services, sp => sp.GetRequiredService<SignInCommandHandler>().HandleAsync(new SignInCommand(IntegrationHost.AdministratorUsername, "not the right passphrase")));
        Assert.Equal(UsersSecurityErrors.InvalidCredentialsCode, wrong.Error.Code);
        await host.SignInAdministratorAsync();
        Assert.True((await InScope(host.Services, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("cashier2", "Cashier Two")))).IsSuccess);
    }

    [Fact]
    public async Task WithNoLicenseAtAll_TheShopCanSignInAndReadButNotDoLicensedWork_AndNothingIsChanged()
    {
        var clock = new TestClock(Now);
        using var licenses = new SignedLicenseWorld(Now, clock);    // no license issued
        await using var host = await IntegrationHost.StartAsync(Modules, extraModules: licenses.HostModules());

        Assert.Equal(LicenseState.Unlicensed, host.Services.GetRequiredService<ILicenseEntitlementService>().State);
        Assert.True(host.Services.GetRequiredService<ICurrentUser>().IsAuthenticated);     // first-run setup and sign-in did not need a license

        var before = await BusinessDataAsync(host);
        var category = await InScope(host.Services, sp => sp.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Drinks")));
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, category.Error.Code);
        Assert.Equal(before, await BusinessDataAsync(host));

        var overview = await InScope(host.Services, sp => sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
        Assert.True(overview.Sales.IsSuccess, overview.Sales.ErrorMessage);
    }

    [Fact]
    public async Task InTheOfflineGracePeriod_TheShopKeepsSelling()
    {
        var clock = new TestClock(Now);
        using var licenses = new SignedLicenseWorld(Now, clock);
        licenses.Issue(Now, Now.AddDays(365), LicenseStatusClaim.Active, leaseUntil: Now.AddDays(7), graceUntil: Now.AddDays(14), Everything);
        await using var host = await IntegrationHost.StartAsync(Modules, extraModules: licenses.HostModules());
        var shop = await CreateShopAsync(host.Services);

        clock.Advance(TimeSpan.FromDays(10));    // the lease could not be renewed (no Internet) but the grace period still runs

        Assert.Equal(LicenseState.GracePeriod, host.Services.GetRequiredService<ILicenseEntitlementService>().State);
        Assert.Null(await SellOneAsync(host.Services, shop));
    }

    [Fact]
    public async Task ModuleEntitlementsAreIsolated_AnUnlicensedModuleIsRefused_WhileTheLicensedOnesKeepWorking()
    {
        var clock = new TestClock(Now);
        using var licenses = new SignedLicenseWorld(Now, clock);
        licenses.Issue(Now, Now.AddDays(30), "catalog", "inventory", "sales", "pos", "users", "audit", "suppliers");   // no purchasing, no customers
        await using var host = await IntegrationHost.StartAsync(Modules, extraModules: licenses.HostModules());
        var shop = await CreateShopAsync(host.Services);

        var supplier = await InScope(host.Services, sp => sp.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("S-1", "Supplier")));
        Assert.True(supplier.IsSuccess, supplier.IsFailure ? supplier.Error.ToString() : null);

        var order = await InScope(host.Services, sp => sp.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier.Value)));
        var customer = await InScope(host.Services, sp => sp.GetRequiredService<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand("C-1", "Customer")));
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, order.Error.Code);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, customer.Error.Code);

        Assert.Null(await SellOneAsync(host.Services, shop));    // the licensed modules are unaffected
        Assert.Equal(0, await CountAsync(host, "pur_PurchaseOrders"));
        Assert.Equal(0, await CountAsync(host, "cus_Customers"));
    }
}
