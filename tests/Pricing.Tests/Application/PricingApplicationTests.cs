using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pricing.Application.Commands;
using Pricing.Application.Queries;
using Pricing.Contracts.Interfaces;
using Pricing.Infrastructure.DependencyInjection;
using Pricing.Infrastructure.Persistence;
using Tests.Common;

namespace Pricing.Tests.Application;

/// <summary>Stub of Catalog.Contracts: Pricing tests never touch the Catalog implementation.</summary>
public sealed class StubCatalog : IProductLookup
{
    private readonly Dictionary<Guid, ProductLookupResult> _products = [];

    public Guid Register(string sku)
    {
        var id = Guid.NewGuid();
        _products[id] = new ProductLookupResult(id, sku, "Product " + sku, null, Guid.NewGuid(), "Cat", Guid.NewGuid(), "Each", "ea", 10m, null, ProductStatusContract.Active);
        return id;
    }

    public Task<ProductLookupResult?> FindByIdAsync(Guid productId, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.GetValueOrDefault(productId));

    public Task<ProductLookupResult?> FindBySkuAsync(string sku, CancellationToken cancellationToken = default)
        => Task.FromResult(_products.Values.FirstOrDefault(p => p.Sku == sku));
}

public sealed class PricingApplicationTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Env(TestModuleDatabase<PricingDbContext> Db, StubCatalog Catalog);

    private static async Task<Env> NewEnv()
    {
        var catalog = new StubCatalog();
        var db = await TestModuleDatabase<PricingDbContext>.CreateAsync(s =>
        {
            s.AddPricingCore();
            s.AddSingleton<IProductLookup>(catalog);
        });
        return new Env(db, catalog);
    }

    private static Task<Platform.Core.Results.Result<Guid>> List(Env e, string code = "RETAIL", bool makeDefault = false)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand(code, code + " list", makeDefault)));

    private static Task<Platform.Core.Results.Result<Guid>> Price(Env e, string sku, decimal amount, DateTime? from = null, DateTime? to = null, decimal minQty = 1m, Guid? list = null)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<CreatePriceCommandHandler>().HandleAsync(new CreatePriceCommand(sku, amount, from ?? Jan1, to, minQty, list)));

    private static Task<Pricing.Contracts.Models.PriceResolutionResult> Resolve(Env e, Guid product, decimal qty = 1m, DateTime? at = null, Guid? list = null)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<IPriceResolver>().ResolveAsync(product, qty, at ?? Jan1.AddDays(1), list));

    // ------------------------------------------------------------------ price lists

    [Fact]
    public async Task FirstPriceList_BecomesTheDefault_Automatically()
    {
        var e = await NewEnv();
        await using var _ = e.Db;

        await List(e, "A");
        await List(e, "B");

        var lists = await e.Db.InScopeAsync(sp => sp.GetRequiredService<ListPriceListsQueryHandler>().HandleAsync(new ListPriceListsQuery()));
        Assert.Equal(["A"], lists.Where(l => l.IsDefault).Select(l => l.Code).ToArray());
    }

    [Fact]
    public async Task CreatePriceList_MakeDefault_MovesTheDefault_AndRejectsDuplicateCodes()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        await List(e, "A");

        var b = await List(e, "B", makeDefault: true);
        var dup = await List(e, "b");

        var lists = await e.Db.InScopeAsync(sp => sp.GetRequiredService<ListPriceListsQueryHandler>().HandleAsync(new ListPriceListsQuery()));
        Assert.Equal(["B"], lists.Where(l => l.IsDefault).Select(l => l.Code).ToArray());
        Assert.Equal("Pricing.CreatePriceList.DuplicateCode", dup.Error.Code);
        Assert.True(b.IsSuccess);
        Assert.Equal("Pricing.PriceList.CodeRequired", (await List(e, " ")).Error.Code);
    }

    [Fact]
    public async Task SetDefault_Deactivate_Rules()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var a = (await List(e, "A")).Value;
        var b = (await List(e, "B")).Value;

        Task<Platform.Core.Results.Result> Deactivate(Guid id) => e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivatePriceListCommandHandler>().HandleAsync(new DeactivatePriceListCommand(id)));

        Assert.Equal("Pricing.PriceList.DefaultCannotBeDeactivated", (await Deactivate(a)).Error.Code);
        Assert.True((await e.Db.InScopeAsync(sp => sp.GetRequiredService<SetDefaultPriceListCommandHandler>().HandleAsync(new SetDefaultPriceListCommand(b)))).IsSuccess);
        Assert.True((await Deactivate(a)).IsSuccess);
        Assert.Equal("Pricing.SetDefault.PriceListNotFound", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<SetDefaultPriceListCommandHandler>().HandleAsync(new SetDefaultPriceListCommand(Guid.NewGuid())))).Error.Code);
        Assert.Equal("Pricing.PriceList.Inactive", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<SetDefaultPriceListCommandHandler>().HandleAsync(new SetDefaultPriceListCommand(a)))).Error.Code);
        Assert.Equal("Pricing.DeactivatePriceList.PriceListNotFound", (await Deactivate(Guid.NewGuid())).Error.Code);
    }

    // ------------------------------------------------------------------ prices

    [Fact]
    public async Task CreatePrice_ForAKnownProduct_UsesTheDefaultList_AndPersists()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");
        await List(e);

        var id = await Price(e, "SKU-1", 19.99m);

        Assert.True(id.IsSuccess, id.IsFailure ? id.Error.ToString() : null);
        var dto = await e.Db.InScopeAsync(sp => sp.GetRequiredService<GetPriceQueryHandler>().HandleAsync(new GetPriceQuery(id.Value)));
        Assert.Equal(19.99m, dto!.Amount);
        Assert.Equal(product, dto.ProductId);
        Assert.Equal(1m, dto.MinimumQuantity);
    }

    [Fact]
    public async Task CreatePrice_Failures()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("SKU-1");

        Assert.Equal("Pricing.CreatePrice.PriceListNotFound", (await Price(e, "SKU-1", 1m)).Error.Code);   // no list yet
        var inactive = (await List(e, "A")).Value;
        var other = (await List(e, "B")).Value;
        await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivatePriceListCommandHandler>().HandleAsync(new DeactivatePriceListCommand(other)));

        Assert.Equal("Pricing.CreatePrice.ProductNotFound", (await Price(e, "GHOST", 1m)).Error.Code);
        Assert.Equal("Pricing.CreatePrice.ProductCodeRequired", (await Price(e, " ", 1m)).Error.Code);
        Assert.Equal("Pricing.CreatePrice.PriceListInactive", (await Price(e, "SKU-1", 1m, list: other)).Error.Code);
        Assert.Equal("Pricing.CreatePrice.PriceListNotFound", (await Price(e, "SKU-1", 1m, list: Guid.NewGuid())).Error.Code);
        Assert.Equal("Pricing.Price.NegativeAmount", (await Price(e, "SKU-1", -1m)).Error.Code);
        Assert.Equal("Pricing.Price.InvalidPeriod", (await Price(e, "SKU-1", 1m, Jan1, Jan1)).Error.Code);
        Assert.NotEqual(Guid.Empty, inactive);
    }

    [Fact]
    public async Task OverlappingActivePrices_AreRejected_ButDifferentBreaksAndPeriodsAreFine()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("SKU-1");
        await List(e);

        Assert.True((await Price(e, "SKU-1", 10m, Jan1, Jan1.AddDays(30))).IsSuccess);
        Assert.Equal("Pricing.Price.Overlap", (await Price(e, "SKU-1", 9m, Jan1.AddDays(10), Jan1.AddDays(40))).Error.Code);
        Assert.True((await Price(e, "SKU-1", 9m, Jan1.AddDays(30), Jan1.AddDays(60))).IsSuccess);     // next period
        Assert.True((await Price(e, "SKU-1", 8m, Jan1, null, minQty: 10m)).IsSuccess);                 // quantity break
    }

    [Fact]
    public async Task DeactivatedPrice_NoLongerBlocksOverlap_AndIsNeverResolved()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");
        await List(e);
        var first = (await Price(e, "SKU-1", 10m)).Value;

        Assert.True((await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivatePriceCommandHandler>().HandleAsync(new DeactivatePriceCommand(first)))).IsSuccess);
        Assert.False((await Resolve(e, product)).Found);
        Assert.True((await Price(e, "SKU-1", 12m)).IsSuccess);
        Assert.Equal(12m, (await Resolve(e, product)).Amount);
        Assert.Equal("Pricing.Price.AlreadyInactive", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivatePriceCommandHandler>().HandleAsync(new DeactivatePriceCommand(first)))).Error.Code);
        Assert.Equal("Pricing.DeactivatePrice.PriceNotFound", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivatePriceCommandHandler>().HandleAsync(new DeactivatePriceCommand(Guid.NewGuid())))).Error.Code);
    }

    [Fact]
    public async Task UpdatePrice_ChangesTheAmount_AndTheNewValueIsResolved()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");
        await List(e);
        var id = (await Price(e, "SKU-1", 10m)).Value;

        var ok = await e.Db.InScopeAsync(sp => sp.GetRequiredService<UpdatePriceCommandHandler>().HandleAsync(new UpdatePriceCommand(id, 11.5m, Jan1)));

        Assert.True(ok.IsSuccess);
        Assert.Equal(11.5m, (await Resolve(e, product)).Amount);
        Assert.Equal("Pricing.UpdatePrice.PriceNotFound", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<UpdatePriceCommandHandler>().HandleAsync(new UpdatePriceCommand(Guid.NewGuid(), 1m, Jan1)))).Error.Code);
        Assert.Equal("Pricing.Price.NegativeAmount", (await e.Db.InScopeAsync(sp => sp.GetRequiredService<UpdatePriceCommandHandler>().HandleAsync(new UpdatePriceCommand(id, -5m, Jan1)))).Error.Code);
        Assert.Equal(11.5m, (await Resolve(e, product)).Amount);   // the failed update changed nothing
    }

    [Fact]
    public async Task UpdatePrice_CreatingAnOverlap_IsRejected_AndNotSaved()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");
        await List(e);
        await Price(e, "SKU-1", 10m, Jan1, Jan1.AddDays(30));
        var second = (await Price(e, "SKU-1", 9m, Jan1.AddDays(30), Jan1.AddDays(60))).Value;

        var r = await e.Db.InScopeAsync(sp => sp.GetRequiredService<UpdatePriceCommandHandler>().HandleAsync(new UpdatePriceCommand(second, 9m, Jan1.AddDays(10), Jan1.AddDays(60))));

        Assert.Equal("Pricing.Price.Overlap", r.Error.Code);
        var stored = await e.Db.InScopeAsync(sp => sp.GetRequiredService<GetPriceQueryHandler>().HandleAsync(new GetPriceQuery(second)));
        Assert.Equal(Jan1.AddDays(30), stored!.EffectiveFrom);
        Assert.Equal(10m, (await Resolve(e, product, at: Jan1.AddDays(20))).Amount);
    }

    // ------------------------------------------------------------------ resolution

    [Fact]
    public async Task Resolve_ReturnsTheCurrentPrice_WithListInformation()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");
        var list = (await List(e, "RETAIL")).Value;
        var price = (await Price(e, "SKU-1", 25m)).Value;

        var r = await Resolve(e, product);

        Assert.True(r.Found);
        Assert.Equal(25m, r.Amount);
        Assert.Equal(price, r.PriceId);
        Assert.Equal(list, r.PriceListId);
        Assert.Equal("RETAIL", r.PriceListCode);
    }

    [Fact]
    public async Task Resolve_AppliesQuantityBreaks_AndScheduledPrices()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");
        await List(e);
        await Price(e, "SKU-1", 10m, Jan1, Jan1.AddDays(30));
        await Price(e, "SKU-1", 12m, Jan1.AddDays(30));                // scheduled increase
        await Price(e, "SKU-1", 8m, Jan1, null, minQty: 10m);          // bulk

        Assert.Equal(10m, (await Resolve(e, product, 1m, Jan1.AddDays(5))).Amount);
        Assert.Equal(8m, (await Resolve(e, product, 10m, Jan1.AddDays(5))).Amount);
        Assert.Equal(12m, (await Resolve(e, product, 1m, Jan1.AddDays(31))).Amount);
        Assert.Equal(8m, (await Resolve(e, product, 25m, Jan1.AddDays(31))).Amount);
        Assert.False((await Resolve(e, product, 1m, Jan1.AddDays(-1))).Found);   // before anything is effective
    }

    [Fact]
    public async Task Resolve_UsesTheRequestedListInsteadOfTheDefault()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");
        await List(e, "RETAIL");
        var wholesale = (await List(e, "WHOLESALE")).Value;
        await Price(e, "SKU-1", 10m);
        await Price(e, "SKU-1", 7m, list: wholesale);

        Assert.Equal(10m, (await Resolve(e, product)).Amount);
        Assert.Equal(7m, (await Resolve(e, product, list: wholesale)).Amount);
    }

    [Fact]
    public async Task Resolve_NothingFound_ForUnpricedProductsInvalidInputOrNoDefaultList()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("SKU-1");

        Assert.Equal(Pricing.Contracts.Models.PriceResolutionResult.None, await Resolve(e, product));    // no lists at all
        await List(e);
        Assert.False((await Resolve(e, product)).Found);                                                  // list but no price
        Assert.False((await Resolve(e, Guid.Empty)).Found);
        Assert.False((await Resolve(e, product, 0m)).Found);
        Assert.False((await Resolve(e, product, list: Guid.NewGuid())).Found);
    }

    [Fact]
    public async Task Resolve_DoesNotLeakAcrossProducts()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var a = e.Catalog.Register("A");
        var b = e.Catalog.Register("B");
        await List(e);
        await Price(e, "A", 5m);

        Assert.Equal(5m, (await Resolve(e, a)).Amount);
        Assert.False((await Resolve(e, b)).Found);
    }

    [Fact]
    public async Task ListPricesForProduct_ReturnsAllStatuses_Ordered()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        var product = e.Catalog.Register("A");
        await List(e);
        await Price(e, "A", 10m);
        var bulk = (await Price(e, "A", 8m, minQty: 10m)).Value;
        await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivatePriceCommandHandler>().HandleAsync(new DeactivatePriceCommand(bulk)));

        var prices = await e.Db.InScopeAsync(sp => sp.GetRequiredService<ListPricesForProductQueryHandler>().HandleAsync(new ListPricesForProductQuery(product)));

        Assert.Equal([1m, 10m], prices.Select(p => p.MinimumQuantity).ToArray());
        Assert.Equal(2, prices.Count);
    }

    [Fact]
    public async Task Contract_DoesNotExposeDomainTypes_AndData_IsPersistedAcrossScopes()
    {
        var e = await NewEnv();
        await using var _ = e.Db;
        e.Catalog.Register("A");
        await List(e);
        await Price(e, "A", 1m);

        var count = await e.Db.InScopeAsync(sp => sp.GetRequiredService<PricingDbContext>().Prices.CountAsync());

        Assert.Equal(1, count);
        Assert.DoesNotContain(typeof(IPriceResolver).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Pricing.Domain", StringComparison.Ordinal));
    }
}
