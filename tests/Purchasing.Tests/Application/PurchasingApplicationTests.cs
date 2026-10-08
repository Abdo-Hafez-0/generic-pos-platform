using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Inventory.Contracts.Models;
using Microsoft.Extensions.DependencyInjection;
using Purchasing.Application.Commands;
using Purchasing.Application.Queries;
using Purchasing.Contracts.Interfaces;
using Purchasing.Contracts.Models;
using Purchasing.Domain.Enums;
using Purchasing.Infrastructure.DependencyInjection;
using Purchasing.Infrastructure.Persistence;
using Suppliers.Contracts.Interfaces;
using Suppliers.Contracts.Models;
using Tests.Common;

namespace Purchasing.Tests.Application;

/// <summary>Stubs of the OTHER modules' contracts: Purchasing tests never touch Suppliers/Catalog/Inventory implementations.</summary>
public sealed class StubSuppliers : ISupplierLookup
{
    private readonly Dictionary<Guid, SupplierLookupResult> _suppliers = [];

    public Guid Register(string name = "Acme Supply", SupplierStatusContract status = SupplierStatusContract.Active)
    {
        var id = Guid.NewGuid();
        _suppliers[id] = new SupplierLookupResult(id, "SUP-" + _suppliers.Count, name, null, null, status);
        return id;
    }

    public Task<SupplierLookupResult?> FindByIdAsync(Guid supplierId, CancellationToken cancellationToken = default)
        => Task.FromResult(_suppliers.GetValueOrDefault(supplierId));

    public Task<SupplierLookupResult?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
        => Task.FromResult(_suppliers.Values.FirstOrDefault(s => s.Code == code));
}

public sealed class StubCatalog : IProductLookup
{
    private readonly Dictionary<Guid, ProductLookupResult> _products = [];

    public Guid Register(string sku, decimal? cost = 4m, ProductStatusContract status = ProductStatusContract.Active, string name = "Product")
    {
        var id = Guid.NewGuid();
        _products[id] = new ProductLookupResult(id, sku, name + " " + sku, null, Guid.NewGuid(), "Cat", Guid.NewGuid(), "Each", "ea", 10m, cost, status);
        return id;
    }

    public Task<ProductLookupResult?> FindByIdAsync(Guid productId, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.GetValueOrDefault(productId));

    public Task<ProductLookupResult?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.Values.FirstOrDefault(p => p.Sku == sku));
}

public sealed class StubReceipts : IStockReceiptService
{
    private readonly HashSet<Guid> _failFor = [];
    public List<(Guid ProductId, Guid WarehouseId, decimal Quantity, string? Reference)> Received { get; } = [];

    public void FailFor(Guid productId) => _failFor.Add(productId);
    public void Heal() => _failFor.Clear();

    public Task<ReceiveStockResult> ReceiveStockAsync(Guid catalogProductId, Guid warehouseId, decimal quantity, string? reference = null, CancellationToken cancellationToken = default)
    {
        if (_failFor.Contains(catalogProductId))
            return Task.FromResult(ReceiveStockResult.Failure("Inventory.Stub", "warehouse offline"));

        Received.Add((catalogProductId, warehouseId, quantity, reference));
        return Task.FromResult(ReceiveStockResult.Success(Guid.NewGuid()));
    }
}

public sealed class StubIssues : IStockIssueService
{
    private readonly HashSet<Guid> _failFor = [];
    public List<(Guid ProductId, Guid WarehouseId, decimal Quantity, string? Reference)> Issued { get; } = [];

    public void FailFor(Guid productId) => _failFor.Add(productId);

    public Task<IssueStockResult> IssueStockAsync(Guid catalogProductId, Guid warehouseId, decimal quantity, string? reference = null, CancellationToken cancellationToken = default)
    {
        if (_failFor.Contains(catalogProductId))
            return Task.FromResult(IssueStockResult.Failure("Inventory.IssueStock.InsufficientStock", "Not enough stock."));

        Issued.Add((catalogProductId, warehouseId, quantity, reference));
        return Task.FromResult(IssueStockResult.Success(Guid.NewGuid()));
    }
}

public sealed class PurchasingApplicationTests
{
    private sealed record Env(TestModuleDatabase<PurchasingDbContext> Db, StubSuppliers Suppliers, StubCatalog Catalog, StubReceipts Receipts)
    {
        public StubIssues Issues { get; init; } = new();
    }

