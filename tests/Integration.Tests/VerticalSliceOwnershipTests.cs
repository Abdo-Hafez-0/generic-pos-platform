using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Catalog.Contracts.Interfaces;
using Inventory.Contracts.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Platform.Application.Abstractions.Authorization;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Pricing.Application.Commands;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 13: the first vertical slice of "Architecture &amp; Solution Design" section 82, on the production-like desktop composition
/// (every module, real SQLite, a validly signed license, the real sign-in and authorization, licensing and update components wired with their
/// HTTP transports, on a network that refuses every request), with the question Stage 12 did not ask: does the RIGHT module own each step?
///
///   Create Product (Catalog) -> Add Stock (Inventory) -> Open POS (POS) -> Find Product (Catalog contract) -> Add to Cart (POS)
///   -> Calculate Price (Pricing contract) -> Complete Sale (Sales contract) -> Process Payment (Payments contract)
///   -> Reduce Stock + Record Stock Movement (Inventory contract) -> one transaction -> audit (security events)
///
/// Each fact is read back through the OWNING module's contract, and the database is compared before and after the checkout: the checkout
/// wrote only to the tables of Sales, Payments, Inventory and POS, and every other module's tables are byte-for-byte unchanged.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class VerticalSliceOwnershipTests
{
    [Fact]
    public async Task TheMainBusinessFlow_RunsOfflineLicensedAndSignedIn_AndEachModuleOwnsItsPart()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        var currentUser = services.GetRequiredService<ICurrentUser>();
        Assert.True(currentUser.IsAuthenticated);                                               // authentication is on
        Assert.Equal(Platform.Core.Licensing.LicenseState.Active,
            services.GetRequiredService<Platform.Application.Abstractions.Licensing.ILicenseEntitlementService>().State);   // licensing is on

        // Create Product (Catalog) + Add Stock (Inventory)
        var shop = await CreateShopAsync(services, salePrice: 2.5m, stock: 10m);

        // a price list price (Pricing) that differs from the catalog price, so the price POS uses is visibly Pricing's
        using (var scope = services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            Assert.True((await sp.GetRequiredService<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand("RETAIL", "Retail"))).IsSuccess);
            Assert.True((await sp.GetRequiredService<CreatePriceCommandHandler>().HandleAsync(new CreatePriceCommand(shop.Sku, 2.0m, DateTime.UtcNow.AddDays(-1)))).IsSuccess);
        }

        // Open POS, Find Product, Add to Cart, Calculate Price
        Guid sessionId, cartId;
        using (var scope = services.CreateScope())
        {
            var sp = scope.ServiceProvider;
            var found = await sp.GetRequiredService<IProductLookup>().FindBySkuAsync(shop.Sku);    // Catalog owns the product
            Assert.Equal(shop.ProductId, found!.ProductId);

            var pos = sp.GetRequiredService<IPOSService>();
            var session = await pos.OpenSessionAsync("typed-at-the-till", shop.WarehouseId);
            Assert.True(session.IsSuccess, session.ErrorMessage);
            var cart = await pos.StartCartAsync(session.SessionId);
            Assert.True((await pos.AddProductAsync(cart.CartId, shop.Sku, 3m)).IsSuccess);
            (sessionId, cartId) = (session.SessionId, cart.CartId);

            var priced = await sp.GetRequiredService<IPOSReader>().GetCartAsync(cartId);
            var line = Assert.Single(priced!.Items);
            Assert.Equal((2.0m, 6.0m), (line.UnitPrice, priced.Total));                            // Pricing's price, not Catalog's 2.50
        }

        var before = await TableContentsAsync(desktop.Host);

        // Complete Sale + Process Payment + Reduce Stock + Record Stock Movement: one checkout, one transaction
        POSCheckoutResult checkout;
        using (var scope = services.CreateScope())
            checkout = await scope.ServiceProvider.GetRequiredService<IPOSService>()
                .CheckoutAsync(cartId, payment: new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 10m));
        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        Assert.Equal(4m, checkout.ChangeDue);

        var after = await TableContentsAsync(desktop.Host);

        // each fact, read back through the module that owns it
        using (var scope = services.CreateScope())
        {
            var sp = scope.ServiceProvider;

            var sale = await sp.GetRequiredService<ISalesReader>().FindByIdAsync(checkout.SaleId);          // Sales owns the sale
            Assert.Equal(SaleStatusContract.Completed, sale!.Status);
            Assert.Equal(6.0m, sale.GrandTotal);

            var payment = Assert.Single(await sp.GetRequiredService<IPaymentReader>().GetPaymentsForReferenceAsync("sale", checkout.SaleId));   // Payments owns the payment
            Assert.Equal((6.0m, PaymentMethodContract.Cash, 10m, PaymentStatusContract.Recorded), (payment.Amount, payment.Method, payment.TenderedAmount, payment.Status));
            Assert.Equal(checkout.PaymentId, payment.PaymentId);

            var level = await sp.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId);   // Inventory owns stock
            Assert.Equal(7m, level!.OnHand);
            var movements = await sp.GetRequiredService<IStockMovementReader>().GetRecentMovementsAsync();
            var issue = Assert.Single(movements, m => m.Quantity is -3m or 3m && m.Reference is not null && m.Reference.Contains(checkout.SaleId.ToString(), StringComparison.OrdinalIgnoreCase));
            Assert.NotEqual(Guid.Empty, issue.MovementId);

            var cart = await sp.GetRequiredService<IPOSReader>().GetCartAsync(cartId);                       // POS owns the cart and the session
            Assert.Equal((POSCartStatusContract.CheckedOut, checkout.SaleId), (cart!.Status, cart.SaleId));
            var session = await sp.GetRequiredService<IPOSReader>().GetSessionAsync(sessionId);
            Assert.Equal(currentUser.UserName, session!.CashierReference);                                     // the signed-in user, not the typed text

            var drawer = await sp.GetRequiredService<CashManagement.Contracts.Interfaces.ICashSessionReader>().GetOpenSessionAsync("MAIN");   // CashManagement owns the drawer (FIX-04)
            Assert.Equal(50m + 6.0m, drawer!.Balance);                                                         // the float plus the sale total (the change went back)

            // Audit: what the platform audits today are security events (sign-in, refusals, license and update decisions). Business actions
            // such as a completed sale are NOT audited yet - a documented deferred limitation (Stage 8 "Audit is not adopted"), not a step here.
            var signIn = await sp.GetRequiredService<IAuditReader>().QueryAsync(new AuditEntryFilter(Module: "security", Action: "security.signin.succeeded"), pageSize: 10);
            Assert.Contains(signIn.Items, e => e.ActorName == currentUser.UserName);
        }

        // ownership in the database: the checkout changed tables of exactly the five owning modules, each of them, and nothing else
        var changed = after.Keys.Where(t => !before.TryGetValue(t, out var old) || old != after[t]).ToList();
        string[] owners = ["sal_", "pay_", "inv_", "pos_", "cash_"];
        Assert.All(changed, t => Assert.Contains(owners, o => t.StartsWith(o, StringComparison.Ordinal)));
        Assert.All(owners, o => Assert.Contains(changed, t => t.StartsWith(o, StringComparison.Ordinal)));
        Assert.Contains("inv_StockMovements", changed);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());                                             // no table created or dropped

        Assert.Equal(0, desktop.Network.Requests);                                                          // offline: not one request
    }

    /// <summary>Every table's full content as text (small test databases), to compare the database before and after an operation.</summary>
    internal static async Task<Dictionary<string, string>> TableContentsAsync(IntegrationHost host)
    {
        await using var connection = await host.OpenDatabaseAsync();
        var tables = await IntegrationHost.QueryAsync(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY rowid";
            await using var reader = await command.ExecuteReaderAsync();
            var text = new System.Text.StringBuilder();
            while (await reader.ReadAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++) text.Append(reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
                text.Append('\n');
            }

            contents[table] = text.ToString();
        }

        return contents;
    }
}
