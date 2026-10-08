using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using POS.Application.Commands;
using POS.Application.Devices;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using POS.Domain.Entities;
using POS.Domain.ValueObjects;
using Pricing.Contracts.Interfaces;
using Pricing.Contracts.Models;

namespace POS.Tests.Application;

/// <summary>
/// FIX-08c: discounts at the till. A discount is kept as given (10%, or 2.00 off) and priced every time: a line discount on its line, a cart
/// discount spread over the lines; Sales receives each line's total discount; a permission and a configured maximum guard them.
/// </summary>
public sealed class PosDiscountTests
{
    // ------------------------------------------------------------------ the rule and the spread

    [Theory]
    [InlineData(DiscountKind.Percent, 0, "POS.Discount.MustBePositive")]
    [InlineData(DiscountKind.Amount, -1, "POS.Discount.MustBePositive")]
    [InlineData(DiscountKind.Percent, 100.5, "POS.Discount.PercentTooHigh")]
    [InlineData((DiscountKind)9, 5, "POS.Discount.KindInvalid")]
    public void A_discount_must_be_a_positive_percentage_up_to_100_or_a_positive_amount(DiscountKind kind, double value, string code)
        => Assert.Equal(code, DiscountRule.Create(kind, (decimal)value).Error.Code);

    [Fact]
    public void A_percentage_is_priced_against_what_it_applies_to_and_an_amount_is_never_more_than_it()
    {
        var tenPercent = DiscountRule.Create(DiscountKind.Percent, 10m).Value;
        var twoOff = DiscountRule.Create(DiscountKind.Amount, 2m).Value;

        Assert.Equal(0.75m, tenPercent.AmountOf(7.5m));
        Assert.Equal(0.33m, DiscountRule.Create(DiscountKind.Percent, 3.3m).Value.AmountOf(10m));
        Assert.Equal(2m, twoOff.AmountOf(7.5m));
        Assert.Equal(1.5m, twoOff.AmountOf(1.5m));                // capped
        Assert.Equal(20m, twoOff.PercentOf(10m));                  // for the maximum check
    }

    [Fact]
    public void A_cart_discount_is_spread_in_proportion_adding_up_exactly_with_the_cent_on_the_largest_line()
    {
        var shares = CartDiscountAllocation.Spread(1m, [7.5m, 2.4m, 0.1m]);

        Assert.Equal(1m, shares.Sum());
        Assert.Equal([0.75m, 0.24m, 0.01m], shares);
        Assert.Equal([3.33m, 3.34m, 3.33m], CartDiscountAllocation.Spread(10m, [10m, 10.01m, 10m]).Select(s => s).ToArray());   // remainder to the largest
        Assert.Equal([10m, 5m], CartDiscountAllocation.Spread(99m, [10m, 5m]));   // never more than the lines
        Assert.Equal([0m, 0m], CartDiscountAllocation.Spread(0m, [10m, 5m]));
    }

    // ------------------------------------------------------------------ the cart

    private static PosCart Cart(params (string Sku, decimal Qty, decimal Price, decimal Rate)[] lines)
    {
        var cart = PosCart.Start(PosSessionId.New()).Value;
        foreach (var (sku, qty, price, rate) in lines)
            cart.AddItem(Guid.NewGuid(), sku, sku, new CartQuantity(qty), new Money(price), rate);
        return cart;
    }

    [Fact]
    public void Line_and_cart_discounts_lower_the_total_and_the_tax_inside_it()
    {
        var cart = Cart(("COLA-1", 3m, 2.5m, 0.14m), ("BREAD-1", 2m, 1.2m, 0m));
        cart.SetLineDiscount(cart.Items[0].CatalogProductId, DiscountRule.Create(DiscountKind.Percent, 10m).Value);   // 7.50 -> 6.75
        cart.SetCartDiscount(DiscountRule.Create(DiscountKind.Amount, 1m).Value);                                     // spread over 6.75 + 2.40

        Assert.Equal(9.9m, cart.Subtotal.Amount);
        Assert.Equal(1.75m, cart.DiscountTotal.Amount);            // 0.75 + 1.00
        Assert.Equal(8.15m, cart.Total.Amount);
        var cola = cart.PricedLines[0];
        Assert.Equal((0.74m, 1.49m, 6.01m), (cola.CartShare, cola.Amounts.Discount, cola.Amounts.Total));   // 1.00 x 6.75/9.15
        Assert.Equal(0.74m, cart.TaxTotal.Amount);                 // 6.01 x 0.14 / 1.14
        Assert.Equal(cart.Total.Amount, cart.PricedLines.Sum(l => l.Amounts.Total));
    }

