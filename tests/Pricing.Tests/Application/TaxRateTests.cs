using Catalog.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Results;
using Pricing.Application.Commands;
using Pricing.Application.DTOs;
using Pricing.Application.Queries;
using Pricing.Contracts.Interfaces;
using Pricing.Contracts.Models;
using Pricing.Domain.Entities;
using Pricing.Domain.Enums;
using Pricing.Domain.Services;
using Pricing.Infrastructure.DependencyInjection;
using Pricing.Infrastructure.Persistence;
using Tests.Common;

namespace Pricing.Tests.Application;

/// <summary>
/// FIX-08a: tax rates in Pricing. Prices include tax; a product uses its own rate, else the default, else none; the till snapshots the
/// resolved rate (so changing a rate never rewrites history).
/// </summary>
public sealed class TaxRateTests
{
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

    private static Task<Result<Guid>> Create(Env e, string code, decimal rate, bool makeDefault = false)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<CreateTaxRateCommandHandler>().HandleAsync(new CreateTaxRateCommand(code, code + " rate", rate, makeDefault)));

    private static Task<Result> Assign(Env e, string sku, Guid? rate)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<SetProductTaxRateCommandHandler>().HandleAsync(new SetProductTaxRateCommand(sku, rate)));

    private static Task<TaxResolutionResult> Resolve(Env e, Guid product)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<ITaxRateResolver>().ResolveAsync(product));

    private static Task<IReadOnlyList<TaxRateDto>> List(Env e)
        => e.Db.InScopeAsync(sp => sp.GetRequiredService<ListTaxRatesQueryHandler>().HandleAsync(new ListTaxRatesQuery()));

    // ------------------------------------------------------------------ domain

    [Theory]
    [InlineData(-0.01, "Pricing.TaxRate.OutOfRange")]
    [InlineData(1.01, "Pricing.TaxRate.OutOfRange")]
    [InlineData(0.14255, "Pricing.TaxRate.TooPrecise")]
    public void A_rate_outside_0_to_100_percent_or_finer_than_a_hundredth_of_a_percent_is_refused(double rate, string code)
        => Assert.Equal(code, TaxRate.Create("STD", "Standard", (decimal)rate, true).Error.Code);

    [Fact]
    public void A_rate_of_0_or_100_percent_and_two_decimal_percentages_are_accepted()
    {
        Assert.True(TaxRate.Create("ZERO", "Zero", 0m, false).IsSuccess);
        Assert.True(TaxRate.Create("ALL", "All", 1m, false).IsSuccess);
        Assert.Equal(0.1425m, TaxRate.Create("std", "Standard", 0.1425m, false).Value.Rate);
        Assert.Equal("STD", TaxRate.Create(" std ", "Standard", 0.14m, false).Value.Code);
    }

    [Fact]
    public void The_default_rate_cannot_be_deactivated_and_an_inactive_rate_cannot_be_changed()
    {
        var rate = TaxRate.Create("STD", "Standard", 0.14m, true).Value;
        Assert.Equal("Pricing.TaxRate.DefaultCannotBeDeactivated", rate.Deactivate().Error.Code);

        rate.ClearDefault();
        Assert.True(rate.Deactivate().IsSuccess);
        Assert.Equal("Pricing.TaxRate.Inactive", rate.Update("Standard", 0.15m).Error.Code);
        Assert.Equal("Pricing.TaxRate.Inactive", rate.MakeDefault().Error.Code);
    }

    [Fact]
    public void Selection_takes_the_products_own_active_rate_else_the_active_default_else_none()
    {
        var standard = TaxRate.Create("STD", "Standard", 0.14m, true).Value;
        var reduced = TaxRate.Create("RED", "Reduced", 0.05m, false).Value;

        Assert.Same(reduced, TaxSelection.Select(reduced, standard));
        Assert.Same(standard, TaxSelection.Select(null, standard));
        reduced.Deactivate();
        Assert.Same(standard, TaxSelection.Select(reduced, standard));     // its rate was deactivated: the default applies
        Assert.Null(TaxSelection.Select(null, null));
    }

    // ------------------------------------------------------------------ handlers and the contract

    [Fact]
    public async Task Without_any_rate_a_product_has_no_tax()
    {
        var e = await NewEnv();
        var cola = e.Catalog.Register("COLA-1");

        var tax = await Resolve(e, cola);

        Assert.False(tax.Found);
        Assert.Equal(0m, tax.Rate);
    }

    [Fact]
    public async Task The_first_rate_becomes_the_default_and_every_product_uses_it()
    {
        var e = await NewEnv();
        var cola = e.Catalog.Register("COLA-1");

        var standard = await Create(e, "std", 0.14m);

        Assert.True(standard.IsSuccess);
        var tax = await Resolve(e, cola);
        Assert.Equal((true, 0.14m, standard.Value, "STD"), (tax.Found, tax.Rate, tax.TaxRateId, tax.Code));
        Assert.True(Assert.Single(await List(e)).IsDefault);
    }

    [Fact]
    public async Task A_product_can_have_its_own_rate_and_go_back_to_the_default()
    {
        var e = await NewEnv();
        var bread = e.Catalog.Register("BREAD-1");
        await Create(e, "STD", 0.14m);
        var zero = (await Create(e, "ZERO", 0m)).Value;

        Assert.True((await Assign(e, "BREAD-1", zero)).IsSuccess);
        Assert.Equal(("ZERO", 0m), ((await Resolve(e, bread)).Code, (await Resolve(e, bread)).Rate));

        Assert.True((await Assign(e, "BREAD-1", null)).IsSuccess);
        Assert.Equal("STD", (await Resolve(e, bread)).Code);
        Assert.True((await Assign(e, "BREAD-1", null)).IsSuccess);   // already on the default: nothing to do
    }

    [Fact]
    public async Task Choosing_another_default_moves_every_product_without_its_own_rate()
    {
        var e = await NewEnv();
        var cola = e.Catalog.Register("COLA-1");
        var standard = (await Create(e, "STD", 0.14m)).Value;
        var newLaw = (await Create(e, "STD2027", 0.15m, makeDefault: true)).Value;

        Assert.Equal(0.15m, (await Resolve(e, cola)).Rate);
        Assert.True((await e.Db.InScopeAsync(sp => sp.GetRequiredService<SetDefaultTaxRateCommandHandler>().HandleAsync(new SetDefaultTaxRateCommand(standard)))).IsSuccess);
        Assert.Equal(0.14m, (await Resolve(e, cola)).Rate);
        Assert.Single(await List(e), r => r.IsDefault);
        Assert.NotEqual(Guid.Empty, newLaw);
    }

    [Fact]
    public async Task Changing_a_rate_applies_to_the_next_resolution_and_deactivating_one_falls_back_to_the_default()
    {
        var e = await NewEnv();
        var bread = e.Catalog.Register("BREAD-1");
        var standard = (await Create(e, "STD", 0.14m)).Value;
        var reduced = (await Create(e, "RED", 0.05m)).Value;
        await Assign(e, "BREAD-1", reduced);

        Assert.True((await e.Db.InScopeAsync(sp => sp.GetRequiredService<UpdateTaxRateCommandHandler>().HandleAsync(new UpdateTaxRateCommand(reduced, "Reduced", 0.08m)))).IsSuccess);
        Assert.Equal(0.08m, (await Resolve(e, bread)).Rate);

        Assert.True((await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivateTaxRateCommandHandler>().HandleAsync(new DeactivateTaxRateCommand(reduced)))).IsSuccess);
        Assert.Equal((standard, 0.14m), ((await Resolve(e, bread)).TaxRateId, (await Resolve(e, bread)).Rate));

        var product = await e.Db.InScopeAsync(sp => sp.GetRequiredService<GetProductTaxQueryHandler>().HandleAsync(new GetProductTaxQuery(bread)));
        Assert.Equal(TaxRateStatus.Inactive, product.Assigned!.Status);   // the choice is kept and shown, but no longer applies
        Assert.Equal("STD", product.Effective!.Code);
    }

    [Fact]
    public async Task Refusals_are_plain_duplicate_code_unknown_product_inactive_rate_and_deactivating_the_default()
    {
        var e = await NewEnv();
        e.Catalog.Register("COLA-1");
        var standard = (await Create(e, "STD", 0.14m)).Value;
        var old = (await Create(e, "OLD", 0.10m)).Value;
        await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivateTaxRateCommandHandler>().HandleAsync(new DeactivateTaxRateCommand(old)));

        Assert.Equal("Pricing.TaxRate.DuplicateCode", (await Create(e, "std", 0.2m)).Error.Code);
        Assert.Equal("Pricing.ProductTaxRate.ProductNotFound", (await Assign(e, "NOPE", standard)).Error.Code);
        Assert.Equal("Pricing.TaxRate.Inactive", (await Assign(e, "COLA-1", old)).Error.Code);
        Assert.Equal("Pricing.TaxRate.DefaultCannotBeDeactivated",
            (await e.Db.InScopeAsync(sp => sp.GetRequiredService<DeactivateTaxRateCommandHandler>().HandleAsync(new DeactivateTaxRateCommand(standard)))).Error.Code);
    }
}
