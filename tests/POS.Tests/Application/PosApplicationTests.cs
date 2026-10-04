using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Commands;
using POS.Application.Queries;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using POS.Infrastructure.Persistence;

namespace POS.Tests.Application;

/// <summary>
/// Integration-style tests of the POS handlers against in-memory SQLite.
/// Catalog, Inventory and Sales are stubs of their Contracts.
/// </summary>
public sealed class PosApplicationTests
{
    private static readonly Guid Warehouse = Guid.NewGuid();

    private sealed record Ctx(PosTestDatabase Db, Guid SessionId, Guid CartId);

    private static async Task<Ctx> StartAsync(PosTestDatabase db)
    {
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await service.OpenSessionAsync("cashier", Warehouse);
        Assert.True(session.IsSuccess);
        var cart = await service.StartCartAsync(session.SessionId);
        Assert.True(cart.IsSuccess);
        return new Ctx(db, session.SessionId, cart.CartId);
    }

    private static async Task<POSCartResult> CartAsync(PosTestDatabase db, Guid cartId)
    {
        using var scope = db.CreateScope();
        var cart = await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cartId);
        Assert.NotNull(cart);
        return cart!;
    }

    private static async Task<POSAddItemResult> AddAsync(PosTestDatabase db, Guid cartId, string code, decimal qty = 1m)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>().AddProductAsync(cartId, code, qty);
    }

    private static (Guid Id, string Sku) Product(PosTestDatabase db, string sku, decimal price = 10m, decimal stock = 100m, string? barcode = null)
    {
        var id = db.Catalog.Register(sku, "Product " + sku, price, barcode);
        db.Inventory.SetStock(id, stock);
        return (id, sku);
    }

    // --- Sessions ---

    [Fact]
    public async Task OpenSession_PersistsOpenSession()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);

        using var scope = db.CreateScope();
        var session = await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetSessionAsync(ctx.SessionId);

        Assert.NotNull(session);
        Assert.Equal(POSSessionStatusContract.Open, session!.Status);
        Assert.Equal("cashier", session.CashierReference);
        Assert.Equal(Warehouse, session.WarehouseId);
    }

    [Fact]
    public async Task OpenSession_Invalid_Fails_AndPersistsNothing()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IPOSService>().OpenSessionAsync(" ", Warehouse);

        Assert.False(result.IsSuccess);
        Assert.Equal("POS.Session.CashierRequired", result.ErrorCode);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<POSDbContext>().Sessions.CountAsync());
    }

    [Fact]
    public async Task CloseSession_EmptyCart_Succeeds()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IPOSService>().CloseSessionAsync(ctx.SessionId);

        Assert.True(result.IsSuccess);
        var session = await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetSessionAsync(ctx.SessionId);
        Assert.Equal(POSSessionStatusContract.Closed, session!.Status);
        Assert.NotNull(session.ClosedAt);
    }

    [Fact]
    public async Task CloseSession_WithItemsInCart_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var p = Product(db, "A");
        Assert.True((await AddAsync(db, ctx.CartId, p.Sku)).IsSuccess);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IPOSService>().CloseSessionAsync(ctx.SessionId);

        Assert.False(result.IsSuccess);
        Assert.Equal("POS.CloseSession.OpenCartHasItems", result.ErrorCode);
    }

    [Fact]
    public async Task CloseSession_Twice_Or_Unknown_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();

        Assert.True((await service.CloseSessionAsync(ctx.SessionId)).IsSuccess);
        Assert.Equal("POS.Session.AlreadyClosed", (await service.CloseSessionAsync(ctx.SessionId)).ErrorCode);
        Assert.Equal("POS.CloseSession.SessionNotFound", (await service.CloseSessionAsync(Guid.NewGuid())).ErrorCode);
    }

    // --- Carts ---

    [Fact]
    public async Task StartCart_IsIdempotent_ForOpenCart()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        using var scope = db.CreateScope();

        var again = await scope.ServiceProvider.GetRequiredService<IPOSService>().StartCartAsync(ctx.SessionId);

        Assert.True(again.IsSuccess);
        Assert.Equal(ctx.CartId, again.CartId);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<POSDbContext>().Carts.CountAsync());
    }

    [Fact]
    public async Task StartCart_UnknownOrClosedSession_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        await service.CloseSessionAsync(ctx.SessionId);

        Assert.Equal("POS.StartCart.SessionNotFound", (await service.StartCartAsync(Guid.NewGuid())).ErrorCode);
        Assert.Equal("POS.StartCart.SessionNotOpen", (await service.StartCartAsync(ctx.SessionId)).ErrorCode);
    }

    [Fact]
    public async Task GetCurrentCart_ReturnsOpenCart_OrNull()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        using var scope = db.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IPOSReader>();

        var current = await reader.GetCurrentCartAsync(ctx.SessionId);
        Assert.Equal(ctx.CartId, current!.CartId);
        Assert.Null(await reader.GetCurrentCartAsync(Guid.NewGuid()));
        Assert.Null(await reader.GetCartAsync(Guid.NewGuid()));
        Assert.Null(await reader.GetSessionAsync(Guid.NewGuid()));
    }

    // --- Add product ---

    [Fact]
    public async Task AddProduct_BySku_SnapshotsNameSkuAndPrice()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var p = Product(db, "SKU-1", price: 12.5m);

        var add = await AddAsync(db, ctx.CartId, "SKU-1", 2m);

        Assert.True(add.IsSuccess);
        var cart = await CartAsync(db, ctx.CartId);
        var line = Assert.Single(cart.Items);
        Assert.Equal(p.Id, line.ProductId);
        Assert.Equal("SKU-1", line.ProductSku);
        Assert.Equal("Product SKU-1", line.ProductName);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(12.5m, line.UnitPrice);
        Assert.Equal(25m, line.LineTotal);
        Assert.Equal(25m, cart.Subtotal);
        Assert.Equal(25m, cart.Total);
    }

    [Fact]
    public async Task AddProduct_ByBarcode_ResolvesProduct()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var p = Product(db, "SKU-B", barcode: "123456789");

        var add = await AddAsync(db, ctx.CartId, "123456789");

        Assert.True(add.IsSuccess);
        Assert.Equal(p.Id, Assert.Single((await CartAsync(db, ctx.CartId)).Items).ProductId);
    }

    [Fact]
    public async Task AddProduct_UsesSessionWarehouseForStockCheck()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");

        await AddAsync(db, ctx.CartId, "A");

        Assert.Equal([Warehouse], db.Inventory.AvailabilityWarehouses.Distinct().ToArray());
    }

    [Fact]
    public async Task AddProduct_SameProductTwice_MergesLine_AndChecksTotalStock()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A", stock: 5m);

        Assert.True((await AddAsync(db, ctx.CartId, "A", 3m)).IsSuccess);
        var second = await AddAsync(db, ctx.CartId, "A", 3m); // 3 + 3 > 5

        Assert.False(second.IsSuccess);
        Assert.Equal("POS.AddProduct.InsufficientStock", second.ErrorCode);
        Assert.Equal(3m, Assert.Single((await CartAsync(db, ctx.CartId)).Items).Quantity);

        Assert.True((await AddAsync(db, ctx.CartId, "A", 2m)).IsSuccess);
        Assert.Equal(5m, Assert.Single((await CartAsync(db, ctx.CartId)).Items).Quantity);
    }

    [Fact]
    public async Task AddProduct_UnknownProduct_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);

        var add = await AddAsync(db, ctx.CartId, "NOPE");

        Assert.Equal("POS.AddProduct.ProductNotFound", add.ErrorCode);
    }

    [Fact]
    public async Task AddProduct_InactiveProduct_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var id = db.Catalog.Register("OLD", status: Catalog.Contracts.Models.ProductStatusContract.Inactive);
        db.Inventory.SetStock(id, 10m);

        Assert.Equal("POS.AddProduct.ProductInactive", (await AddAsync(db, ctx.CartId, "OLD")).ErrorCode);
    }

    [Fact]
    public async Task AddProduct_NoStock_Fails_AndCartUnchanged()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A", stock: 0m);

        var add = await AddAsync(db, ctx.CartId, "A");

        Assert.Equal("POS.AddProduct.InsufficientStock", add.ErrorCode);
        Assert.Empty((await CartAsync(db, ctx.CartId)).Items);
    }

    [Theory]
    [InlineData("", 1, "POS.AddProduct.CodeRequired")]
    [InlineData("A", 0, "POS.CartQuantity.MustBePositive")]
    [InlineData("A", -1, "POS.CartQuantity.MustBePositive")]
    public async Task AddProduct_InvalidInput_Fails(string code, double qty, string expected)
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");

        Assert.Equal(expected, (await AddAsync(db, ctx.CartId, code, (decimal)qty)).ErrorCode);
    }

    [Fact]
    public async Task AddProduct_UnknownCart_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        Product(db, "A");

        Assert.Equal("POS.AddProduct.CartNotFound", (await AddAsync(db, Guid.NewGuid(), "A")).ErrorCode);
    }

    [Fact]
    public async Task AddProduct_ClosedSession_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        using (var scope = db.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPOSService>().CloseSessionAsync(ctx.SessionId);

        Assert.Equal("POS.AddProduct.SessionNotOpen", (await AddAsync(db, ctx.CartId, "A")).ErrorCode);
    }

    [Fact]
    public async Task AddProduct_PriceSnapshot_IsNotAffectedByLaterCatalogPriceChange()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var p = Product(db, "A", price: 10m);
        await AddAsync(db, ctx.CartId, "A", 2m);

        db.Catalog.ChangePrice(p.Id, 99m);

        var cart = await CartAsync(db, ctx.CartId);
        Assert.Equal(10m, cart.Items[0].UnitPrice);
        Assert.Equal(20m, cart.Total);
    }

    // --- Remove / change / clear ---

    [Fact]
    public async Task RemoveProduct_RemovesLine_AndTotalsUpdate()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var a = Product(db, "A", price: 5m);
        Product(db, "B", price: 7m);
        await AddAsync(db, ctx.CartId, "A");
        await AddAsync(db, ctx.CartId, "B");
        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSService>()
                .RemoveProductAsync(ctx.CartId, a.Id)).IsSuccess);

        var cart = await CartAsync(db, ctx.CartId);
        Assert.Equal("B", Assert.Single(cart.Items).ProductSku);
        Assert.Equal(7m, cart.Total);
    }

    [Fact]
    public async Task RemoveProduct_NotInCart_Or_UnknownCart_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();

        Assert.Equal("POS.Cart.ItemNotFound", (await service.RemoveProductAsync(ctx.CartId, Guid.NewGuid())).ErrorCode);
        Assert.Equal("POS.RemoveProduct.CartNotFound", (await service.RemoveProductAsync(Guid.NewGuid(), Guid.NewGuid())).ErrorCode);
    }

    [Fact]
    public async Task ChangeQuantity_UpdatesLine_AndValidatesStock()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var p = Product(db, "A", price: 4m, stock: 10m);
        await AddAsync(db, ctx.CartId, "A");
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();

        Assert.True((await service.ChangeQuantityAsync(ctx.CartId, p.Id, 6m)).IsSuccess);
        var tooMany = await service.ChangeQuantityAsync(ctx.CartId, p.Id, 11m);
        var zero = await service.ChangeQuantityAsync(ctx.CartId, p.Id, 0m);

        Assert.Equal("POS.ChangeQuantity.InsufficientStock", tooMany.ErrorCode);
        Assert.Equal("POS.CartQuantity.MustBePositive", zero.ErrorCode);
        var cart = await CartAsync(db, ctx.CartId);
        Assert.Equal(6m, cart.Items[0].Quantity);
        Assert.Equal(24m, cart.Total);
    }

    [Fact]
    public async Task ChangeQuantity_ItemOrCartMissing_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();

        Assert.Equal("POS.Cart.ItemNotFound", (await service.ChangeQuantityAsync(ctx.CartId, Guid.NewGuid(), 1m)).ErrorCode);
        Assert.Equal("POS.ChangeQuantity.CartNotFound", (await service.ChangeQuantityAsync(Guid.NewGuid(), Guid.NewGuid(), 1m)).ErrorCode);
    }

    [Fact]
    public async Task ClearCart_RemovesAllItems()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A"); Product(db, "B");
        await AddAsync(db, ctx.CartId, "A");
        await AddAsync(db, ctx.CartId, "B");
        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSService>().ClearCartAsync(ctx.CartId)).IsSuccess);

        var cart = await CartAsync(db, ctx.CartId);
        Assert.Empty(cart.Items);
        Assert.Equal(0m, cart.Total);
    }

    [Fact]
    public async Task ClearCart_UnknownCart_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        Assert.Equal("POS.ClearCart.CartNotFound",
            (await scope.ServiceProvider.GetRequiredService<IPOSService>().ClearCartAsync(Guid.NewGuid())).ErrorCode);
    }

    // --- Checkout orchestration ---

    private static async Task<POSCheckoutResult> CheckoutAsync(PosTestDatabase db, Guid cartId, string? reference = null)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cartId, reference);
    }

    [Fact]
    public async Task Checkout_Success_CreatesConfirmsIssuesCompletes_InOrder()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var a = Product(db, "A", price: 10m, stock: 20m);
        var b = Product(db, "B", price: 2.5m, stock: 4m);
        await AddAsync(db, ctx.CartId, "A", 3m);
        await AddAsync(db, ctx.CartId, "B", 4m);

        var result = await CheckoutAsync(db, ctx.CartId, "TX-9");

        Assert.True(result.IsSuccess);
        Assert.Equal(db.Sales.LastSaleId, result.SaleId);
        Assert.Equal(["create", "add", "add", "confirm", "complete"], db.Sales.Calls.ToArray());
        Assert.Equal("TX-9", db.Sales.CompletedTransactionReference);
        Assert.Contains("cashier", db.Sales.CreatedNotes);

        // Sales receives the cart snapshot values and the session warehouse
        Assert.Contains(db.Sales.Items, i => i.ProductId == a.Id && i.Quantity == 3m && i.UnitPrice == 10m && i.WarehouseId == Warehouse);
        Assert.Contains(db.Sales.Items, i => i.ProductId == b.Id && i.Quantity == 4m && i.UnitPrice == 2.5m);

        // Inventory reduced through its contract
        Assert.Equal(2, db.Inventory.Issued.Count);
        Assert.Equal(17m, db.Inventory.OnHand(a.Id));
        Assert.Equal(0m, db.Inventory.OnHand(b.Id));
        Assert.All(db.Inventory.Issued, i => Assert.Equal(Warehouse, i.WarehouseId));

        // POS records the outcome
        var cart = await CartAsync(db, ctx.CartId);
        Assert.Equal(POSCartStatusContract.CheckedOut, cart.Status);
        Assert.Equal(result.SaleId, cart.SaleId);
        Assert.NotNull(cart.CheckedOutAt);
    }

    [Fact]
    public async Task Checkout_PassesSnapshotPrice_EvenIfCatalogPriceChanged()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var a = Product(db, "A", price: 10m);
        await AddAsync(db, ctx.CartId, "A", 1m);
        db.Catalog.ChangePrice(a.Id, 50m);

        Assert.True((await CheckoutAsync(db, ctx.CartId)).IsSuccess);

        Assert.Equal(10m, Assert.Single(db.Sales.Items).UnitPrice);
    }

    [Fact]
    public async Task Checkout_AfterCheckout_NewCartCanBeStarted_AndOldCartIsNotEditable()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        await AddAsync(db, ctx.CartId, "A");
        await CheckoutAsync(db, ctx.CartId);

        Assert.Equal("POS.AddProduct.CartNotOpen", (await AddAsync(db, ctx.CartId, "A")).ErrorCode);
        Assert.Equal("POS.Checkout.CartNotOpen", (await CheckoutAsync(db, ctx.CartId)).ErrorCode);

        using var scope = db.CreateScope();
        var next = await scope.ServiceProvider.GetRequiredService<IPOSService>().StartCartAsync(ctx.SessionId);
        Assert.True(next.IsSuccess);
        Assert.NotEqual(ctx.CartId, next.CartId);
    }

    [Fact]
    public async Task Checkout_EmptyOrUnknownCart_Fails_WithoutCallingOtherModules()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);

        Assert.Equal("POS.Checkout.CartEmpty", (await CheckoutAsync(db, ctx.CartId)).ErrorCode);
        Assert.Equal("POS.Checkout.CartNotFound", (await CheckoutAsync(db, Guid.NewGuid())).ErrorCode);
        Assert.Empty(db.Sales.Calls);
        Assert.Empty(db.Inventory.Issued);
    }

    [Fact]
    public async Task Checkout_ClosedSession_Fails()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        await AddAsync(db, ctx.CartId, "A");
        // Close the session directly through the DbContext (the service refuses while items exist).
        using (var scope = db.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<POSDbContext>();
            var session = await dbContext.Sessions.SingleAsync();
            session.Close();
            await dbContext.SaveChangesAsync();
        }

        var result = await CheckoutAsync(db, ctx.CartId);

        Assert.Equal("POS.Checkout.SessionNotOpen", result.ErrorCode);
        Assert.Empty(db.Sales.Calls);
    }

    [Fact]
    public async Task Checkout_StockGone_Fails_BeforeTouchingSales()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        var a = Product(db, "A", stock: 5m);
        await AddAsync(db, ctx.CartId, "A", 5m);
        db.Inventory.SetStock(a.Id, 2m); // someone else sold it meanwhile

        var result = await CheckoutAsync(db, ctx.CartId);

        Assert.Equal("POS.Checkout.InsufficientStock", result.ErrorCode);
        Assert.Empty(db.Sales.Calls);
        Assert.Equal(POSCartStatusContract.Open, (await CartAsync(db, ctx.CartId)).Status);
    }

    [Fact]
    public async Task Checkout_SalesCreateFails_ReturnsFailure_CartStaysOpen()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        await AddAsync(db, ctx.CartId, "A");
        db.Sales.FailAt = "create";

        var result = await CheckoutAsync(db, ctx.CartId);

        Assert.False(result.IsSuccess);
        Assert.Equal("POS.Checkout.CreateSaleFailed", result.ErrorCode);
        Assert.Equal(["create"], db.Sales.Calls.ToArray());
        Assert.Empty(db.Inventory.Issued);
        Assert.Equal(POSCartStatusContract.Open, (await CartAsync(db, ctx.CartId)).Status);
    }

    [Fact]
    public async Task Checkout_SalesAddItemFails_CancelsSale_NoStockIssued()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        await AddAsync(db, ctx.CartId, "A");
        db.Sales.FailAt = "add";

        var result = await CheckoutAsync(db, ctx.CartId);

        Assert.Equal("POS.Checkout.AddItemFailed", result.ErrorCode);
        Assert.Contains("cancel", db.Sales.Calls);
        Assert.DoesNotContain("confirm", db.Sales.Calls);
        Assert.Empty(db.Inventory.Issued);
        Assert.Equal(POSCartStatusContract.Open, (await CartAsync(db, ctx.CartId)).Status);
    }

    [Fact]
    public async Task Checkout_SalesConfirmFails_CancelsSale_NoStockIssued()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        await AddAsync(db, ctx.CartId, "A");
        db.Sales.FailAt = "confirm";

        var result = await CheckoutAsync(db, ctx.CartId);

        Assert.Equal("POS.Checkout.ConfirmFailed", result.ErrorCode);
        Assert.Contains("cancel", db.Sales.Calls);
        Assert.Empty(db.Inventory.Issued);
    }

    [Fact]
    public async Task Checkout_StockIssueFails_CancelsSale_CartStaysOpen_AndWarnsAboutPartialIssue()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        var b = Product(db, "B");
        await AddAsync(db, ctx.CartId, "A");
        await AddAsync(db, ctx.CartId, "B");
        db.Inventory.FailIssueFor(b.Id);

        var result = await CheckoutAsync(db, ctx.CartId);

        Assert.False(result.IsSuccess);
        Assert.Equal("POS.Checkout.StockIssueFailed", result.ErrorCode);
        Assert.Contains("cancel", db.Sales.Calls);
        Assert.DoesNotContain("complete", db.Sales.Calls);
        Assert.Contains("manual stock correction", result.ErrorMessage);
        Assert.Equal(POSCartStatusContract.Open, (await CartAsync(db, ctx.CartId)).Status);
    }

    [Fact]
    public async Task Checkout_SaleCompleteFails_LeavesCartOpen_AndSaysSaleStaysConfirmed()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A");
        await AddAsync(db, ctx.CartId, "A");
        db.Sales.FailAt = "complete";

        var result = await CheckoutAsync(db, ctx.CartId);

        Assert.Equal("POS.Checkout.CompleteSaleFailed", result.ErrorCode);
        Assert.Contains("Confirmed", result.ErrorMessage);
        Assert.DoesNotContain("cancel", db.Sales.Calls);
        Assert.Equal(POSCartStatusContract.Open, (await CartAsync(db, ctx.CartId)).Status);
    }

    // --- Query handlers directly ---

    [Fact]
    public async Task QueryHandlers_ReturnReadModels()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var ctx = await StartAsync(db);
        Product(db, "A", price: 3m);
        await AddAsync(db, ctx.CartId, "A", 2m);
        using var scope = db.CreateScope();

        var cart = await scope.ServiceProvider.GetRequiredService<GetCartQueryHandler>().HandleAsync(new GetCartQuery(ctx.CartId));
        var current = await scope.ServiceProvider.GetRequiredService<GetCurrentCartQueryHandler>().HandleAsync(new GetCurrentCartQuery(ctx.SessionId));
        var session = await scope.ServiceProvider.GetRequiredService<GetPosSessionQueryHandler>().HandleAsync(new GetPosSessionQuery(ctx.SessionId));

        Assert.Equal(6m, cart!.Total);
        Assert.Equal(cart.CartId, current!.CartId);
        Assert.Equal(ctx.SessionId, session!.SessionId);
    }
}
