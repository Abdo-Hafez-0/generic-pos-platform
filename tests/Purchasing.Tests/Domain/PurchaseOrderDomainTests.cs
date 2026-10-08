using Purchasing.Domain.Entities;
using Purchasing.Domain.Enums;
using Purchasing.Domain.ValueObjects;

namespace Purchasing.Tests.Domain;

public sealed class PurchaseOrderDomainTests
{
    private static readonly Guid Supplier = Guid.NewGuid();

    private static PurchaseOrder NewOrder()
    {
        var r = PurchaseOrder.Create(Supplier, "SUP-1", "Acme Supply", "REF-1", "notes");
        Assert.True(r.IsSuccess);
        return r.Value;
    }

    private static Platform.Core.Results.Result<PurchaseOrderLine> Add(PurchaseOrder o, Guid? product = null, decimal qty = 2m, decimal cost = 10m, string sku = "SKU", string name = "Item")
        => o.AddLine(product ?? Guid.NewGuid(), sku, name, new OrderQuantity(qty), new Money(cost));

    private static PurchaseOrder SubmittedWithLines(int lines = 2)
    {
        var o = NewOrder();
        for (var i = 0; i < lines; i++) Add(o, sku: "S" + i);
        Assert.True(o.Submit().IsSuccess);
        return o;
    }

    // --- create ---

    [Fact]
    public void Create_StartsDraft_WithNumberAndSnapshots()
    {
        var o = NewOrder();

        Assert.Equal(PurchaseOrderStatus.Draft, o.Status);
        Assert.StartsWith("PO-", o.Number);
        Assert.Equal(Supplier, o.SupplierId);
        Assert.Equal("SUP-1", o.SupplierCode);
        Assert.Equal("Acme Supply", o.SupplierName);
        Assert.Equal(0m, o.TotalAmount.Amount);
        Assert.Empty(o.Lines);
    }

    [Fact]
    public void Create_NumbersAreUnique()
        => Assert.NotEqual(NewOrder().Number, NewOrder().Number);