    private static async Task<Env> NewEnv()
    {
        var suppliers = new StubSuppliers();
        var catalog = new StubCatalog();
        var receipts = new StubReceipts();
        var issues = new StubIssues();
        var db = await TestModuleDatabase<PurchasingDbContext>.CreateAsync(s =>
        {
            s.AddPurchasingCore();
            s.AddSingleton<ISupplierLookup>(suppliers);
            s.AddSingleton<IProductLookup>(catalog);
            s.AddSingleton<IStockReceiptService>(receipts);
            s.AddSingleton<IStockIssueService>(issues);
        });
        return new Env(db, suppliers, catalog, receipts) { Issues = issues };
    }

    private static async Task<Guid> Draft(Env e, Guid? supplier = null)
        => await e.Db.InScopeAsync(async sp =>
        {
            var r = await sp.GetRequiredService<CreatePurchaseOrderCommandHandler>()
                .HandleAsync(new CreatePurchaseOrderCommand(supplier ?? e.Suppliers.Register(), "REF"));
            Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
            return r.Value;
        });

    private static Task<Platform.Core.Results.Result<Guid>> Line(Env e, Guid order, string code, decimal qty = 2m, decimal? cost = null)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(order, code, qty, cost)));

    private static Task<Purchasing.Application.DTOs.PurchaseOrderDto?> Get(Env e, Guid order)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<GetPurchaseOrderQueryHandler>().HandleAsync(new GetPurchaseOrderQuery(order)));

    private static Task<Platform.Core.Results.Result> Submit(Env e, Guid order)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(order)));

    private static Task<Platform.Core.Results.Result<int>> Receive(Env e, Guid order, Guid warehouse, params ReceiveLineQuantity[]? lines)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<ReceivePurchaseOrderCommandHandler>()
            .HandleAsync(new ReceivePurchaseOrderCommand(order, warehouse, lines is { Length: > 0 } ? lines : null)));

    private static Task<Platform.Core.Results.Result> CloseShort(Env e, Guid order, string reason)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<ClosePurchaseOrderShortCommandHandler>().HandleAsync(new ClosePurchaseOrderShortCommand(order, reason)));

    // ------------------------------------------------------------------ create

    [Fact]
    public async Task Create_ForActiveSupplier_SnapshotsSupplier_AndPersistsDraft()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var supplier = e.Suppliers.Register("Globex");

        var id = await Draft(e, supplier);

        var dto = await Get(e, id);
        Assert.Equal(PurchaseOrderStatus.Draft, dto!.Status);
        Assert.Equal("Globex", dto.SupplierName);
        Assert.Equal(supplier, dto.SupplierId);
        Assert.Equal("REF", dto.Reference);
        Assert.Empty(dto.Lines);
    }

    [Fact]
    public async Task Create_UnknownOrInactiveSupplier_IsRejected()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var inactive = e.Suppliers.Register(status: SupplierStatusContract.Inactive);

        var unknown = await e.Db.InScopeAsync(sp => sp.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(Guid.NewGuid())));
        var notActive = await e.Db.InScopeAsync(sp => sp.GetRequiredService<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(inactive)));

        Assert.Equal("Purchasing.CreatePurchaseOrder.SupplierNotFound", unknown.Error.Code);
        Assert.Equal("Purchasing.CreatePurchaseOrder.SupplierInactive", notActive.Error.Code);
        Assert.Equal(0, (await e.Db.InScopeAsync(sp => sp.GetRequiredService<ListPurchaseOrdersQueryHandler>().HandleAsync(new ListPurchaseOrdersQuery()))).Total);
    }

    // ------------------------------------------------------------------ lines

    [Fact]
    public async Task AddLine_BySku_UsesCatalogCostWhenNoCostGiven_AndSnapshotsProduct()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1", cost: 4.5m);
        var order = await Draft(e);

        var r = await Line(e, order, "SKU-1", 4m);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        var line = Assert.Single((await Get(e, order))!.Lines);
        Assert.Equal(product, line.ProductId);
        Assert.Equal("SKU-1", line.ProductSku);
        Assert.Equal(4.5m, line.UnitCost);
        Assert.Equal(18m, line.LineTotal);
        Assert.Equal(18m, (await Get(e, order))!.TotalAmount);
    }

    [Fact]
    public async Task AddLine_ByProductId_AndExplicitCostOverridesCatalog()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-2", cost: 1m);
        var order = await Draft(e);

        var r = await Line(e, order, product.ToString(), 3m, cost: 7m);

        Assert.True(r.IsSuccess);
        Assert.Equal(7m, Assert.Single((await Get(e, order))!.Lines).UnitCost);
    }

    [Fact]
    public async Task AddLine_Failures()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("NOCOST", cost: null);
        e.Catalog.Register("OLD", status: ProductStatusContract.Inactive);
        e.Catalog.Register("OK");
        var order = await Draft(e);

        Assert.Equal("Purchasing.AddLine.ProductNotFound", (await Line(e, order, "GHOST")).Error.Code);
        Assert.Equal("Purchasing.AddLine.ProductInactive", (await Line(e, order, "OLD")).Error.Code);
        Assert.Equal("Purchasing.AddLine.CostRequired", (await Line(e, order, "NOCOST")).Error.Code);
        Assert.Equal("Purchasing.AddLine.ProductCodeRequired", (await Line(e, order, " ")).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidQuantity", (await Line(e, order, "OK", 0m)).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidCost", (await Line(e, order, "OK", 1m, -1m)).Error.Code);
        Assert.Equal("Purchasing.AddLine.OrderNotFound", (await Line(e, Guid.NewGuid(), "OK")).Error.Code);
        Assert.Empty((await Get(e, order))!.Lines);
    }

    [Fact]
    public async Task RemoveLine_AndChangeQuantity_UpdateTheOrder()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A", cost: 10m);
        e.Catalog.Register("B", cost: 1m);
        var order = await Draft(e);
        var a = (await Line(e, order, "A", 2m)).Value;
        var b = (await Line(e, order, "B", 5m)).Value;

        Assert.True((await e.Db.InScopeAsync(sp => sp.GetRequiredService<ChangePurchaseOrderLineQuantityCommandHandler>()
            .HandleAsync(new ChangePurchaseOrderLineQuantityCommand(order, a, 3m)))).IsSuccess);
        Assert.True((await e.Db.InScopeAsync(sp => sp.GetRequiredService<RemovePurchaseOrderLineCommandHandler>()
            .HandleAsync(new RemovePurchaseOrderLineCommand(order, b)))).IsSuccess);

        var dto = (await Get(e, order))!;
        Assert.Equal(30m, dto.TotalAmount);
        Assert.Equal(a, Assert.Single(dto.Lines).LineId);
        var bad = await e.Db.InScopeAsync(sp => sp.GetRequiredService<ChangePurchaseOrderLineQuantityCommandHandler>()
            .HandleAsync(new ChangePurchaseOrderLineQuantityCommand(order, a, -1m)));
        Assert.Equal("Purchasing.PurchaseOrder.InvalidQuantity", bad.Error.Code);
    }

    // ------------------------------------------------------------------ submit / cancel

    [Fact]
    public async Task Submit_FreezesTheOrder()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A");
        var order = await Draft(e);

        Assert.Equal("Purchasing.PurchaseOrder.NoLines", (await Submit(e, order)).Error.Code);
        await Line(e, order, "A");
        Assert.True((await Submit(e, order)).IsSuccess);

        Assert.Equal(PurchaseOrderStatus.Submitted, (await Get(e, order))!.Status);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", (await Line(e, order, "A")).Error.Code);
        Assert.Equal("Purchasing.Submit.OrderNotFound", (await Submit(e, Guid.NewGuid())).Error.Code);
    }

    [Fact]
    public async Task Cancel_Works_ForDraftAndSubmitted_NotForReceived()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A");
        var draft = await Draft(e);
        var submitted = await Draft(e);
        await Line(e, submitted, "A");
        await Submit(e, submitted);

        Task<Platform.Core.Results.Result> Cancel(Guid id, string reason)
            => e.Db.InScopeAsync(sp => sp.GetRequiredService<CancelPurchaseOrderCommandHandler>().HandleAsync(new CancelPurchaseOrderCommand(id, reason)));

        Assert.True((await Cancel(draft, "no longer needed")).IsSuccess);
        Assert.True((await Cancel(submitted, "supplier closed")).IsSuccess);
        Assert.Equal("no longer needed", (await Get(e, draft))!.CancellationReason);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", (await Cancel(draft, "again")).Error.Code);
        Assert.Equal("Purchasing.Cancel.OrderNotFound", (await Cancel(Guid.NewGuid(), "x")).Error.Code);

        var toReceive = await Draft(e);
        await Line(e, toReceive, "A");
        await Submit(e, toReceive);
        await Receive(e, toReceive, Guid.NewGuid());
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", (await Cancel(toReceive, "late")).Error.Code);
    }

    // ------------------------------------------------------------------ receive

    [Fact]
    public async Task Receive_ReceivesEveryLineThroughInventoryContracts_AndCompletesTheOrder()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var a = e.Catalog.Register("A");
        var b = e.Catalog.Register("B");
        var order = await Draft(e);
        await Line(e, order, "A", 3m);
        await Line(e, order, "B", 7m);
        await Submit(e, order);
        var warehouse = Guid.NewGuid();

        var r = await Receive(e, order, warehouse);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        Assert.Equal(2, r.Value);
        var dto = (await Get(e, order))!;
        Assert.Equal(PurchaseOrderStatus.Received, dto.Status);
        Assert.NotNull(dto.ReceivedAt);
        Assert.Equal(warehouse, dto.WarehouseId);
        Assert.All(dto.Lines, l => Assert.True(l.IsReceived));
        Assert.Equal(2, e.Receipts.Received.Count);
        Assert.Contains(e.Receipts.Received, x => x.ProductId == a && x.Quantity == 3m && x.WarehouseId == warehouse);
        Assert.Contains(e.Receipts.Received, x => x.ProductId == b && x.Quantity == 7m);
        Assert.All(e.Receipts.Received, x => Assert.Contains(dto.Number, x.Reference));
    }

    [Fact]
    public async Task Receive_PartialFailure_KeepsProgress_AndRetryNeverReceivesALineTwice()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A");
        var b = e.Catalog.Register("B");
        var order = await Draft(e);
        await Line(e, order, "A", 1m);
        await Line(e, order, "B", 1m);
        await Submit(e, order);
        var warehouse = Guid.NewGuid();
        e.Receipts.FailFor(b);

        var first = await Receive(e, order, warehouse);

        Assert.True(first.IsFailure);
        Assert.Equal("Purchasing.Receive.StockReceiptFailed", first.Error.Code);
        var partial = (await Get(e, order))!;
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, partial.Status);   // FIX-09: the kept part is a part receipt
        Assert.Equal([true, false], partial.Lines.OrderBy(l => l.ProductSku).Select(l => l.IsReceived).ToArray());
        Assert.Single(e.Receipts.Received);

        e.Receipts.Heal();
        var retry = await Receive(e, order, warehouse);

        Assert.True(retry.IsSuccess);
        Assert.Equal(1, retry.Value);                       // only the outstanding line
        Assert.Equal(2, e.Receipts.Received.Count);         // A was NOT received twice
        Assert.Equal(PurchaseOrderStatus.Received, (await Get(e, order))!.Status);
    }

    [Fact]
    public async Task Receive_PartiallyReceivedOrder_CannotBeCancelled_OrRedirectedToAnotherWarehouse()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A");
        var b = e.Catalog.Register("B");
        var order = await Draft(e);
        await Line(e, order, "A");
        await Line(e, order, "B");
        await Submit(e, order);
        var warehouse = Guid.NewGuid();
        e.Receipts.FailFor(b);
        await Receive(e, order, warehouse);

        var cancel = await e.Db.InScopeAsync(sp => sp.GetRequiredService<CancelPurchaseOrderCommandHandler>().HandleAsync(new CancelPurchaseOrderCommand(order, "x")));
        var elsewhere = await Receive(e, order, Guid.NewGuid());

        Assert.Equal("Purchasing.PurchaseOrder.PartiallyReceived", cancel.Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.WarehouseMismatch", elsewhere.Error.Code);
    }

    [Fact]
    public async Task Receive_Validation()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A");
        var draft = await Draft(e);
        await Line(e, draft, "A");

        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", (await Receive(e, draft, Guid.NewGuid())).Error.Code);
        await Submit(e, draft);
        Assert.Equal("Purchasing.PurchaseOrder.WarehouseRequired", (await Receive(e, draft, Guid.Empty)).Error.Code);
        Assert.Equal("Purchasing.Receive.OrderNotFound", (await Receive(e, Guid.NewGuid(), Guid.NewGuid())).Error.Code);
        Assert.Empty(e.Receipts.Received);

        Assert.True((await Receive(e, draft, Guid.NewGuid())).IsSuccess);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", (await Receive(e, draft, Guid.NewGuid())).Error.Code);   // already received
        Assert.Single(e.Receipts.Received);
    }

    // ------------------------------------------------------------------ FIX-09: part deliveries and closing short

    private static async Task<(Guid Order, Guid LineA, Guid LineB, Guid ProductA, Guid ProductB)> PlacedTwoLineOrder(Env e, decimal qtyA = 10m, decimal qtyB = 4m)
    {
        var a = e.Catalog.Register("A", cost: 2m);
        var b = e.Catalog.Register("B", cost: 5m);
        var order = await Draft(e);
        var lineA = (await Line(e, order, "A", qtyA)).Value;
        var lineB = (await Line(e, order, "B", qtyB)).Value;
        Assert.True((await Submit(e, order)).IsSuccess);
        return (order, lineA, lineB, a, b);
    }

    [Fact]
    public async Task A_part_delivery_moves_only_what_arrived_and_the_next_delivery_completes_the_order()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, lineB, a, b) = await PlacedTwoLineOrder(e);
        var warehouse = Guid.NewGuid();

        var first = await Receive(e, order, warehouse, new ReceiveLineQuantity(lineA, 6m), new ReceiveLineQuantity(lineB, 0m));

        Assert.Equal(1, first.Value);                                         // one line took part in the delivery
        Assert.Equal([(a, 6m)], e.Receipts.Received.Select(r => (r.ProductId, r.Quantity)).ToArray());
        var partly = (await Get(e, order))!;
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, partly.Status);
        Assert.Equal(12m, partly.ReceivedAmount);                            // 6 x 2
        var a1 = partly.Lines.Single(l => l.LineId == lineA);
        Assert.Equal((6m, 4m, false), (a1.ReceivedQuantity, a1.OutstandingQuantity, a1.IsReceived));
        Assert.Equal(4m, partly.Lines.Single(l => l.LineId == lineB).OutstandingQuantity);

        var second = await Receive(e, order, warehouse);                       // everything still outstanding

        Assert.Equal(2, second.Value);
        Assert.Equal([(a, 6m), (a, 4m), (b, 4m)], e.Receipts.Received.Select(r => (r.ProductId, r.Quantity)).ToArray());
        var done = (await Get(e, order))!;
        Assert.Equal(PurchaseOrderStatus.Received, done.Status);
        Assert.Equal(done.TotalAmount, done.ReceivedAmount);
        Assert.All(done.Lines, l => Assert.Equal((true, 0m), (l.IsReceived, l.OutstandingQuantity)));
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", (await Receive(e, order, warehouse)).Error.Code);
    }

    [Fact]
    public async Task A_delivery_with_more_than_is_still_expected_is_refused_before_any_stock_moves()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, lineB, _, _) = await PlacedTwoLineOrder(e);
        var warehouse = Guid.NewGuid();
        await Receive(e, order, warehouse, new ReceiveLineQuantity(lineA, 8m));

        // B is fine, A has only 2 left: nothing of the delivery is taken (B comes first in the request on purpose)
        var refused = await Receive(e, order, warehouse, new ReceiveLineQuantity(lineB, 1m), new ReceiveLineQuantity(lineA, 3m));

        Assert.Equal("Purchasing.PurchaseOrder.MoreThanOrdered", refused.Error.Code);
        Assert.Single(e.Receipts.Received);
        var dto = (await Get(e, order))!;
        Assert.Equal((8m, 0m), (dto.Lines.Single(l => l.LineId == lineA).ReceivedQuantity, dto.Lines.Single(l => l.LineId == lineB).ReceivedQuantity));
    }

    [Fact]
    public async Task A_delivery_is_validated_in_plain_words()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, lineB, _, _) = await PlacedTwoLineOrder(e);
        var warehouse = Guid.NewGuid();

        Assert.Equal("Purchasing.Receive.InvalidQuantity", (await Receive(e, order, warehouse, new ReceiveLineQuantity(lineA, -1m))).Error.Code);
        Assert.Equal("Purchasing.Receive.DuplicateLine", (await Receive(e, order, warehouse, new ReceiveLineQuantity(lineA, 1m), new ReceiveLineQuantity(lineA, 1m))).Error.Code);
        Assert.Equal("Purchasing.Receive.NothingToReceive", (await Receive(e, order, warehouse, new ReceiveLineQuantity(lineA, 0m), new ReceiveLineQuantity(lineB, 0m))).Error.Code);
        Assert.Equal("Purchasing.Receive.NothingToReceive", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<ReceivePurchaseOrderCommandHandler>()
            .HandleAsync(new ReceivePurchaseOrderCommand(order, warehouse, [])))).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.LineNotFound", (await Receive(e, order, warehouse, new ReceiveLineQuantity(Guid.NewGuid(), 1m))).Error.Code);
        Assert.Empty(e.Receipts.Received);
        Assert.Equal(PurchaseOrderStatus.Submitted, (await Get(e, order))!.Status);
    }

    [Fact]
    public async Task A_partly_received_order_is_closed_short_and_then_takes_no_more_goods()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, _, _, _) = await PlacedTwoLineOrder(e);
        var warehouse = Guid.NewGuid();

        Assert.Contains("cancel it instead", (await CloseShort(e, order, "nothing came")).Error.Description);   // nothing received yet
        await Receive(e, order, warehouse, new ReceiveLineQuantity(lineA, 10m));
        Assert.Equal("Purchasing.PurchaseOrder.ClosingReasonRequired", (await CloseShort(e, order, " ")).Error.Code);
        Assert.Contains("Close it short", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<CancelPurchaseOrderCommandHandler>()
            .HandleAsync(new CancelPurchaseOrderCommand(order, "x")))).Error.Description);

        Assert.True((await CloseShort(e, order, "B is discontinued")).IsSuccess);

        var closed = (await Get(e, order))!;
        Assert.Equal((PurchaseOrderStatus.Closed, "B is discontinued"), (closed.Status, closed.ClosingReason));
        Assert.NotNull(closed.ClosedAt);
        Assert.All(closed.Lines, l => Assert.Equal(0m, l.OutstandingQuantity));   // nothing is expected any more
        Assert.Equal(20m, closed.ReceivedAmount);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", (await Receive(e, order, warehouse)).Error.Code);
        Assert.Equal("Purchasing.CloseShort.OrderNotFound", (await CloseShort(e, Guid.NewGuid(), "x")).Error.Code);
    }

    [Fact]
    public async Task The_summary_counts_part_deliveries_and_closed_orders_and_values_what_really_arrived()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var warehouse = Guid.NewGuid();
        var (partly, lineA, _, _, _) = await PlacedTwoLineOrder(e);   // total 10x2 + 4x5 = 40
        await Receive(e, partly, warehouse, new ReceiveLineQuantity(lineA, 5m));   // 10 received, 30 still to come
        var closedOrder = await Draft(e);
        var closedLine = (await Line(e, closedOrder, "A", 3m)).Value;   // total 6
        await Line(e, closedOrder, "B", 1m);                            // + 5
        await Submit(e, closedOrder);
        await Receive(e, closedOrder, warehouse, new ReceiveLineQuantity(closedLine, 3m));   // 6 received
        await CloseShort(e, closedOrder, "rest discontinued");

        var summary = await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().GetSummaryAsync());
        var line = (await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().GetAsync(partly)))!.Lines.Single(l => l.LineId == lineA);

        Assert.Equal(new PurchaseSummaryResult(2, 0, 0, 0, 0, ReceivedValue: 16m, OpenValue: 30m, PartiallyReceived: 1, Closed: 1), summary);
        Assert.Equal((5m, false), (line.ReceivedQuantity, line.IsReceived));
    }

    // ------------------------------------------------------------------ FIX-09b: supplier returns

    private static Task<Platform.Core.Results.Result<Guid>> Return(Env e, Guid order, string reason, params ReturnLineQuantity[] lines)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<ReturnToSupplierCommandHandler>().HandleAsync(new ReturnToSupplierCommand(order, reason, lines)));

    private static Task<IReadOnlyList<Purchasing.Application.DTOs.SupplierReturnDto>> Returns(Env e, Guid order)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<ListSupplierReturnsQueryHandler>().HandleAsync(new ListSupplierReturnsQuery(order)));

    [Fact]
    public async Task Received_goods_go_back_to_the_supplier_out_of_the_order_warehouse()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, lineB, a, b) = await PlacedTwoLineOrder(e);
        var warehouse = Guid.NewGuid();
        await Receive(e, order, warehouse);

        var returned = await Return(e, order, "damaged", new ReturnLineQuantity(lineA, 3m), new ReturnLineQuantity(lineB, 1m));

        Assert.True(returned.IsSuccess, returned.IsFailure ? returned.Error.ToString() : null);
        var number = Assert.Single(await Returns(e, order)).Number;
        Assert.Equal([(a, warehouse, 3m), (b, warehouse, 1m)], e.Issues.Issued.Select(i => (i.ProductId, i.WarehouseId, i.Quantity)).ToArray());
        Assert.All(e.Issues.Issued, i => Assert.Contains(number, i.Reference));
        var r = (await Returns(e, order)).Single();
        Assert.Equal((returned.Value, "damaged", 11m, warehouse), (r.ReturnId, r.Reason, r.TotalAmount, r.WarehouseId));   // 3 x 2 + 1 x 5
        Assert.Equal(2, r.Lines.Count);
        var dto = (await Get(e, order))!;
        Assert.Equal((3m, 7m), (dto.Lines.Single(l => l.LineId == lineA).ReturnedQuantity, dto.Lines.Single(l => l.LineId == lineA).ReturnableQuantity));
        Assert.Equal(PurchaseOrderStatus.Received, dto.Status);
        var contract = (await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().GetAsync(order)))!;
        Assert.Equal(1m, contract.Lines.Single(l => l.LineId == lineB).ReturnedQuantity);
    }

    [Fact]
    public async Task A_return_beyond_what_is_left_is_refused_before_any_stock_moves()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, lineB, _, _) = await PlacedTwoLineOrder(e);
        await Receive(e, order, Guid.NewGuid(), new ReceiveLineQuantity(lineA, 5m));   // B never arrived
        Assert.True((await Return(e, order, "first", new ReturnLineQuantity(lineA, 2m))).IsSuccess);

        var tooMuch = await Return(e, order, "second", new ReturnLineQuantity(lineA, 1m), new ReturnLineQuantity(lineB, 1m));
        var overA = await Return(e, order, "third", new ReturnLineQuantity(lineA, 4m));

        Assert.Equal("Purchasing.SupplierReturn.MoreThanReceived", tooMuch.Error.Code);   // B: nothing received
        Assert.Contains("Only 3", overA.Error.Description);
        Assert.Single(e.Issues.Issued);
        Assert.Single(await Returns(e, order));
    }

    [Fact]
    public async Task A_return_is_validated_in_plain_words()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, _, _, _) = await PlacedTwoLineOrder(e);

        Assert.Equal("Purchasing.SupplierReturn.NothingReceived", (await Return(e, order, "x", new ReturnLineQuantity(lineA, 1m))).Error.Code);
        await Receive(e, order, Guid.NewGuid());
        Assert.Equal("Purchasing.SupplierReturn.ReasonRequired", (await Return(e, order, " ", new ReturnLineQuantity(lineA, 1m))).Error.Code);
        Assert.Equal("Purchasing.SupplierReturn.InvalidQuantity", (await Return(e, order, "x", new ReturnLineQuantity(lineA, -1m))).Error.Code);
        Assert.Equal("Purchasing.SupplierReturn.DuplicateLine", (await Return(e, order, "x", new ReturnLineQuantity(lineA, 1m), new ReturnLineQuantity(lineA, 1m))).Error.Code);
        Assert.Equal("Purchasing.SupplierReturn.NothingToReturn", (await Return(e, order, "x")).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.LineNotFound", (await Return(e, order, "x", new ReturnLineQuantity(Guid.NewGuid(), 1m))).Error.Code);
        Assert.Equal("Purchasing.SupplierReturn.OrderNotFound", (await Return(e, Guid.NewGuid(), "x", new ReturnLineQuantity(lineA, 1m))).Error.Code);
        Assert.Empty(e.Issues.Issued);
        Assert.Empty(await Returns(e, order));
    }

    [Fact]
    public async Task Without_a_transaction_a_failed_line_keeps_only_what_left_stock()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var (order, lineA, lineB, _, b) = await PlacedTwoLineOrder(e);
        await Receive(e, order, Guid.NewGuid());
        e.Issues.FailFor(b);

        var onlyB = await Return(e, order, "x", new ReturnLineQuantity(lineB, 1m));
        var both = await Return(e, order, "y", new ReturnLineQuantity(lineA, 2m), new ReturnLineQuantity(lineB, 1m));

        Assert.Contains("Nothing was returned", onlyB.Error.Description);
        Assert.Equal("Purchasing.SupplierReturn.StockIssueFailed", both.Error.Code);
        var kept = Assert.Single(await Returns(e, order));
        Assert.Equal((lineA, 2m), (Assert.Single(kept.Lines).OrderLineId, kept.Lines[0].Quantity));
        var dto = (await Get(e, order))!;
        Assert.Equal((2m, 0m), (dto.Lines.Single(l => l.LineId == lineA).ReturnedQuantity, dto.Lines.Single(l => l.LineId == lineB).ReturnedQuantity));
    }

    // ------------------------------------------------------------------ queries and contracts

    [Fact]
    public async Task List_IsPaged_NewestFirst_AndFilteredByStatus()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A");
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await Draft(e));
            await Task.Delay(5);
        }

        await Line(e, ids[0], "A");
        await Submit(e, ids[0]);

        var page1 = await e.Db.InScopeAsync(sp => sp.GetRequiredService<ListPurchaseOrdersQueryHandler>().HandleAsync(new ListPurchaseOrdersQuery(0, 2)));
        var page3 = await e.Db.InScopeAsync(sp => sp.GetRequiredService<ListPurchaseOrdersQueryHandler>().HandleAsync(new ListPurchaseOrdersQuery(4, 2)));
        var submitted = await e.Db.InScopeAsync(sp => sp.GetRequiredService<ListPurchaseOrdersQueryHandler>().HandleAsync(new ListPurchaseOrdersQuery(Status: PurchaseOrderStatus.Submitted)));

        Assert.Equal(5, page1.Total);
        Assert.Equal([ids[4], ids[3]], page1.Items.Select(i => i.OrderId).ToArray());
        Assert.Equal([ids[0]], page3.Items.Select(i => i.OrderId).ToArray());
        Assert.Equal([ids[0]], submitted.Items.Select(i => i.OrderId).ToArray());
        Assert.Equal(1, submitted.Total);
    }

    [Fact]
    public async Task Reader_Contract_ExposesOrdersAndSummary_WithoutDomainTypes()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A", cost: 10m);
        var open = await Draft(e);
        await Line(e, open, "A", 2m);                            // draft, value 20
        var received = await Draft(e);
        await Line(e, received, "A", 5m);                        // value 50
        await Submit(e, received);
        await Receive(e, received, Guid.NewGuid());
        var cancelled = await Draft(e);
        await e.Db.InScopeAsync(sp => sp.GetRequiredService<CancelPurchaseOrderCommandHandler>().HandleAsync(new CancelPurchaseOrderCommand(cancelled, "x")));

        var summary = await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().GetSummaryAsync());
        var one = await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().GetAsync(received));
        var recent = await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().ListRecentAsync(10, PurchaseOrderStatusContract.Draft));
        var none = await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().GetAsync(Guid.NewGuid()));

        Assert.Equal(new PurchaseSummaryResult(3, 1, 0, 1, 1, 50m, 20m), summary);
        Assert.Equal(PurchaseOrderStatusContract.Received, one!.Status);
        Assert.Single(one.Lines);
        Assert.Equal(open, Assert.Single(recent).OrderId);
        Assert.Empty(recent[0].Lines);                           // list results carry no lines
        Assert.Null(none);
        Assert.DoesNotContain(typeof(IPurchaseOrderReader).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Purchasing.Domain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyDatabase_Summary_IsAllZero()
    {
        var e = await NewEnv();
        await using var _ = e.Db;

        var summary = await e.Db.InScopeAsync(sp => sp.GetRequiredService<IPurchaseOrderReader>().GetSummaryAsync());

        Assert.Equal(new PurchaseSummaryResult(0, 0, 0, 0, 0, 0m, 0m), summary);
    }
}
