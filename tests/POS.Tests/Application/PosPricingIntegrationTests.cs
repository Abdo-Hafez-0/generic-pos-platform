using Microsoft.Extensions.DependencyInjection;
using POS.Contracts.Interfaces;
using Pricing.Contracts.Interfaces;
using Pricing.Contracts.Models;

namespace POS.Tests.Application;

/// <summary>
/// POS consumes Pricing ONLY through Pricing.Contracts and only OPTIONALLY: without the module POS uses the Catalog sale price.
/// </summary>
public sealed class PosPricingIntegrationTests
{
    private sealed class StubResolver(Func<Guid, decimal, PriceResolutionResult> resolve) : IPriceResolver
    {
        public List<(Guid ProductId, decimal Quantity)> Calls { get; } = [];

        public Task<PriceResolutionResult> ResolveAsync(Guid productId, decimal quantity = 1m, DateTime? at = null, Guid? priceListId = null, CancellationToken cancellationToken = default)
        {
            Calls.Add((productId, quantity));
            return Task.FromResult(resolve(productId, quantity));
        }
    }

    private static async Task<(Guid CartId, Guid ProductId)> Setup(PosTestDatabase db, decimal catalogPrice)
    {
        var product = db.Catalog.Register("SKU-P", "Priced product", catalogPrice);
        db.Inventory.SetStock(product, 100m);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await service.OpenSessionAsync("cashier", Guid.NewGuid());
        var cart = await service.StartCartAsync(session.SessionId);
        return (cart.CartId, product);
    }

    private static async Task<decimal> AddAndReadPrice(PosTestDatabase db, Guid cartId, decimal qty = 1m)
    {
        using var scope = db.CreateScope();
        var add = await scope.ServiceProvider.GetRequiredService<IPOSService>().AddProductAsync(cartId, "SKU-P", qty);
        Assert.True(add.IsSuccess, add.ErrorMessage);
        var cart = await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cartId);
        return cart!.Items.Single().UnitPrice;
    }

    [Fact]
    public async Task WithoutThePricingModule_PosUsesTheCatalogPrice()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var (cart, _) = await Setup(db, 10m);

        Assert.Equal(10m, await AddAndReadPrice(db, cart));
    }

    [Fact]
    public async Task WithPricing_AResolvedPriceOverridesTheCatalogPrice_AndIsSnapshotted()
    {
        var resolver = new StubResolver((p, q) => new PriceResolutionResult(true, 7.25m, Guid.NewGuid(), Guid.NewGuid(), "RETAIL"));
        await using var db = await PosTestDatabase.CreateAsync(resolver);
        var (cart, product) = await Setup(db, 10m);

        var price = await AddAndReadPrice(db, cart, 3m);

        Assert.Equal(7.25m, price);
        Assert.Equal([(product, 3m)], resolver.Calls.ToArray());
    }

    [Fact]
    public async Task WithPricing_NoApplicablePrice_FallsBackToTheCatalogPrice()
    {
        var resolver = new StubResolver((_, _) => PriceResolutionResult.None);
        await using var db = await PosTestDatabase.CreateAsync(resolver);
        var (cart, _) = await Setup(db, 10m);

        Assert.Equal(10m, await AddAndReadPrice(db, cart));
        Assert.Single(resolver.Calls);
    }

    [Fact]
    public async Task ResolvedPrice_IsWhatCheckoutPassesToSales_NotTheCatalogPrice()
    {
        var resolver = new StubResolver((_, _) => new PriceResolutionResult(true, 6m, Guid.NewGuid(), Guid.NewGuid(), "RETAIL"));
        await using var db = await PosTestDatabase.CreateAsync(resolver);
        var (cart, _) = await Setup(db, 10m);
        await AddAndReadPrice(db, cart, 2m);

        using var scope = db.CreateScope();
        var checkout = await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cart);

        Assert.True(checkout.IsSuccess, checkout.ErrorMessage);
        Assert.Equal(6m, Assert.Single(db.Sales.Items).UnitPrice);
    }

    [Fact]
    public async Task PricingChangesAfterAddingToTheCart_DoNotChangeTheSnapshot()
    {
        var current = 5m;
        var resolver = new StubResolver((_, _) => new PriceResolutionResult(true, current, Guid.NewGuid(), Guid.NewGuid(), "RETAIL"));
        await using var db = await PosTestDatabase.CreateAsync(resolver);
        var (cart, _) = await Setup(db, 10m);
        await AddAndReadPrice(db, cart);

        current = 99m;
        using var scope = db.CreateScope();
        var after = (await scope.ServiceProvider.GetRequiredService<IPOSReader>().GetCartAsync(cart))!.Items.Single().UnitPrice;

        Assert.Equal(5m, after);
    }
}
