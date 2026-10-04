using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sales.Application.Commands;
using Sales.Application.Queries;
using Sales.Domain.Enums;
using Sales.Infrastructure.Persistence;

namespace Sales.Tests.Application;

/// <summary>
/// Integration-style tests of the Sales command/query handlers against an in-memory SQLite database.
/// Catalog and Inventory are stubbed through their Contracts.
/// </summary>
public sealed class SalesCommandAndQueryTests
{
    private static readonly Guid ProductA = Guid.NewGuid();
    private static readonly Guid Warehouse = Guid.NewGuid();

    private static StubProductLookup LookupWithProduct()
    {
        var lookup = new StubProductLookup();
        lookup.Register(ProductA, sku: "SKU-A", name: "Product A", salePrice: 25m);
        return lookup;
    }

    private static async Task<Guid> CreateSaleAsync(SalesTestDatabase db, string? reference = null)
    {
        using var scope = db.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<CreateSaleCommandHandler>()
            .HandleAsync(new CreateSaleCommand(reference, "notes"));
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static async Task<Guid> AddItemAsync(SalesTestDatabase db, Guid saleId, decimal qty = 2m, decimal price = 25m,
        decimal discount = 0m, decimal tax = 0m, Guid? warehouseId = null)
    {
        using var scope = db.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<AddSaleItemCommandHandler>()
            .HandleAsync(new AddSaleItemCommand(saleId, ProductA, qty, price, discount, tax, warehouseId));
        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private static async Task<Sales.Application.DTOs.SaleDto?> GetAsync(SalesTestDatabase db, Guid saleId)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GetSaleByIdQueryHandler>()
            .HandleAsync(new GetSaleByIdQuery(saleId));
    }

    // --- CreateSale ---

    [Fact]
    public async Task CreateSale_PersistsDraftSale()
    {
        await using var db = await SalesTestDatabase.CreateAsync();

        var id = await CreateSaleAsync(db, "REF-1");

        var dto = await GetAsync(db, id);
        Assert.NotNull(dto);
        Assert.Equal(SaleStatus.Draft, dto!.Status);
        Assert.Equal("REF-1", dto.Reference);
        Assert.Empty(dto.Items);
    }

    [Fact]
    public async Task CreateSale_InvalidReference_Fails_AndPersistsNothing()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<CreateSaleCommandHandler>()
            .HandleAsync(new CreateSaleCommand(new string('x', 101)));

        Assert.True(result.IsFailure);
        Assert.Equal("Sales.Sale.ReferenceTooLong", result.Error.Code);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SalesDbContext>().Sales.CountAsync());
    }

    // --- AddSaleItem ---

    [Fact]
    public async Task AddSaleItem_SnapshotsProductAndPricingValues()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var saleId = await CreateSaleAsync(db);

        await AddItemAsync(db, saleId, qty: 4m, price: 30m, discount: 20m, tax: 0.1m);

        var dto = (await GetAsync(db, saleId))!;
        var item = Assert.Single(dto.Items);
        Assert.Equal(ProductA, item.CatalogProductId);
        Assert.Equal("Product A", item.ProductName);
        Assert.Equal("SKU-A", item.ProductSku);
        Assert.Equal(4m, item.Quantity);
        Assert.Equal(30m, item.UnitPrice);   // supplied price, NOT the catalog's current 25
        Assert.Equal(20m, item.Discount);
        Assert.Equal(0.1m, item.TaxRate);
        Assert.Equal(100m, item.SubTotal);
        Assert.Equal(10m, item.TaxAmount);
        Assert.Equal(110m, item.LineTotal);
        Assert.Equal(110m, dto.GrandTotal);
        Assert.Equal(10m, dto.TaxTotal);
    }

    [Fact]
    public async Task AddSaleItem_SaleNotFound_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AddSaleItemCommandHandler>()
            .HandleAsync(new AddSaleItemCommand(Guid.NewGuid(), ProductA, 1m, 1m));

        Assert.Equal("Sales.AddSaleItem.SaleNotFound", result.Error.Code);
    }

    [Fact]
    public async Task AddSaleItem_UnknownProduct_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync(); // no products registered
        var saleId = await CreateSaleAsync(db);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AddSaleItemCommandHandler>()
            .HandleAsync(new AddSaleItemCommand(saleId, Guid.NewGuid(), 1m, 1m));

        Assert.Equal("Sales.AddSaleItem.ProductNotFound", result.Error.Code);
    }

    [Fact]
    public async Task AddSaleItem_InsufficientStock_Fails_WhenWarehouseSpecified()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct(), new StubStockAvailabilityChecker(false));
        var saleId = await CreateSaleAsync(db);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AddSaleItemCommandHandler>()
            .HandleAsync(new AddSaleItemCommand(saleId, ProductA, 1m, 10m, WarehouseId: Warehouse));

        Assert.Equal("Sales.AddSaleItem.InsufficientStock", result.Error.Code);
        Assert.Empty((await GetAsync(db, saleId))!.Items);
    }

    [Fact]
    public async Task AddSaleItem_StockNotChecked_WhenNoWarehouse()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct(), new StubStockAvailabilityChecker(false));
        var saleId = await CreateSaleAsync(db);

        await AddItemAsync(db, saleId); // would fail if stock were checked

        Assert.Single((await GetAsync(db, saleId))!.Items);
    }

    [Fact]
    public async Task AddSaleItem_StockAvailable_Succeeds_WhenWarehouseSpecified()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct(), new StubStockAvailabilityChecker(true));
        var saleId = await CreateSaleAsync(db);

        await AddItemAsync(db, saleId, warehouseId: Warehouse);

        Assert.Single((await GetAsync(db, saleId))!.Items);
    }

    [Theory]
    [InlineData(0, 10, "Sales.SaleQuantity.MustBePositive")]
    [InlineData(-1, 10, "Sales.SaleQuantity.MustBePositive")]
    [InlineData(1, -5, "Sales.Money.NegativeAmount")]
    public async Task AddSaleItem_InvalidValues_Fail(double qty, double price, string expectedCode)
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var saleId = await CreateSaleAsync(db);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AddSaleItemCommandHandler>()
            .HandleAsync(new AddSaleItemCommand(saleId, ProductA, (decimal)qty, (decimal)price));

        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public async Task AddSaleItem_ToConfirmedSale_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var saleId = await CreateSaleAsync(db);
        await AddItemAsync(db, saleId);
        await ConfirmAsync(db, saleId);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<AddSaleItemCommandHandler>()
            .HandleAsync(new AddSaleItemCommand(saleId, ProductA, 1m, 1m));

        Assert.Equal("Sales.AddSaleItem.SaleNotDraft", result.Error.Code);
    }

    // --- ConfirmSale ---

    private static async Task ConfirmAsync(SalesTestDatabase db, Guid saleId)
    {
        using var scope = db.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<ConfirmSaleCommandHandler>()
            .HandleAsync(new ConfirmSaleCommand(saleId));
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task ConfirmSale_MovesToConfirmed()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var saleId = await CreateSaleAsync(db);
        await AddItemAsync(db, saleId);

        await ConfirmAsync(db, saleId);

        Assert.Equal(SaleStatus.Confirmed, (await GetAsync(db, saleId))!.Status);
    }

    [Fact]
    public async Task ConfirmSale_WithoutItems_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var saleId = await CreateSaleAsync(db);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ConfirmSaleCommandHandler>()
            .HandleAsync(new ConfirmSaleCommand(saleId));

        Assert.Equal("Sales.Sale.NoItems", result.Error.Code);
        Assert.Equal(SaleStatus.Draft, (await GetAsync(db, saleId))!.Status);
    }

    [Fact]
    public async Task ConfirmSale_NotFound_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<ConfirmSaleCommandHandler>()
            .HandleAsync(new ConfirmSaleCommand(Guid.NewGuid()));

        Assert.Equal("Sales.ConfirmSale.SaleNotFound", result.Error.Code);
    }

    // --- CompleteSale ---

    [Fact]
    public async Task CompleteSale_RecordsTransaction_AndMarksCompleted()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var saleId = await CreateSaleAsync(db);
        await AddItemAsync(db, saleId, qty: 2m, price: 50m, tax: 0.1m); // 100 + 10 tax
        await ConfirmAsync(db, saleId);

        Guid transactionId;
        using (var scope = db.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CompleteSaleCommandHandler>()
                .HandleAsync(new CompleteSaleCommand(saleId, "TX-1"));
            Assert.True(result.IsSuccess);
            transactionId = result.Value;
        }

        var dto = (await GetAsync(db, saleId))!;
        Assert.Equal(SaleStatus.Completed, dto.Status);
        Assert.NotNull(dto.CompletedAt);

        using var verify = db.CreateScope();
        var tx = await verify.ServiceProvider.GetRequiredService<SalesDbContext>().SalesTransactions.SingleAsync();
        Assert.Equal(transactionId, tx.Id);
        Assert.Equal(saleId, tx.SaleId.Value);
        Assert.Equal(110m, tx.GrandTotal.Amount);
        Assert.Equal(10m, tx.TaxTotal.Amount);
        Assert.Equal("TX-1", tx.Reference);
    }

    [Fact]
    public async Task CompleteSale_FromDraft_Fails_AndRecordsNoTransaction()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var saleId = await CreateSaleAsync(db);
        await AddItemAsync(db, saleId);
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<CompleteSaleCommandHandler>()
            .HandleAsync(new CompleteSaleCommand(saleId));

        Assert.Equal("Sales.Sale.CannotComplete", result.Error.Code);
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<SalesDbContext>().SalesTransactions.CountAsync());
    }

    [Fact]
    public async Task CompleteSale_NotFound_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<CompleteSaleCommandHandler>()
            .HandleAsync(new CompleteSaleCommand(Guid.NewGuid()));

        Assert.Equal("Sales.CompleteSale.SaleNotFound", result.Error.Code);
    }

    // --- CancelSale ---

    [Fact]
    public async Task CancelSale_Draft_Succeeds_AndStoresReason()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        var saleId = await CreateSaleAsync(db);
        using (var scope = db.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<CancelSaleCommandHandler>()
                .HandleAsync(new CancelSaleCommand(saleId, "customer left"));
            Assert.True(result.IsSuccess);
        }

        var dto = (await GetAsync(db, saleId))!;
        Assert.Equal(SaleStatus.Cancelled, dto.Status);
        Assert.Equal("customer left", dto.CancellationReason);
        Assert.NotNull(dto.CancelledAt);
    }

    [Fact]
    public async Task CancelSale_Completed_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var saleId = await CreateSaleAsync(db);
        await AddItemAsync(db, saleId);
        await ConfirmAsync(db, saleId);
        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<CompleteSaleCommandHandler>()
                .HandleAsync(new CompleteSaleCommand(saleId))).IsSuccess);
        using var cancelScope = db.CreateScope();

        var result = await cancelScope.ServiceProvider.GetRequiredService<CancelSaleCommandHandler>()
            .HandleAsync(new CancelSaleCommand(saleId, "too late"));

        Assert.Equal("Sales.Sale.AlreadyCompleted", result.Error.Code);
        Assert.Equal(SaleStatus.Completed, (await GetAsync(db, saleId))!.Status);
    }

    [Fact]
    public async Task CancelSale_NotFound_Fails()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<CancelSaleCommandHandler>()
            .HandleAsync(new CancelSaleCommand(Guid.NewGuid(), "x"));

        Assert.Equal("Sales.CancelSale.SaleNotFound", result.Error.Code);
    }

    // --- Queries ---

    [Fact]
    public async Task GetSaleById_Unknown_ReturnsNull()
    {
        await using var db = await SalesTestDatabase.CreateAsync();

        Assert.Null(await GetAsync(db, Guid.NewGuid()));
    }

    [Fact]
    public async Task GetAllSales_ReturnsAllSales_NewestFirst_WithItems()
    {
        await using var db = await SalesTestDatabase.CreateAsync(LookupWithProduct());
        var first = await CreateSaleAsync(db, "first");
        await Task.Delay(15);
        var second = await CreateSaleAsync(db, "second");
        await AddItemAsync(db, second);

        using var scope = db.CreateScope();
        var all = await scope.ServiceProvider.GetRequiredService<GetAllSalesQueryHandler>()
            .HandleAsync(new GetAllSalesQuery());

        Assert.Equal(2, all.Count);
        Assert.Equal(second, all[0].SaleId);
        Assert.Equal(first, all[1].SaleId);
        Assert.Single(all[0].Items);
        Assert.Empty(all[1].Items);
    }

    [Fact]
    public async Task GetAllSales_Empty_ReturnsEmptyList()
    {
        await using var db = await SalesTestDatabase.CreateAsync();
        using var scope = db.CreateScope();

        var all = await scope.ServiceProvider.GetRequiredService<GetAllSalesQueryHandler>()
            .HandleAsync(new GetAllSalesQuery());

        Assert.Empty(all);
    }
}
