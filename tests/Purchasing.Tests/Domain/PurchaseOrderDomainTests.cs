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

    [Fact]
    public void ReceivingLines_CompletesTheOrder_OnlyWhenEveryLineIsReceived()
    {
        var o = SubmittedWithLines(2);
        o.BeginReceiving(Guid.NewGuid());

        Assert.True(o.MarkLineReceived(o.Lines[0].Id).IsSuccess);
        Assert.True(o.CompleteIfFullyReceived().IsSuccess);
        Assert.Equal(PurchaseOrderStatus.Submitted, o.Status);          // one line outstanding

        Assert.True(o.MarkLineReceived(o.Lines[1].Id).IsSuccess);
        Assert.True(o.CompleteIfFullyReceived().IsSuccess);
        Assert.Equal(PurchaseOrderStatus.Received, o.Status);
        Assert.NotNull(o.ReceivedAt);
    }

    [Fact]
    public void MarkLineReceived_Twice_OrUnknownLine_Fails()
    {
        var o = SubmittedWithLines(2);
        o.BeginReceiving(Guid.NewGuid());
        o.MarkLineReceived(o.Lines[0].Id);

        Assert.Equal("Purchasing.PurchaseOrder.LineAlreadyReceived", o.MarkLineReceived(o.Lines[0].Id).Error.Code);
        Assert.Equal("Purchasing.PurchaseOrder.LineNotFound", o.MarkLineReceived(PurchaseOrderLineId.New()).Error.Code);
    }

    [Fact]
    public void ResumedReceiving_MustUseTheSameWarehouse_OnceAnythingWasReceived()
    {
        var o = SubmittedWithLines(2);
        var first = Guid.NewGuid();
        o.BeginReceiving(first);
        o.MarkLineReceived(o.Lines[0].Id);

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
        received.MarkLineReceived(received.Lines[0].Id);
        received.CompleteIfFullyReceived();
        Assert.Equal("Purchasing.PurchaseOrder.InvalidState", received.Cancel("late").Error.Code);

        var partial = SubmittedWithLines(2);
        partial.BeginReceiving(Guid.NewGuid());
        partial.MarkLineReceived(partial.Lines[0].Id);
        Assert.Equal("Purchasing.PurchaseOrder.PartiallyReceived", partial.Cancel("oops").Error.Code);
        Assert.Equal(PurchaseOrderStatus.Submitted, partial.Status);
    }

    [Fact]
    public void CompleteIfFullyReceived_RequiresSubmitted()
        => Assert.Equal("Purchasing.PurchaseOrder.InvalidState", NewOrder().CompleteIfFullyReceived().Error.Code);

    [Fact]
    public void Ids_AreUnique()
    {
        Assert.NotEqual(PurchaseOrderId.New(), PurchaseOrderId.New());
        Assert.NotEqual(PurchaseOrderLineId.New(), PurchaseOrderLineId.New());
        Assert.Equal(Guid.Empty, PurchaseOrderId.Empty.Value);
    }
}
