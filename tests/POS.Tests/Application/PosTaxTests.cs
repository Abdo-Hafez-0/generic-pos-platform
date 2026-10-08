using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Amounts;
using POS.Application.Devices;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using POS.Domain.Entities;
using POS.Domain.ValueObjects;
using Pricing.Contracts.Interfaces;
using Pricing.Contracts.Models;
using Tests.Common.Hardware;

namespace POS.Tests.Application;

/// <summary>
/// FIX-08b: prices include tax. The till snapshots each line's tax rate (from Pricing's ITaxRateResolver, optional) like its price, shows the
/// tax contained in the total, hands the rate to Sales and prints it on the receipt. One rule for every amount: TaxInclusiveLine.
/// </summary>
public sealed class PosTaxTests
{
    private sealed class StubTax(Func<Guid, TaxResolutionResult> resolve) : ITaxRateResolver
    {
        public Task<TaxResolutionResult> ResolveAsync(Guid productId, CancellationToken cancellationToken = default) => Task.FromResult(resolve(productId));
    }

    private static TaxResolutionResult Rate(decimal rate) => new(true, rate, Guid.NewGuid(), "R" + rate, "Rate " + rate);

    private static async Task<(Guid Session, Guid Cart)> TillAsync(PosTestDatabase db)
    {
        using var scope = db.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await pos.OpenSessionAsync("cashier", Guid.NewGuid());
        var cart = await pos.StartCartAsync(session.SessionId);
        return (session.SessionId, cart.CartId);
    }

    private static async Task AddAsync(PosTestDatabase db, Guid cart, string sku, decimal qty)
    {
        using var scope = db.CreateScope();
        var added = await scope.ServiceProvider.GetRequiredService<IPOSService>().AddProductAsync(cart, sku, qty);
        Assert.True(added.IsSuccess, added.ErrorMessage);
    }

