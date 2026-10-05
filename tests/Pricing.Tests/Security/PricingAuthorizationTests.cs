using Catalog.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Pricing.Application.Commands;
using Pricing.Application.Queries;
using Pricing.Application.Security;
using Pricing.Infrastructure.DependencyInjection;
using Pricing.Infrastructure.Persistence;
using Pricing.Tests.Application;
using Tests.Common;
using Tests.Common.Security;

namespace Pricing.Tests.Security;

public sealed class PricingAuthorizationTests
{
    private static async Task<(TestModuleDatabase<PricingDbContext> Db, ScriptedAuthorizationService Auth)> StartAsync(params string[] allowed)
    {
        var auth = new ScriptedAuthorizationService(allowed);
        var db = await TestModuleDatabase<PricingDbContext>.CreateAsync(s =>
        {
            s.AddPricingCore();
            s.AddSingleton<IProductLookup>(new StubCatalog());
            s.AddSingleton<IAuthorizationService>(auth);
        });
        return (db, auth);
    }

    [Fact]
    public async Task Changing_prices_and_price_lists_needs_the_matching_capability()
    {
        var (db, auth) = await StartAsync();
        await using var _ = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var id = Guid.NewGuid();

        var results = new (string Name, Result Result, string Capability)[]
        {
            ("create list", await sp.GetRequiredService<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand("RETAIL", "Retail", true)), PricingCapabilities.ManagePriceLists),
            ("default list", await sp.GetRequiredService<SetDefaultPriceListCommandHandler>().HandleAsync(new SetDefaultPriceListCommand(id)), PricingCapabilities.ManagePriceLists),
            ("deactivate list", await sp.GetRequiredService<DeactivatePriceListCommandHandler>().HandleAsync(new DeactivatePriceListCommand(id)), PricingCapabilities.ManagePriceLists),
            ("create price", await sp.GetRequiredService<CreatePriceCommandHandler>().HandleAsync(new CreatePriceCommand("SKU-1", 1m, DateTime.UtcNow)), PricingCapabilities.ManagePrices),
            ("deactivate price", await sp.GetRequiredService<DeactivatePriceCommandHandler>().HandleAsync(new DeactivatePriceCommand(id)), PricingCapabilities.ManagePrices)
        };

        foreach (var (name, result, capability) in results)
        {
            Assert.True(result.IsFailure, name);
            Assert.Equal(SecurityErrors.ForbiddenCode, result.Error.Code);
            Assert.Contains(capability, auth.Asked);
        }

        Assert.Empty((await sp.GetRequiredService<ListPriceListsQueryHandler>().HandleAsync(new ListPriceListsQuery())));
    }

    [Fact]
    public async Task One_capability_does_not_grant_the_other()
    {
        var (db, _) = await StartAsync(PricingCapabilities.ManagePriceLists);
        await using var _2 = db;
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;

        var list = await sp.GetRequiredService<CreatePriceListCommandHandler>().HandleAsync(new CreatePriceListCommand("RETAIL", "Retail", true));
        var price = await sp.GetRequiredService<CreatePriceCommandHandler>().HandleAsync(new CreatePriceCommand("SKU-1", 1m, DateTime.UtcNow));

        Assert.True(list.IsSuccess, list.IsFailure ? list.Error.ToString() : null);
        Assert.Equal(SecurityErrors.ForbiddenCode, price.Error.Code);
    }

    [Fact]
    public void Every_capability_is_declared_once_owned_by_pricing_and_sensitive()
    {
        var catalog = new CapabilityCatalog([new PricingCapabilityProvider()]);

        Assert.Equal(PricingCapabilities.All.Count, catalog.All.Count);
        Assert.All(catalog.All, c => Assert.Equal("pricing", c.Module));
        Assert.All(catalog.All, c => Assert.True(c.IsSensitive));
    }
}
