using Platform.Core.Results;
using Sales.Domain.Entities;
using Sales.Domain.Enums;
using Sales.Domain.Events;
using Sales.Domain.ValueObjects;

namespace Sales.Tests.Domain;

public sealed class SaleDomainTests
{
    private static Sale NewSale(string? reference = null, string? notes = null)
    {
        var result = Sale.Create(reference, notes);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Result<SaleItem> AddItem(
        Sale sale,
        decimal quantity = 2m,
        decimal unitPrice = 10m,
        decimal discount = 0m,
        decimal taxRate = 0m,
        Guid? productId = null,
        string name = "Widget",
        string sku = "W-1")
        => sale.AddItem(
            productId ?? Guid.NewGuid(), name, sku,
            SaleQuantity.Create(quantity).Value,
            Money.Create(unitPrice).Value,
            Money.Create(discount).Value,
            taxRate);

    private static Sale ConfirmedSale()
    {
        var sale = NewSale();
        Assert.True(AddItem(sale).IsSuccess);
        Assert.True(sale.Confirm().IsSuccess);
        return sale;
    }

    // --- Creation ---

    [Fact]
    public void Create_StartsInDraft_WithEmptyItemsAndZeroTotals()
    {
        var sale = NewSale("REF-1", "  a note ");

        Assert.Equal(SaleStatus.Draft, sale.Status);
        Assert.NotEqual(SaleId.Empty, sale.Id);
        Assert.Equal("REF-1", sale.Reference);
        Assert.Equal("a note", sale.Notes);
        Assert.Empty(sale.Items);
        Assert.Equal(0m, sale.GrandTotal.Amount);
        Assert.Null(sale.CompletedAt);
        Assert.Null(sale.CancelledAt);
    }

    [Fact]
    public void Create_RaisesSaleCreatedEvent()
    {
        var sale = NewSale();

        var evt = Assert.Single(sale.DomainEvents);
        var created = Assert.IsType<SaleCreatedEvent>(evt);
        Assert.Equal(sale.Id, created.SaleId);
    }

    [Fact]
    public void Create_ReferenceTooLong_Fails()
    {
        var result = Sale.Create(new string('x', 101));

        Assert.True(result.IsFailure);
        Assert.Equal("Sales.Sale.ReferenceTooLong", result.Error.Code);
    }

    [Fact]
    public void Create_NotesTooLong_Fails()
    {
        var result = Sale.Create(null, new string('x', 501));

        Assert.True(result.IsFailure);
        Assert.Equal("Sales.Sale.NotesTooLong", result.Error.Code);
    }

    [Fact]
    public void ClearDomainEvents_RemovesEvents()
    {
        var sale = NewSale();
        sale.ClearDomainEvents();
        Assert.Empty(sale.DomainEvents);
    }

    // --- Items ---

    [Fact]
    public void AddItem_InDraft_AddsItemAndSnapshotsValues()
    {
        var sale = NewSale();
        var productId = Guid.NewGuid();

        var result = AddItem(sale, quantity: 3m, unitPrice: 20m, discount: 5m, taxRate: 0.1m,
            productId: productId, name: "  Gadget ", sku: " G-9 ");

        Assert.True(result.IsSuccess);
        var item = Assert.Single(sale.Items);
        Assert.Equal(productId, item.CatalogProductId);
        Assert.Equal("Gadget", item.ProductName);
        Assert.Equal("G-9", item.ProductSku);
        Assert.Equal(3m, item.Quantity.Value);
        Assert.Equal(20m, item.UnitPrice.Amount);
        Assert.Equal(5m, item.Discount.Amount);
        Assert.Equal(0.1m, item.TaxRate);
        Assert.Equal(sale.Id, item.SaleId);
    }

    [Fact]
    public void Item_ComputesSubTotalTaxAndLineTotal()
    {
        var sale = NewSale();
        AddItem(sale, quantity: 3m, unitPrice: 20m, discount: 10m, taxRate: 0.1m);

        // FIX-08b: prices include tax; the tax is the part of the line total that is tax, rounded per line
        var item = sale.Items[0];
        Assert.Equal(50m, item.LineTotal.Amount);    // 3*20 - 10, what the customer pays
        Assert.Equal(4.55m, item.TaxAmount.Amount);  // 50 * 0.1 / 1.1 = 4.5454...
        Assert.Equal(45.45m, item.SubTotal.Amount);  // before tax
    }

    [Fact]
    public void Sale_Totals_AggregateAllItems()
    {
        var sale = NewSale();
        AddItem(sale, quantity: 2m, unitPrice: 10m, taxRate: 0.1m);  // line 20, tax 20*0.1/1.1 = 1.82, net 18.18
        AddItem(sale, quantity: 1m, unitPrice: 30m, discount: 10m, taxRate: 0.5m); // line 20, tax 20*0.5/1.5 = 6.67, net 13.33

        Assert.Equal(40m, sale.GrandTotal.Amount);   // the sums of the rounded lines
        Assert.Equal(8.49m, sale.TaxTotal.Amount);
        Assert.Equal(31.51m, sale.SubTotal.Amount);
    }

    [Fact]
    public void AddItem_InvalidTaxRate_Fails()
    {
        var sale = NewSale();

        Assert.Equal("Sales.SaleItem.TaxRateInvalid", AddItem(sale, taxRate: 1.5m).Error.Code);
        Assert.Equal("Sales.SaleItem.TaxRateInvalid", AddItem(sale, taxRate: -0.1m).Error.Code);
        Assert.Empty(sale.Items);
    }

    [Fact]
    public void AddItem_DiscountExceedingLineTotal_Fails()
    {
        var sale = NewSale();

        var result = AddItem(sale, quantity: 1m, unitPrice: 10m, discount: 11m);

        Assert.True(result.IsFailure);
        Assert.Equal("Sales.SaleItem.DiscountExceedsLineTotal", result.Error.Code);
    }

    [Fact]
    public void AddItem_EmptyProductId_Fails()
    {
        var sale = NewSale();

        var result = AddItem(sale, productId: Guid.Empty);

        Assert.Equal("Sales.SaleItem.ProductRequired", result.Error.Code);
    }

    [Theory]
    [InlineData("", "SKU", "Sales.SaleItem.ProductNameRequired")]
    [InlineData("  ", "SKU", "Sales.SaleItem.ProductNameRequired")]
    [InlineData("Name", "", "Sales.SaleItem.ProductSkuRequired")]
    [InlineData("Name", " ", "Sales.SaleItem.ProductSkuRequired")]
    public void AddItem_MissingNameOrSku_Fails(string name, string sku, string expectedCode)
    {
        var sale = NewSale();

        var result = AddItem(sale, name: name, sku: sku);

        Assert.True(result.IsFailure);
        Assert.Equal(expectedCode, result.Error.Code);
    }

    [Fact]
    public void RemoveItem_InDraft_RemovesItem()
    {
        var sale = NewSale();
        var item = AddItem(sale).Value;

        var result = sale.RemoveItem(item.Id);

        Assert.True(result.IsSuccess);
        Assert.Empty(sale.Items);
    }

    [Fact]
    public void RemoveItem_Unknown_Fails()
    {
        var sale = NewSale();

        var result = sale.RemoveItem(SaleItemId.New());

        Assert.Equal("Sales.Sale.ItemNotFound", result.Error.Code);
    }

    [Fact]
    public void AddOrRemoveItem_AfterConfirm_Fails()
    {
        var sale = ConfirmedSale();
        var existing = sale.Items[0];

        Assert.Equal("Sales.Sale.NotDraft", AddItem(sale).Error.Code);
        Assert.Equal("Sales.Sale.NotDraft", sale.RemoveItem(existing.Id).Error.Code);
        Assert.Single(sale.Items);
    }

    // --- Confirm ---

    [Fact]
    public void Confirm_Draft_WithItems_MovesToConfirmed()
    {
        var sale = NewSale();
        AddItem(sale);

        var result = sale.Confirm();

        Assert.True(result.IsSuccess);
        Assert.Equal(SaleStatus.Confirmed, sale.Status);
    }

    [Fact]
    public void Confirm_WithoutItems_Fails()
    {
        var sale = NewSale();

        var result = sale.Confirm();

        Assert.Equal("Sales.Sale.NoItems", result.Error.Code);
        Assert.Equal(SaleStatus.Draft, sale.Status);
    }

    [Fact]
    public void Confirm_WhenNotDraft_Fails()
    {
        var sale = ConfirmedSale();

        var result = sale.Confirm();

        Assert.Equal("Sales.Sale.CannotConfirm", result.Error.Code);
    }

    // --- Complete ---

    [Fact]
    public void Complete_Confirmed_MovesToCompleted_SetsCompletedAt_AndRaisesEvent()
    {
        var sale = ConfirmedSale();
        sale.ClearDomainEvents();

        var result = sale.Complete();

        Assert.True(result.IsSuccess);
        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.NotNull(sale.CompletedAt);
        var completed = Assert.IsType<SaleCompletedEvent>(Assert.Single(sale.DomainEvents));
        Assert.Equal(sale.Id, completed.SaleId);
    }

    [Fact]
    public void Complete_FromDraft_Fails()
    {
        var sale = NewSale();
        AddItem(sale);

        var result = sale.Complete();

        Assert.Equal("Sales.Sale.CannotComplete", result.Error.Code);
        Assert.Equal(SaleStatus.Draft, sale.Status);
    }

    [Fact]
    public void Complete_Twice_Fails()
    {
        var sale = ConfirmedSale();
        sale.Complete();

        Assert.Equal("Sales.Sale.CannotComplete", sale.Complete().Error.Code);
    }

    [Fact]
    public void CompletedSale_ItemsAreFrozen()
    {
        var sale = ConfirmedSale();
        sale.Complete();

        Assert.Equal("Sales.Sale.NotDraft", AddItem(sale).Error.Code);
    }

    // --- Cancel ---

    [Fact]
    public void Cancel_Draft_MovesToCancelled_WithReason_AndRaisesEvent()
    {
        var sale = NewSale();
        sale.ClearDomainEvents();

        var result = sale.Cancel("  customer left ");

        Assert.True(result.IsSuccess);
        Assert.Equal(SaleStatus.Cancelled, sale.Status);
        Assert.Equal("customer left", sale.CancellationReason);
        Assert.NotNull(sale.CancelledAt);
        Assert.IsType<SaleCancelledEvent>(Assert.Single(sale.DomainEvents));
    }

    [Fact]
    public void Cancel_Confirmed_Succeeds()
    {
        var sale = ConfirmedSale();

        Assert.True(sale.Cancel("changed mind").IsSuccess);
        Assert.Equal(SaleStatus.Cancelled, sale.Status);
    }

    [Fact]
    public void Cancel_Completed_Fails()
    {
        var sale = ConfirmedSale();
        sale.Complete();

        var result = sale.Cancel("too late");

        Assert.Equal("Sales.Sale.AlreadyCompleted", result.Error.Code);
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    [Fact]
    public void Cancel_Twice_Fails()
    {
        var sale = NewSale();
        sale.Cancel("first");

        Assert.Equal("Sales.Sale.AlreadyCancelled", sale.Cancel("second").Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Cancel_WithoutReason_Fails(string? reason)
    {
        var sale = NewSale();

        var result = sale.Cancel(reason!);

        Assert.Equal("Sales.Sale.CancellationReasonRequired", result.Error.Code);
        Assert.Equal(SaleStatus.Draft, sale.Status);
    }

    [Fact]
    public void Cancel_ReasonTooLong_Fails()
    {
        var sale = NewSale();

        var result = sale.Cancel(new string('x', 501));

        Assert.Equal("Sales.Sale.CancellationReasonTooLong", result.Error.Code);
    }

    [Fact]
    public void CancelledSale_CannotBeConfirmedOrCompleted()
    {
        var sale = ConfirmedSale();
        sale.Cancel("nope");

        Assert.Equal("Sales.Sale.CannotConfirm", sale.Confirm().Error.Code);
        Assert.Equal("Sales.Sale.CannotComplete", sale.Complete().Error.Code);
    }
}