    [Fact]
    public void A_percentage_follows_quantity_changes_and_an_amount_is_capped_when_the_line_shrinks()
    {
        var cart = Cart(("COLA-1", 2m, 2.5m, 0m), ("CHIPS-1", 4m, 1m, 0m));
        var (cola, chips) = (cart.Items[0].CatalogProductId, cart.Items[1].CatalogProductId);
        cart.SetLineDiscount(cola, DiscountRule.Create(DiscountKind.Percent, 10m).Value);
        cart.SetLineDiscount(chips, DiscountRule.Create(DiscountKind.Amount, 3m).Value);

        cart.ChangeQuantity(cola, new CartQuantity(4m));
        cart.ChangeQuantity(chips, new CartQuantity(2m));

        Assert.Equal(1m, cart.PricedLines[0].Amounts.Discount);     // 10% of 10.00
        Assert.Equal(2m, cart.PricedLines[1].Amounts.Discount);     // 3.00 off, but the line is now 2.00
        Assert.Equal(0m, cart.PricedLines[1].Amounts.Total);
    }

    [Fact]
    public void A_cart_discount_needs_products_and_clearing_the_cart_removes_it()
    {
        var empty = PosCart.Start(PosSessionId.New()).Value;
        Assert.Equal("POS.Cart.Empty", empty.SetCartDiscount(DiscountRule.Create(DiscountKind.Percent, 5m).Value).Error.Code);

        var cart = Cart(("COLA-1", 1m, 2.5m, 0m));
        cart.SetCartDiscount(DiscountRule.Create(DiscountKind.Percent, 5m).Value);
        cart.Clear();
        Assert.Null(cart.CartDiscount);
    }

    // ------------------------------------------------------------------ the handlers, through IPOSService

    private sealed class StubTax : ITaxRateResolver
    {
        public Task<TaxResolutionResult> ResolveAsync(Guid productId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaxResolutionResult(true, 0.14m, Guid.NewGuid(), "STD", "Standard"));
    }

    private static async Task<(PosTestDatabase Db, Guid Cart, Guid Cola)> TillAsync(decimal maximumPercent = 100m, bool allowed = true)
    {
        var db = await PosTestDatabase.CreateAsync(configureServices: s =>
        {
            s.AddSingleton<ITaxRateResolver>(new StubTax());
            s.AddSingleton(new PosDiscountOptions { MaximumPercent = maximumPercent });
            if (!allowed)   // a cashier who may sell but not give discounts
                s.AddSingleton<IAuthorizationService>(new global::Tests.Common.Security.ScriptedAuthorizationService(
                    POS.Application.Security.POSCapabilities.ManageSession, POS.Application.Security.POSCapabilities.CreateSale));
        });
        var cola = db.Catalog.Register("COLA-1", "Cola", 2.5m);
        db.Inventory.SetStock(cola, 10m);
        using var scope = db.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await pos.OpenSessionAsync("cashier", Guid.NewGuid());
        var cart = await pos.StartCartAsync(session.SessionId);
        Assert.True((await pos.AddProductAsync(cart.CartId, "COLA-1", 4m)).IsSuccess);
        return (db, cart.CartId, cola);
    }

    private static async Task<T> PosAsync<T>(PosTestDatabase db, Func<IPOSService, Task<T>> call)
    {
        using var scope = db.CreateScope();
        return await call(scope.ServiceProvider.GetRequiredService<IPOSService>());
    }

