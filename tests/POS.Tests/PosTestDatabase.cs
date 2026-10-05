using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Abstractions;
using POS.Application.Devices;
using POS.Application.Commands;
using POS.Application.Queries;
using POS.Application.Repositories;
using POS.Contracts.Interfaces;
using POS.Infrastructure.Persistence;
using POS.Infrastructure.Repositories;
using POS.Infrastructure.Services;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;

namespace POS.Tests;

/// <summary>
/// Creates a POSDbContext backed by an in-memory SQLite database for tests, using EnsureCreated
/// (schema from the EF model). Catalog, Inventory and Sales are replaced by stubs of their CONTRACTS:
/// POS tests never touch those modules' implementation assemblies.
/// </summary>
public sealed class PosTestDatabase : IAsyncDisposable
{
    private readonly ServiceProvider _serviceProvider;

    private PosTestDatabase(ServiceProvider serviceProvider, StubCatalog catalog, StubInventory inventory, StubSales sales)
    {
        _serviceProvider = serviceProvider;
        Catalog = catalog;
        Inventory = inventory;
        Sales = sales;
    }

    public StubCatalog Catalog { get; }
    public StubInventory Inventory { get; }
    public StubSales Sales { get; }

    public static async Task<PosTestDatabase> CreateAsync(
        Pricing.Contracts.Interfaces.IPriceResolver? priceResolver = null,
        Payments.Contracts.Interfaces.IPaymentService? paymentService = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var catalog = new StubCatalog();
        var inventory = new StubInventory();
        var sales = new StubSales();

        var services = new ServiceCollection();
        var dbName = $"Data Source=file:pos-test-{Guid.NewGuid():N}?mode=memory&cache=shared";

        services.AddDbContext<POSDbContext>(options =>
        {
            options.UseSqlite(dbName);
            options.EnableSensitiveDataLogging();
        });

        services.AddScoped<IPosUnitOfWork, PosUnitOfWork>();
        services.AddScoped<IPosSessionRepository, EfPosSessionRepository>();
        services.AddScoped<IPosCartRepository, EfPosCartRepository>();
        services.AddScoped<IPOSService, POSService>();
        services.AddScoped<IPOSReader, POSReader>();

        // Cross-module contracts - stubs
        services.AddSingleton<IProductLookup>(catalog);
        services.AddSingleton<IProductBarcodeResolver>(catalog);
        services.AddSingleton<IStockAvailabilityChecker>(inventory);
        services.AddSingleton<IStockIssueService>(inventory);
        services.AddSingleton<ISalesService>(sales);
        if (priceResolver is not null) services.AddSingleton(priceResolver);   // OPTIONAL integration
        if (paymentService is not null) services.AddSingleton(paymentService); // OPTIONAL integration

        // Stage 10: optional peripherals are registered by the test through configureServices (fake hardware); none by default.
        services.AddSingleton(new PosReceiptOptions { StoreName = "Test Store", FooterLines = ["Thank you"] });
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IPOSDevices, POSDevices>();
        services.AddSingleton<IPOSBarcodeInput, POSBarcodeInput>();
        services.AddTransient<PrintReceiptCommandHandler>();
        services.AddTransient<OpenCashDrawerCommandHandler>();
        services.AddTransient<PrintProductLabelCommandHandler>();
        services.AddTransient<ReadWeightQueryHandler>();
        services.AddTransient<GetDeviceStatusQueryHandler>();
        configureServices?.Invoke(services);

        services.AddTransient<OpenPosSessionCommandHandler>();
        services.AddTransient<ClosePosSessionCommandHandler>();
        services.AddTransient<StartCartCommandHandler>();
        services.AddTransient<AddProductToCartCommandHandler>();
        services.AddTransient<RemoveProductFromCartCommandHandler>();
        services.AddTransient<ChangeCartQuantityCommandHandler>();
        services.AddTransient<ClearCartCommandHandler>();
        services.AddTransient<CheckoutCartCommandHandler>();
        services.AddTransient<GetPosSessionQueryHandler>();
        services.AddTransient<GetCartQueryHandler>();
        services.AddTransient<GetCurrentCartQueryHandler>();

        var provider = services.BuildServiceProvider();

        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<POSDbContext>().Database.EnsureCreatedAsync();

        return new PosTestDatabase(provider, catalog, inventory, sales);
    }

    public IServiceScope CreateScope() => _serviceProvider.CreateScope();

    public async ValueTask DisposeAsync() => await _serviceProvider.DisposeAsync();
}

/// <summary>Stub of Catalog.Contracts (product lookup + barcode resolution).</summary>
public sealed class StubCatalog : IProductLookup, IProductBarcodeResolver
{
    private readonly Dictionary<Guid, ProductLookupResult> _products = [];
    private readonly Dictionary<string, Guid> _barcodes = [];

    public Guid Register(
        string sku,
        string name = "Test Product",
        decimal salePrice = 10m,
        string? barcode = null,
        ProductStatusContract status = ProductStatusContract.Active,
        Guid? productId = null)
    {
        var id = productId ?? Guid.NewGuid();
        _products[id] = new ProductLookupResult(
            id, sku, name, null, Guid.NewGuid(), "Cat", Guid.NewGuid(), "Each", "ea", salePrice, null, status);
        if (barcode is not null) _barcodes[barcode] = id;
        return id;
    }