    private static async Task<POSCartResult> CartAsync(PosTestDatabase db, Guid cart)
    {
        using var scope = db.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cart))!;
    }

    // ------------------------------------------------------------------ the one rule

    [Theory]
    [InlineData(2.50, 2, 0, 0.14, 5.00, 0.61, 4.39)]       // 5.00 * 0.14 / 1.14 = 0.614...
    [InlineData(10, 3, 5, 0.10, 25.00, 2.27, 22.73)]       // a discount comes off the tax-included amount
    [InlineData(1.99, 3, 0, 0, 5.97, 0, 5.97)]             // no tax
    [InlineData(0.333, 3, 0, 0.14, 1.00, 0.12, 0.88)]      // rounded per line: 0.999 -> 1.00
    [InlineData(4, 1, 9, 0.14, 0, 0, 0)]                   // a discount larger than the line is capped at the line
    public void The_line_rule_extracts_the_tax_from_the_tax_included_amount_rounded_per_line(
        double unit, double qty, double discount, double rate, double total, double tax, double net)
    {
        var line = TaxInclusiveLine.Compute((decimal)unit, (decimal)qty, (decimal)discount, (decimal)rate);

        Assert.Equal((decimal)total, line.Total);
        Assert.Equal((decimal)tax, line.Tax);
        Assert.Equal((decimal)net, line.Net);
        Assert.Equal(line.Total, line.Net + line.Tax);
        Assert.Equal(line.Gross - line.Discount, line.Total);
    }

    [Fact]
    public void A_cart_line_refuses_a_rate_outside_0_to_100_percent()
    {
        var cart = PosCart.Start(PosSessionId.New()).Value;

        var added = cart.AddItem(Guid.NewGuid(), "SKU", "Product", new CartQuantity(1m), new Money(1m), 1.5m);

        Assert.Equal("POS.CartItem.TaxRateInvalid", added.Error.Code);
    }

    // ------------------------------------------------------------------ the till

    [Fact]
    public async Task Without_the_Pricing_module_a_line_has_no_tax()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var cola = db.Catalog.Register("COLA-1", "Cola", 2.5m);
        db.Inventory.SetStock(cola, 10m);
        var (_, cart) = await TillAsync(db);

        await AddAsync(db, cart, "COLA-1", 2m);

        var read = await CartAsync(db, cart);
        Assert.Equal((5m, 0m, 0m), (read.Total, read.TaxTotal, read.Items.Single().TaxRate));
    }

    [Fact]
    public async Task Each_line_snapshots_its_rate_the_total_stays_the_shelf_price_and_the_tax_inside_it_is_shown()
    {
        var rates = new Dictionary<Guid, decimal>();
        await using var db = await PosTestDatabase.CreateAsync(configureServices: s => s.AddSingleton<ITaxRateResolver>(new StubTax(p => Rate(rates[p]))));
        var cola = db.Catalog.Register("COLA-1", "Cola", 2.5m);
        var bread = db.Catalog.Register("BREAD-1", "Bread", 1.2m);
        (rates[cola], rates[bread]) = (0.14m, 0m);
        db.Inventory.SetStock(cola, 10m);
        db.Inventory.SetStock(bread, 10m);
        var (_, cart) = await TillAsync(db);

        await AddAsync(db, cart, "COLA-1", 2m);
        await AddAsync(db, cart, "BREAD-1", 1m);

        var read = await CartAsync(db, cart);
        Assert.Equal(6.2m, read.Total);                 // 5.00 + 1.20: prices include tax, so the customer pays the shelf prices
        Assert.Equal(6.2m, read.Subtotal);
        Assert.Equal(0.61m, read.TaxTotal);             // only the cola carries tax
        var colaLine = read.Items.Single(i => i.ProductSku == "COLA-1");
        Assert.Equal((0.14m, 0.61m, 5m), (colaLine.TaxRate, colaLine.TaxAmount, colaLine.LineTotal));
    }

    [Fact]
    public async Task A_rate_change_after_adding_does_not_change_the_line_and_more_of_the_same_product_keeps_the_first_snapshot()
    {
        var rate = 0.14m;
        await using var db = await PosTestDatabase.CreateAsync(configureServices: s => s.AddSingleton<ITaxRateResolver>(new StubTax(_ => Rate(rate))));
        var cola = db.Catalog.Register("COLA-1", "Cola", 2.5m);
        db.Inventory.SetStock(cola, 10m);
        var (_, cart) = await TillAsync(db);
        await AddAsync(db, cart, "COLA-1", 1m);

        rate = 0.20m;                                   // a new law while the customer is at the till
        await AddAsync(db, cart, "COLA-1", 1m);

        var line = (await CartAsync(db, cart)).Items.Single();
        Assert.Equal((2m, 0.14m, 0.61m), (line.Quantity, line.TaxRate, line.TaxAmount));
    }

    [Fact]
    public async Task Checkout_hands_each_lines_snapshot_rate_to_Sales()
    {
        await using var db = await PosTestDatabase.CreateAsync(configureServices: s => s.AddSingleton<ITaxRateResolver>(new StubTax(_ => Rate(0.14m))));
        var cola = db.Catalog.Register("COLA-1", "Cola", 2.5m);
        db.Inventory.SetStock(cola, 10m);
        var (_, cart) = await TillAsync(db);
        await AddAsync(db, cart, "COLA-1", 2m);

        using (var scope = db.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cart)).IsSuccess);

        var sold = db.Sales.Items.Single();
        Assert.Equal((2.5m, 2m, 0.14m, 0m), (sold.UnitPrice, sold.Quantity, sold.TaxRate, sold.Discount));
    }

    [Fact]
    public void The_receipt_shows_the_tax_contained_in_the_total_per_rate()
    {
        var session = PosSession.Open("cashier", Guid.NewGuid()).Value;
        var cart = PosCart.Start(session.Id).Value;
        cart.AddItem(Guid.NewGuid(), "COLA-1", "Cola", new CartQuantity(2m), new Money(2.5m), 0.14m);
        cart.AddItem(Guid.NewGuid(), "CHIPS-1", "Chips", new CartQuantity(1m), new Money(1.14m), 0.14m);
        cart.AddItem(Guid.NewGuid(), "BREAD-1", "Bread", new CartQuantity(1m), new Money(1.2m), 0m);

        var receipt = PosReceiptFactory.Create(cart, session, Guid.NewGuid(), null, new PosReceiptOptions(), DateTimeOffset.UtcNow);

        Assert.Equal(7.34m, receipt.Total);
        var tax = Assert.Single(receipt.Taxes!);                        // the zero-rate bread prints no tax line
        Assert.Equal((0.14m, 0.75m), (tax.Rate, tax.Amount));           // 0.61 + 0.14, the sum of the rounded lines
    }
}
