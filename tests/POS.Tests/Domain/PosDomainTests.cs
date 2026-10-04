using POS.Domain.Entities;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;

namespace POS.Tests.Domain;

public sealed class PosDomainTests
{
    private static readonly Guid Warehouse = Guid.NewGuid();

    private static PosSession NewSession(string cashier = "cashier-1")
    {
        var r = PosSession.Open(cashier, Warehouse);
        Assert.True(r.IsSuccess);
        return r.Value;
    }

    private static PosCart NewCart()
    {
        var r = PosCart.Start(PosSessionId.New());
        Assert.True(r.IsSuccess);
        return r.Value;
    }

    private static Platform.Core.Results.Result<PosCartItem> Add(
        PosCart cart, Guid? product = null, decimal qty = 1m, decimal price = 10m, string sku = "SKU", string name = "Item")
        => cart.AddItem(product ?? Guid.NewGuid(), sku, name, new CartQuantity(qty), new Money(price));

    // --- Session ---

    [Fact]
    public void Session_Open_StartsOpen_WithTrimmedCashier()
    {
        var s = NewSession("  alice ");

        Assert.Equal(PosSessionStatus.Open, s.Status);
        Assert.Equal("alice", s.CashierReference);
        Assert.Equal(Warehouse, s.WarehouseId);
        Assert.NotEqual(PosSessionId.Empty, s.Id);
        Assert.Null(s.ClosedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Session_Open_RequiresCashier(string cashier)
    {
        var r = PosSession.Open(cashier, Warehouse);

        Assert.True(r.IsFailure);
        Assert.Equal("POS.Session.CashierRequired", r.Error.Code);
    }

    [Fact]
    public void Session_Open_CashierTooLong_Fails()
        => Assert.Equal("POS.Session.CashierTooLong", PosSession.Open(new string('x', 101), Warehouse).Error.Code);

    [Fact]
    public void Session_Open_RequiresWarehouse()
        => Assert.Equal("POS.Session.WarehouseRequired", PosSession.Open("c", Guid.Empty).Error.Code);

    [Fact]
    public void Session_Close_SetsClosedAt_AndIsTerminal()
    {
        var s = NewSession();

        Assert.True(s.Close().IsSuccess);
        Assert.Equal(PosSessionStatus.Closed, s.Status);
        Assert.NotNull(s.ClosedAt);
        Assert.Equal("POS.Session.AlreadyClosed", s.Close().Error.Code);
    }

    // --- Cart lifecycle ---

    [Fact]
    public void Cart_Start_IsOpenAndEmpty_WithZeroTotals()
    {
        var c = NewCart();

        Assert.Equal(PosCartStatus.Open, c.Status);
        Assert.Empty(c.Items);
        Assert.Equal(0m, c.Subtotal.Amount);
        Assert.Equal(0m, c.Total.Amount);
        Assert.Null(c.SaleId);
    }

    [Fact]
    public void Cart_Start_RequiresSession()
        => Assert.Equal("POS.Cart.SessionRequired", PosCart.Start(PosSessionId.Empty).Error.Code);

    // --- Items ---

    [Fact]
    public void Cart_AddItem_SnapshotsProductValues()
    {
        var c = NewCart();
        var product = Guid.NewGuid();

        var r = c.AddItem(product, " SKU-1 ", " Widget ", new CartQuantity(3m), new Money(2.5m));

        Assert.True(r.IsSuccess);
        var item = Assert.Single(c.Items);
        Assert.Equal(product, item.CatalogProductId);
        Assert.Equal("SKU-1", item.ProductSku);
        Assert.Equal("Widget", item.ProductName);
        Assert.Equal(3m, item.Quantity.Value);
        Assert.Equal(2.5m, item.UnitPrice.Amount);
        Assert.Equal(7.5m, item.LineTotal.Amount);
        Assert.Equal(c.Id, item.CartId);
    }

    [Fact]
    public void Cart_AddSameProductTwice_MergesQuantity_KeepsOriginalPriceSnapshot()
    {
        var c = NewCart();
        var product = Guid.NewGuid();
        Add(c, product, qty: 2m, price: 10m);

        Add(c, product, qty: 3m, price: 99m); // later (different) price must not rewrite the snapshot

        var item = Assert.Single(c.Items);
        Assert.Equal(5m, item.Quantity.Value);
        Assert.Equal(10m, item.UnitPrice.Amount);
        Assert.Equal(50m, c.Total.Amount);
    }

    [Fact]
    public void Cart_Totals_AggregateLines()
    {
        var c = NewCart();
        Add(c, qty: 2m, price: 10m);
        Add(c, qty: 1m, price: 5.25m);

        Assert.Equal(25.25m, c.Subtotal.Amount);
        Assert.Equal(c.Subtotal, c.Total);
        Assert.Equal(3m, c.TotalQuantity);
    }

    [Theory]
    [InlineData("", "name", "POS.CartItem.SkuRequired")]
    [InlineData("sku", " ", "POS.CartItem.NameRequired")]
    public void Cart_AddItem_RequiresSkuAndName(string sku, string name, string code)
    {
        var r = Add(NewCart(), sku: sku, name: name);

        Assert.True(r.IsFailure);
        Assert.Equal(code, r.Error.Code);
    }

    [Fact]
    public void Cart_AddItem_RequiresProduct()
        => Assert.Equal("POS.CartItem.ProductRequired", Add(NewCart(), product: Guid.Empty).Error.Code);

    // --- Remove / change / clear ---

    [Fact]
    public void Cart_RemoveItem_RemovesLine_AndUnknownFails()
    {
        var c = NewCart();
        var product = Guid.NewGuid();
        Add(c, product);

        Assert.True(c.RemoveItem(product).IsSuccess);
        Assert.Empty(c.Items);
        Assert.Equal("POS.Cart.ItemNotFound", c.RemoveItem(product).Error.Code);
    }

    [Fact]
    public void Cart_ChangeQuantity_SetsQuantity_AndRecalculatesTotal()
    {
        var c = NewCart();
        var product = Guid.NewGuid();
        Add(c, product, qty: 1m, price: 4m);

        Assert.True(c.ChangeQuantity(product, new CartQuantity(5m)).IsSuccess);

        Assert.Equal(5m, c.Items[0].Quantity.Value);
        Assert.Equal(20m, c.Total.Amount);
        Assert.Equal("POS.Cart.ItemNotFound", c.ChangeQuantity(Guid.NewGuid(), new CartQuantity(1m)).Error.Code);
    }

    [Fact]
    public void Cart_Clear_RemovesAllItems()
    {
        var c = NewCart();
        Add(c); Add(c);

        Assert.True(c.Clear().IsSuccess);
        Assert.Empty(c.Items);
        Assert.Equal(0m, c.Total.Amount);
    }

    // --- Quantity / money validation ---

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void CartQuantity_NonPositive_Fails(double value)
    {
        var r = CartQuantity.Create((decimal)value);

        Assert.True(r.IsFailure);
        Assert.Equal("POS.CartQuantity.MustBePositive", r.Error.Code);
    }

    [Fact]
    public void CartQuantity_Positive_AllowsFractions()
        => Assert.Equal(0.5m, CartQuantity.Create(0.5m).Value.Value);

    [Fact]
    public void Money_Create_RoundsAndRejectsNegative()
    {
        Assert.Equal(1.2346m, Money.Create(1.23456m).Value.Amount);
        Assert.Equal("POS.Money.NegativeAmount", Money.Create(-1m).Error.Code);
    }

    // --- Checkout state transitions ---

    [Fact]
    public void Cart_MarkCheckedOut_SetsSaleId_AndIsTerminal()
    {
        var c = NewCart();
        var product = Guid.NewGuid();
        Add(c, product);
        var saleId = Guid.NewGuid();

        Assert.True(c.MarkCheckedOut(saleId).IsSuccess);

        Assert.Equal(PosCartStatus.CheckedOut, c.Status);
        Assert.Equal(saleId, c.SaleId);
        Assert.NotNull(c.CheckedOutAt);
        Assert.Equal("POS.Cart.NotOpen", Add(c).Error.Code);
        Assert.Equal("POS.Cart.NotOpen", c.RemoveItem(product).Error.Code);
        Assert.Equal("POS.Cart.NotOpen", c.ChangeQuantity(product, new CartQuantity(2m)).Error.Code);
        Assert.Equal("POS.Cart.NotOpen", c.Clear().Error.Code);
        Assert.Equal("POS.Cart.NotOpen", c.MarkCheckedOut(Guid.NewGuid()).Error.Code);
    }

    [Fact]
    public void Cart_MarkCheckedOut_EmptyCart_Fails()
        => Assert.Equal("POS.Cart.Empty", NewCart().MarkCheckedOut(Guid.NewGuid()).Error.Code);

    [Fact]
    public void Cart_MarkCheckedOut_RequiresSaleId()
    {
        var c = NewCart();
        Add(c);

        Assert.Equal("POS.Cart.SaleRequired", c.MarkCheckedOut(Guid.Empty).Error.Code);
        Assert.Equal(PosCartStatus.Open, c.Status);
    }

    [Fact]
    public void Ids_AreUnique_AndEmptyIsEmpty()
    {
        Assert.NotEqual(PosSessionId.New(), PosSessionId.New());
        Assert.NotEqual(PosCartId.New(), PosCartId.New());
        Assert.NotEqual(PosCartItemId.New(), PosCartItemId.New());
        Assert.Equal(Guid.Empty, PosSessionId.Empty.Value);
        Assert.Equal(Guid.Empty, PosCartId.Empty.Value);
        Assert.Equal(Guid.Empty, PosCartItemId.Empty.Value);
    }
}