    private static Task<POSCartResult> ReadAsync(PosTestDatabase db, Guid cart)
        => PosAsync(db, async _ =>
        {
            using var scope = db.CreateScope();
            return (await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cart))!;
        });

    [Fact]
    public async Task Discounts_given_at_the_till_reach_Sales_line_by_line()
    {
        var (db, cart, cola) = await TillAsync();
        await using var _ = db;

        Assert.True((await PosAsync(db, p => p.SetLineDiscountAsync(cart, cola, POSDiscountKind.Percent, 10m))).IsSuccess);
        Assert.True((await PosAsync(db, p => p.SetCartDiscountAsync(cart, POSDiscountKind.Amount, 1m))).IsSuccess);

        var read = await ReadAsync(db, cart);
        Assert.Equal((10m, 2m, 8m), (read.Subtotal, read.DiscountTotal, read.Total));   // 10.00 - 1.00 (10%) - 1.00
        Assert.Equal((POSDiscountKind.Percent, 10m), (read.Items.Single().LineDiscountKind, read.Items.Single().LineDiscountValue));
        Assert.Equal((POSDiscountKind.Amount, 1m), (read.CartDiscountKind, read.CartDiscountValue));

        Assert.True((await PosAsync(db, p => p.CheckoutAsync(cart))).IsSuccess);
        var sold = db.Sales.Items.Single();
        Assert.Equal((2.5m, 4m, 2m, 0.14m), (sold.UnitPrice, sold.Quantity, sold.Discount, sold.TaxRate));
    }

    [Fact]
    public async Task Zero_removes_a_discount()
    {
        var (db, cart, cola) = await TillAsync();
        await using var _ = db;
        await PosAsync(db, p => p.SetLineDiscountAsync(cart, cola, POSDiscountKind.Amount, 1m));
        await PosAsync(db, p => p.SetCartDiscountAsync(cart, POSDiscountKind.Percent, 5m));

        Assert.True((await PosAsync(db, p => p.SetLineDiscountAsync(cart, cola, POSDiscountKind.Amount, 0m))).IsSuccess);
        Assert.True((await PosAsync(db, p => p.SetCartDiscountAsync(cart, POSDiscountKind.Percent, 0m))).IsSuccess);

        var read = await ReadAsync(db, cart);
        Assert.Equal((0m, 10m, (POSDiscountKind?)null), (read.DiscountTotal, read.Total, read.CartDiscountKind));
    }

    [Fact]
    public async Task The_maximum_and_the_amount_of_the_line_are_enforced_in_plain_words()
    {
        var (db, cart, cola) = await TillAsync(maximumPercent: 20m);
        await using var _ = db;

        var tooMuch = await PosAsync(db, p => p.SetLineDiscountAsync(cart, cola, POSDiscountKind.Percent, 25m));
        var asAmount = await PosAsync(db, p => p.SetCartDiscountAsync(cart, POSDiscountKind.Amount, 2.5m));   // 25% of 10.00
        var moreThanLine = await PosAsync(db, p => p.SetLineDiscountAsync(cart, cola, POSDiscountKind.Amount, 12m));
        var notInCart = await PosAsync(db, p => p.SetLineDiscountAsync(cart, Guid.NewGuid(), POSDiscountKind.Percent, 5m));

        Assert.Equal(("POS.Discount.AboveMaximum", "A discount can be at most 20% here."), (tooMuch.ErrorCode, tooMuch.ErrorMessage));
        Assert.Equal("POS.Discount.AboveMaximum", asAmount.ErrorCode);
        Assert.Equal("POS.Discount.MoreThanTheAmount", moreThanLine.ErrorCode);
        Assert.Equal("POS.Cart.ItemNotFound", notInCart.ErrorCode);
        Assert.Equal(0m, (await ReadAsync(db, cart)).DiscountTotal);
        Assert.True((await PosAsync(db, p => p.SetLineDiscountAsync(cart, cola, POSDiscountKind.Percent, 20m))).IsSuccess);
    }

    [Fact]
    public async Task Without_the_permission_no_discount_can_be_given()
    {
        var (db, cart, cola) = await TillAsync(allowed: false);
        await using var _ = db;

        var refused = await PosAsync(db, p => p.SetLineDiscountAsync(cart, cola, POSDiscountKind.Percent, 5m));

        Assert.False(refused.IsSuccess);
        Assert.Equal(0m, (await ReadAsync(db, cart)).DiscountTotal);
    }

    [Fact]
    public void The_receipt_lines_carry_their_discount_and_the_taxes_are_after_discounts()
    {
        var session = PosSession.Open("cashier", Guid.NewGuid()).Value;
        var cart = PosCart.Start(session.Id).Value;
        cart.AddItem(Guid.NewGuid(), "COLA-1", "Cola", new CartQuantity(4m), new Money(2.5m), 0.14m);
        cart.SetCartDiscount(DiscountRule.Create(DiscountKind.Amount, 2m).Value);

        var receipt = PosReceiptFactory.Create(cart, session, Guid.NewGuid(), null, new PosReceiptOptions(), DateTimeOffset.UtcNow);

        var line = Assert.Single(receipt.Lines);
        Assert.Equal((8m, 2m), (line.LineTotal, line.Discount));
        Assert.Equal(8m, receipt.Total);
        Assert.Equal(0.98m, Assert.Single(receipt.Taxes!).Amount);   // 8.00 x 0.14 / 1.14
    }
}