    [Fact]
    public void Create_Validates()
    {
        Assert.Equal("Purchasing.PurchaseOrder.SupplierRequired", PurchaseOrder.Create(Guid.Empty, "C", "N").Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.SupplierSnapshotRequired", PurchaseOrder.Create(Supplier, "", "N").Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.ReferenceTooLong", PurchaseOrder.Create(Supplier, "C", "N", new string('x', 101)).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.NotesTooLong", PurchaseOrder.Create(Supplier, "C", "N", null, new string('x', 1001)).Error.Code);
    }

    // --- lines ---

    [Fact]
    public void AddLine_SnapshotsProduct_AndUpdatesTotal()
    {
        var o = NewOrder();
        var product = Guid.NewGuid();

        var r = o.AddLine(product, " SKU-9 ", " Widget ", new OrderQuantity(3m), new Money(2.5m));

        Assert.True(r.IsSuccess);
        var line = Assert.Single(o.Lines);
        Assert.Equal(product, line.ProductId);
        Assert.Equal("SKU-9", line.ProductSku);
        Assert.Equal("Widget", line.ProductName);
        Assert.Equal(7.5m, line.LineTotal.Amount);
        Assert.Equal(7.5m, o.TotalAmount.Amount);
    }

    [Fact]
    public void AddLine_SameProductTwice_MergesQuantity_AndUsesTheLatestCost()
    {
        var o = NewOrder();
        var product = Guid.NewGuid();
        Add(o, product, 2m, 10m);

        Add(o, product, 3m, 12m);

        var line = Assert.Single(o.Lines);
        Assert.Equal(5m, line.Quantity.Value);
        Assert.Equal(12m, line.UnitCost.Amount);
        Assert.Equal(60m, o.TotalAmount.Amount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Quantity_MustBePositive(double q)
        => Assert.Equal("Purchasing.PurchaseOrder.InvalidQuantity", OrderQuantity.Create((decimal)q).Error.Code);

    [Fact]
    public void Cost_CannotBeNegative_AndRoundsToFourDecimals()
    {
        Assert.Equal("Purchasing.PurchaseOrder.InvalidCost", Money.Create(-0.01m).Error.Code);
        Assert.Equal(1.2346m, Money.Create(1.23456m).Value.Amount);
        Assert.Equal(0m, Money.Create(0m).Value.Amount);   // free goods are allowed
    }

    [Fact]
    public void AddLine_RequiresProductAndSnapshot()
    {
        var o = NewOrder();

        Assert.Equal("Purchasing.PurchaseOrder.ProductRequired", Add(o, Guid.Empty).Error.Code);
        // (Guid.Empty is rejected: the product is referenced by its Catalog ID)
        Assert.Equal("Purchasing.PurchaseOrder.ProductSnapshotRequired", Add(o, Guid.NewGuid(), sku: " ").Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.ProductSnapshotRequired", Add(o, Guid.NewGuid(), name: "").Error.Code);
        Assert.Empty(o.Lines);
    }

    [Fact]
    public void RemoveLine_And_ChangeQuantity_WorkInDraft()
    {
        var o = NewOrder();
        var a = Add(o, qty: 2m, cost: 5m).Value;
        var b = Add(o, qty: 1m, cost: 10m).Value;

        Assert.True(o.ChangeLineQuantity(a.Id, new OrderQuantity(4m)).IsSuccess);
        Assert.Equal(30m, o.TotalAmount.Amount);
        Assert.True(o.RemoveLine(b.Id).IsSuccess);
        Assert.Equal(20m, o.TotalAmount.Amount);
        Assert.Equal("Purchasing.PurchaseOrder.LineNotFound", o.RemoveLine(b.Id).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.LineNotFound", o.ChangeLineQuantity(b.Id, new OrderQuantity(1m)).Error.Code);
    }

    [Fact]
    public void LineChanges_AreRejectedOutsideDraft()
    {
        var o = SubmittedWithLines(1);
        var line = o.Lines[0];

        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", Add(o).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", o.RemoveLine(line.Id).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", o.ChangeLineQuantity(line.Id, new OrderQuantity(9m)).Error.Code);
    }

    // --- lifecycle ---

    [Fact]
    public void Submit_RequiresLines_AndMovesToSubmitted()
    {
        var o = NewOrder();
        Assert.Equal("Purchasing.PurchaseOrder.NoLines", o.Submit().Error.Code);

        Add(o);
        Assert.True(o.Submit().IsSuccess);

        Assert.Equal(PurchaseOrderStatus.Submitted, o.Status);
        Assert.NotNull(o.SubmittedAt);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", o.Submit().Error.Code);
    }

    [Fact]
    public void Receiving_RequiresSubmittedOrder_AndAWarehouse()
    {
        var draft = NewOrder();
        Add(draft);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", draft.BeginReceiving(Guid.NewGuid()).Error.Code);

        var o = SubmittedWithLines();
        Assert.Equal("Purchasing.PurchaseOrder.WarehouseRequired", o.BeginReceiving(Guid.Empty).Error.Code);
        var warehouse = Guid.NewGuid();
        Assert.True(o.BeginReceiving(warehouse).IsSuccess);
        Assert.Equal(warehouse, o.WarehouseId);
    }

    private static Platform.Core.Results.Result ReceiveAll(PurchaseOrder o, int line)
        => o.ReceiveLine(o.Lines[line].Id, new OrderQuantity(o.Lines[line].OutstandingQuantity));

    [Fact]
    public void ReceivingLines_CompletesTheOrder_OnlyWhenEveryLineIsReceived()
    {
        var o = SubmittedWithLines(2);
        o.BeginReceiving(Guid.NewGuid());

        Assert.True(ReceiveAll(o, 0).IsSuccess);
        Assert.True(o.UpdateReceivingStatus().IsSuccess);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, o.Status);   // one line outstanding
        Assert.Null(o.ReceivedAt);

        Assert.True(ReceiveAll(o, 1).IsSuccess);
        Assert.True(o.UpdateReceivingStatus().IsSuccess);
        Assert.Equal(PurchaseOrderStatus.Received, o.Status);
        Assert.NotNull(o.ReceivedAt);
        Assert.Equal(o.TotalAmount, o.ReceivedAmount);
    }

    [Fact]
    public void ReceiveLine_Twice_OrUnknownLine_Fails()
    {
        var o = SubmittedWithLines(2);
        o.BeginReceiving(Guid.NewGuid());
        ReceiveAll(o, 0);

        Assert.Equal("Purchasing.PurchaseOrder.LineAlreadyReceived", o.ReceiveLine(o.Lines[0].Id, new OrderQuantity(1m)).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.LineNotFound", o.ReceiveLine(PurchaseOrderLineId.New(), new OrderQuantity(1m)).Error.Code);
    }

    // --- FIX-09: part deliveries ---

    [Fact]
    public void A_line_is_received_over_several_deliveries_and_the_received_value_follows()
    {
        var o = NewOrder();
        Add(o, qty: 10m, cost: 2.5m);
        o.Submit();
        o.BeginReceiving(Guid.NewGuid());
        var line = o.Lines[0];

        Assert.True(o.ReceiveLine(line.Id, new OrderQuantity(4m)).IsSuccess);
        o.UpdateReceivingStatus();
        Assert.Equal((4m, 6m, false, true), (line.ReceivedQuantity, line.OutstandingQuantity, line.IsReceived, line.HasReceipts));
        Assert.Equal(10m, o.ReceivedAmount.Amount);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, o.Status);
        Assert.Null(line.ReceivedAt);

        Assert.True(o.BeginReceiving(o.WarehouseId!.Value).IsSuccess);   // a partly received order takes the next delivery
        Assert.True(o.ReceiveLine(line.Id, new OrderQuantity(6m)).IsSuccess);
        o.UpdateReceivingStatus();
        Assert.Equal((10m, 0m, true), (line.ReceivedQuantity, line.OutstandingQuantity, line.IsReceived));
        Assert.NotNull(line.ReceivedAt);
        Assert.Equal(25m, o.ReceivedAmount.Amount);
        Assert.Equal(PurchaseOrderStatus.Received, o.Status);
    }

    [Fact]
    public void More_than_is_still_expected_is_refused_and_changes_nothing()
    {
        var o = NewOrder();
        Add(o, qty: 5m);
        o.Submit();
        o.BeginReceiving(Guid.NewGuid());
        o.ReceiveLine(o.Lines[0].Id, new OrderQuantity(3m));

        var refused = o.ReceiveLine(o.Lines[0].Id, new OrderQuantity(2.5m));

        Assert.Equal("Purchasing.PurchaseOrder.MoreThanOrdered", refused.Error.Code);
        Assert.Contains("Only 2", refused.Error.Description);
        Assert.Equal(3m, o.Lines[0].ReceivedQuantity);
        Assert.Equal(30m, o.ReceivedAmount.Amount);
    }

    [Fact]
    public void Nothing_can_be_received_on_a_draft_or_a_finished_order()
    {
        var draft = NewOrder();
        Add(draft);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", draft.ReceiveLine(draft.Lines[0].Id, new OrderQuantity(1m)).Error.Code);

        var closed = SubmittedWithLines(2);
        closed.BeginReceiving(Guid.NewGuid());
        ReceiveAll(closed, 0);
        closed.UpdateReceivingStatus();
        Assert.True(closed.CloseShort("supplier stopped the product").IsSuccess);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", closed.BeginReceiving(closed.WarehouseId!.Value).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", ReceiveAll(closed, 1).Error.Code);
    }

    [Fact]
    public void Close_short_needs_a_partly_received_order_and_a_reason()
    {
        var submitted = SubmittedWithLines(2);
        Assert.Contains("cancel it instead", submitted.CloseShort("x").Error.Description);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", NewOrder().CloseShort("x").Error.Code);

        var partial = SubmittedWithLines(2);
        partial.BeginReceiving(Guid.NewGuid());
        ReceiveAll(partial, 0);
        partial.UpdateReceivingStatus();
        Assert.Equal("Purchasing.PurchaseOrder.ClosingReasonRequired", partial.CloseShort(" ").Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.ClosingReasonTooLong", partial.CloseShort(new string('x', 501)).Error.Code);

        Assert.True(partial.CloseShort("  rest discontinued ").IsSuccess);
        Assert.Equal(PurchaseOrderStatus.Closed, partial.Status);
        Assert.Equal("rest discontinued", partial.ClosingReason);
        Assert.NotNull(partial.ClosedAt);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", partial.CloseShort("again").Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", partial.Cancel("too late").Error.Code);
    }

    [Fact]
    public void ResumedReceiving_MustUseTheSameWarehouse_OnceAnythingWasReceived()
    {
        var o = SubmittedWithLines(2);
        var first = Guid.NewGuid();
        o.BeginReceiving(first);
        o.ReceiveLine(o.Lines[0].Id, new OrderQuantity(0.5m));

        Assert.Equal("Purchasing.PurchaseOrder.WarehouseMismatch", o.BeginReceiving(Guid.NewGuid()).Error.Code);
        Assert.True(o.BeginReceiving(first).IsSuccess);
    }

    [Fact]
    public void BeforeAnyLineIsReceived_TheWarehouseMayStillChange()
    {
        var o = SubmittedWithLines();
        o.BeginReceiving(Guid.NewGuid());

        var other = Guid.NewGuid();
        Assert.True(o.BeginReceiving(other).IsSuccess);
        Assert.Equal(other, o.WarehouseId);
    }

    [Fact]
    public void Cancel_WorksFromDraftAndSubmitted_AndNeedsAReason()
    {
        var draft = NewOrder();
        Assert.Equal("Purchasing.PurchaseOrder.CancellationReasonRequired", draft.Cancel(" ").Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.CancellationReasonTooLong", draft.Cancel(new string('x', 501)).Error.Code);
        Assert.True(draft.Cancel("changed mind").IsSuccess);
        Assert.Equal(PurchaseOrderStatus.Cancelled, draft.Status);
        Assert.Equal("changed mind", draft.CancellationReason);
        Assert.NotNull(draft.CancelledAt);

        var submitted = SubmittedWithLines();
        Assert.True(submitted.Cancel("supplier unavailable").IsSuccess);
    }

    [Fact]
    public void Cancel_IsRejected_WhenCancelledReceivedOrPartiallyReceived()
    {
        var cancelled = NewOrder();
        cancelled.Cancel("x");
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", cancelled.Cancel("again").Error.Code);

        var received = SubmittedWithLines(1);
        received.BeginReceiving(Guid.NewGuid());
        ReceiveAll(received, 0);
        received.UpdateReceivingStatus();
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", received.Cancel("late").Error.Code);

        var partial = SubmittedWithLines(2);
        partial.BeginReceiving(Guid.NewGuid());
        partial.ReceiveLine(partial.Lines[0].Id, new OrderQuantity(1m));
        partial.UpdateReceivingStatus();
        var refused = partial.Cancel("oops");
        Assert.Equal("Purchasing.PurchaseOrder.PartiallyReceived", refused.Error.Code);
        Assert.Contains("Close it short", refused.Error.Description);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, partial.Status);
    }

    [Fact]
    public void UpdateReceivingStatus_RequiresAnOrderAwaitingGoods()
        => Assert.Equal("Purchasing.PurchaseOrder.InvalidState", NewOrder().UpdateReceivingStatus().Error.Code);

    // --- FIX-09b: supplier returns ---

    private static PurchaseOrder ReceivedOrder(decimal qty = 10m, decimal cost = 2.5m, decimal arrived = 10m)
    {
        var o = NewOrder();
        Add(o, qty: qty, cost: cost, name: "Water");
        o.Submit();
        o.BeginReceiving(Guid.NewGuid());
        o.ReceiveLine(o.Lines[0].Id, new OrderQuantity(arrived));
        o.UpdateReceivingStatus();
        return o;
    }

    [Fact]
    public void A_return_takes_received_goods_back_at_the_order_cost_from_the_order_warehouse()
    {
        var o = ReceivedOrder();
        var r = SupplierReturn.Start(o, "  damaged in transport ").Value;

        Assert.True(o.RecordReturn(r, o.Lines[0].Id, new OrderQuantity(3m)).IsSuccess);

        Assert.StartsWith("RT-", r.Number);
        Assert.Equal((o.Id, o.Number, o.SupplierId, o.WarehouseId!.Value, "damaged in transport"), (r.PurchaseOrderId, r.PurchaseOrderNumber, r.SupplierId, r.WarehouseId, r.Reason));
        var line = Assert.Single(r.Lines);
        Assert.Equal((o.Lines[0].Id, 3m, 2.5m, 7.5m), (line.PurchaseOrderLineId, line.Quantity.Value, line.UnitCost.Amount, line.LineTotal.Amount));
        Assert.Equal(7.5m, r.TotalAmount.Amount);
        Assert.Equal((3m, 7m), (o.Lines[0].ReturnedQuantity, o.Lines[0].ReturnableQuantity));
        Assert.Equal(PurchaseOrderStatus.Received, o.Status);   // a return does not reopen or change the order
    }

    [Fact]
    public void Never_more_than_was_received_minus_earlier_returns()
    {
        var o = ReceivedOrder(qty: 10m, arrived: 4m);   // part delivery: 4 arrived
        var first = SupplierReturn.Start(o, "wrong size").Value;
        Assert.Equal("Purchasing.SupplierReturn.MoreThanReceived", o.RecordReturn(first, o.Lines[0].Id, new OrderQuantity(5m)).Error.Code);
        Assert.True(o.RecordReturn(first, o.Lines[0].Id, new OrderQuantity(4m)).IsSuccess);

        var second = SupplierReturn.Start(o, "again").Value;
        var refused = o.RecordReturn(second, o.Lines[0].Id, new OrderQuantity(1m));

        Assert.Equal("Purchasing.SupplierReturn.MoreThanReceived", refused.Error.Code);
        Assert.Contains("Nothing more of 'Water", refused.Error.Description);
        Assert.Empty(second.Lines);
        Assert.Equal(4m, o.Lines[0].ReturnedQuantity);
    }

    [Fact]
    public void A_return_needs_received_goods_a_reason_and_its_own_order()
    {
        var draft = NewOrder();
        Add(draft);
        Assert.Equal("Purchasing.SupplierReturn.NothingReceived", SupplierReturn.Start(draft, "x").Error.Code);
        Assert.Equal("Purchasing.SupplierReturn.NothingReceived", SupplierReturn.Start(SubmittedWithLines(), "x").Error.Code);

        var o = ReceivedOrder();
        Assert.Equal("Purchasing.SupplierReturn.ReasonRequired", SupplierReturn.Start(o, " ").Error.Code);
        Assert.Equal("Purchasing.SupplierReturn.ReasonTooLong", SupplierReturn.Start(o, new string('x', 501)).Error.Code);

        var other = ReceivedOrder();
        var foreign = SupplierReturn.Start(other, "x").Value;
        Assert.Equal("Purchasing.SupplierReturn.OtherOrder", o.RecordReturn(foreign, o.Lines[0].Id, new OrderQuantity(1m)).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.LineNotFound", o.RecordReturn(SupplierReturn.Start(o, "x").Value, PurchaseOrderLineId.New(), new OrderQuantity(1m)).Error.Code);
    }

    [Fact]
    public void A_closed_short_order_can_still_return_what_arrived()
    {
        var o = ReceivedOrder(qty: 10m, arrived: 6m);
        o.CloseShort("rest discontinued");
        var r = SupplierReturn.Start(o, "faulty batch").Value;

        Assert.True(o.RecordReturn(r, o.Lines[0].Id, new OrderQuantity(6m)).IsSuccess);
        Assert.Equal(PurchaseOrderStatus.Closed, o.Status);
    }

    [Fact]
    public void Ids_AreUnique()
    {
        Assert.NotEqual(PurchaseOrderId.New(), PurchaseOrderId.New());
        Assert.NotEqual(PurchaseOrderLineId.New(), PurchaseOrderLineId.New());
        Assert.Equal(Guid.Empty, PurchaseOrderId.Empty.Value);
    }
}