    /// <summary>Simulates a Catalog price change after the product was already put in a cart.</summary>
    public void ChangePrice(Guid productId, decimal newPrice)
        => _products[productId] = _products[productId] with { SalePrice = newPrice };

    public Task<ProductLookupResult?> FindByIdAsync(Guid productId, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.TryGetValue(productId, out var p) ? p : null);

    public Task<ProductLookupResult?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.Values.FirstOrDefault(p => p.Sku == sku));

    public Task<ProductLookupResult?> ResolveAsync(string barcodeValue, CancellationToken cancellationToken = default)
        => Task.FromResult(_barcodes.TryGetValue(barcodeValue, out var id) ? _products[id] : null);
}

/// <summary>Stub of Inventory.Contracts: stock availability + stock issue.</summary>
public sealed class StubInventory : IStockAvailabilityChecker, IStockIssueService
{
    private readonly Dictionary<Guid, decimal> _onHand = [];
    private readonly HashSet<Guid> _failIssueFor = [];

    public List<(Guid ProductId, Guid WarehouseId, decimal Quantity, string? Reference)> Issued { get; } = [];
    public List<Guid> AvailabilityWarehouses { get; } = [];

    public void SetStock(Guid productId, decimal onHand) => _onHand[productId] = onHand;

    public void FailIssueFor(Guid productId) => _failIssueFor.Add(productId);

    public decimal OnHand(Guid productId) => _onHand.TryGetValue(productId, out var q) ? q : 0m;

    public Task<bool> IsAvailableAsync(Guid catalogProductId, Guid warehouseId, decimal requiredQuantity, CancellationToken cancellationToken = default)
    {
        AvailabilityWarehouses.Add(warehouseId);
        return Task.FromResult(requiredQuantity > 0 && OnHand(catalogProductId) >= requiredQuantity);
    }

    public Task<IssueStockResult> IssueStockAsync(Guid catalogProductId, Guid warehouseId, decimal quantity, string? reference = null, CancellationToken cancellationToken = default)
    {
        if (_failIssueFor.Contains(catalogProductId))
            return Task.FromResult(IssueStockResult.Failure("Inventory.IssueStock.Stub", "stub failure"));

        _onHand[catalogProductId] = OnHand(catalogProductId) - quantity;
        Issued.Add((catalogProductId, warehouseId, quantity, reference));
        return Task.FromResult(IssueStockResult.Success(Guid.NewGuid()));
    }
}

/// <summary>Stub of Sales.Contracts that records the calls POS makes. Can be told to fail at a step.</summary>
public sealed class StubSales : ISalesService
{
    public sealed record AddedItem(Guid SaleId, Guid ProductId, decimal Quantity, decimal UnitPrice, Guid? WarehouseId);

    public string? FailAt { get; set; }   // "create" | "add" | "confirm" | "complete"
    public Guid? LastSaleId { get; private set; }
    public string? CreatedReference { get; private set; }
    public string? CreatedNotes { get; private set; }
    public string? CompletedTransactionReference { get; private set; }
    public List<AddedItem> Items { get; } = [];
    public List<string> Calls { get; } = [];
    public List<string> CancelReasons { get; } = [];

    public Task<CreateSaleResult> CreateSaleAsync(string? reference = null, string? notes = null, CancellationToken cancellationToken = default)
    {
        Calls.Add("create");
        if (FailAt == "create") return Task.FromResult(CreateSaleResult.Failure("Sales.Stub", "create failed"));
        LastSaleId = Guid.NewGuid();
        CreatedReference = reference;
        CreatedNotes = notes;
        return Task.FromResult(CreateSaleResult.Success(LastSaleId.Value));
    }

    public Task<AddSaleItemResult> AddItemAsync(Guid saleId, Guid catalogProductId, decimal quantity, decimal unitPrice, decimal discount = 0, decimal taxRate = 0, Guid? warehouseId = null, CancellationToken cancellationToken = default)
    {
        Calls.Add("add");
        if (FailAt == "add") return Task.FromResult(AddSaleItemResult.Failure("Sales.Stub", "add failed"));
        Items.Add(new AddedItem(saleId, catalogProductId, quantity, unitPrice, warehouseId));
        return Task.FromResult(AddSaleItemResult.Success(Guid.NewGuid()));
    }

    public Task<SaleOperationResult> ConfirmSaleAsync(Guid saleId, CancellationToken cancellationToken = default)
    {
        Calls.Add("confirm");
        return Task.FromResult(FailAt == "confirm"
            ? SaleOperationResult.Failure("Sales.Stub", "confirm failed")
            : SaleOperationResult.Success());
    }

    public Task<SaleOperationResult> CompleteSaleAsync(Guid saleId, string? transactionReference = null, CancellationToken cancellationToken = default)
    {
        Calls.Add("complete");
        if (FailAt == "complete") return Task.FromResult(SaleOperationResult.Failure("Sales.Stub", "complete failed"));
        CompletedTransactionReference = transactionReference;
        return Task.FromResult(SaleOperationResult.Success());
    }

    public Task<SaleOperationResult> CancelSaleAsync(Guid saleId, string reason, CancellationToken cancellationToken = default)
    {
        Calls.Add("cancel");
        CancelReasons.Add(reason);
        return Task.FromResult(SaleOperationResult.Success());
    }
}
