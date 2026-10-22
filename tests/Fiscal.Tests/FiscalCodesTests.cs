using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Application.DTOs;
using Fiscal.Application.Queries;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.DependencyInjection;
using Fiscal.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Pricing.Contracts.Interfaces;
using Pricing.Contracts.Models;
using Tests.Common;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11b: the tax authority's codes for products, units and tax rates (owner's decisions: kept on the e-receipts screen; a valid GS1
/// barcode is the product's code without typing; EGS codes suggested from the SKU and exported for the ETA portal).
/// </summary>
public sealed class FiscalCodesTests
{
    private static readonly Guid Piece = Guid.NewGuid(), Kilo = Guid.NewGuid(), Unused = Guid.NewGuid();
    private static readonly Guid Vat14 = Guid.NewGuid(), Zero = Guid.NewGuid();
    private static readonly CatalogProductListItem Cola = new(Guid.NewGuid(), "COLA-330", "Cola 330ml", Piece, "Piece", "pc", ["5449000000996"], true);
    private static readonly CatalogProductListItem Chips = new(Guid.NewGuid(), "CHIPS 150/g", "Potato chips", Piece, "Piece", "pc", ["12345"], true);
    private static readonly CatalogProductListItem Dates = new(Guid.NewGuid(), "DATES-500", "Dates", Kilo, "Kilogram", "kg", [], true);

    private sealed class StubCatalog : ICatalogListing
    {
        public Task<IReadOnlyList<CatalogProductListItem>> ListProductsAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CatalogProductListItem>>([Cola, Chips, Dates]);

        public Task<IReadOnlyList<CatalogUnitListItem>> ListUnitsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CatalogUnitListItem>>([new(Kilo, "Kilogram", "kg", true), new(Piece, "Piece", "pc", true), new(Unused, "Box", "bx", true)]);
    }

    private sealed class StubTax : ITaxRateListing, ITaxRateResolver
    {
        public Task<IReadOnlyList<TaxRateListItem>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TaxRateListItem>>([new(Vat14, "VAT14", "VAT 14%", 0.14m, true, true), new(Zero, "ZERO", "Zero rated", 0m, false, true)]);

        public Task<TaxResolutionResult> ResolveAsync(Guid productId, CancellationToken cancellationToken = default)
            => Task.FromResult(productId == Dates.ProductId ? new TaxResolutionResult(true, 0m, Zero, "ZERO", "Zero rated") : new TaxResolutionResult(true, 0.14m, Vat14, "VAT14", "VAT 14%"));
    }

    private readonly TestCountryPart _part = new();

    private Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync()
        => TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            s.AddSingleton<ICatalogListing, StubCatalog>();
            var tax = new StubTax();
            s.AddSingleton<ITaxRateListing>(tax);
            s.AddSingleton<ITaxRateResolver>(tax);
            s.AddSingleton<IFiscalCountryPart>(_part);
        });

    private static async Task SwitchOnAsync(TestModuleDatabase<FiscalDbContext> db, FiscalCountry country = FiscalCountry.Egypt)
        => Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(country == FiscalCountry.Egypt
            ? new SaveFiscalSettingsCommand(country, "123456789", "0", "4711", "متجر", new Fiscal.Domain.Entities.FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))
            : new SaveFiscalSettingsCommand(country, "300000000000003", null, null, "متجر", new Fiscal.Domain.Entities.FiscalAddress(null, "الرياض", "طريق الملك فهد", "2322", "12214", District: "العليا"), "CRN", "1010010000")))).IsSuccess);

    private static Task<FiscalCodesDto> CodesAsync(TestModuleDatabase<FiscalDbContext> db)
        => db.InScopeAsync(async sp => (await sp.GetRequiredService<GetFiscalCodesQueryHandler>().HandleAsync(new GetFiscalCodesQuery())).Value);

    private static Task<Platform.Core.Results.Result> SaveAsync(TestModuleDatabase<FiscalDbContext> db, SaveFiscalCodesCommand command)
        => db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalCodesCommandHandler>().HandleAsync(command));

    [Theory]
    [InlineData("5449000000996", true)]     // EAN-13 (Coca-Cola)
    [InlineData("5449000000997", false)]    // wrong check digit
    [InlineData("96385074", true)]          // GTIN-8
    [InlineData("036000291452", true)]      // UPC-A
    [InlineData("10012345678902", true)]    // GTIN-14
    [InlineData("12345", false)]
    [InlineData("ABCDEFGHIJKLM", false)]
    public void A_GS1_code_has_the_right_length_and_check_digit(string code, bool valid) => Assert.Equal(valid, Gs1.IsValid(code));

    [Fact]
    public void EGS_codes_are_EG_the_tax_number_and_the_products_code_and_are_suggested_from_the_SKU()
    {
        Assert.True(Egs.IsValid("EG-123456789-COLA-330"));
        Assert.False(Egs.IsValid("EG-12345-COLA"));
        Assert.False(Egs.IsValid("COLA-330"));
        Assert.Equal("EG-123456789-CHIPS-150-G", Egs.Suggest("123456789", "chips 150/g"));
        Assert.True(Egs.IsValid(Egs.Suggest("123456789", "chips 150/g")));
        Assert.Equal("EG-123456789-ITEM", Egs.Suggest("123456789", " / "));
    }

    [Fact]
    public async Task For_Egypt_a_GS1_barcode_is_the_code_and_the_rest_is_listed_as_missing()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnAsync(db);

        var codes = await CodesAsync(db);

        var cola = codes.Products.Single(p => p.ProductId == Cola.ProductId);
        Assert.Equal((FiscalCodeSource.Barcode, FiscalItemCodeType.GS1, "5449000000996"), (cola.Source, cola.Type, cola.Code));
        Assert.Equal(FiscalCodeSource.Missing, codes.Products.Single(p => p.ProductId == Chips.ProductId).Source);   // "12345" is no GS1 code
        Assert.Equal(2, codes.MissingProducts);
        Assert.Equal(2, codes.MissingUnits);                                            // the box is not used by any product
        Assert.Equal(2, codes.MissingTaxRates);
        Assert.Equal(6, codes.MissingTotal);
    }

    [Fact]
    public async Task Saved_codes_complete_the_list_and_a_code_in_the_wrong_form_saves_nothing()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnAsync(db);

        var refused = await SaveAsync(db, new SaveFiscalCodesCommand(
            Products: [new(Chips.ProductId, FiscalItemCodeType.EGS, "EG-123456789-CHIPS"), new(Dates.ProductId, FiscalItemCodeType.GS1, "123")]));
        Assert.Equal("Fiscal.Code.Gs1Invalid", refused.Error.Code);
        Assert.Equal(2, (await CodesAsync(db)).MissingProducts);                       // all or nothing

        var saved = await SaveAsync(db, new SaveFiscalCodesCommand(
            Products: [new(Chips.ProductId, FiscalItemCodeType.EGS, " EG-123456789-CHIPS "), new(Dates.ProductId, FiscalItemCodeType.EGS, Egs.Suggest("123456789", Dates.Sku))],
            Units: [new(Piece, "ea"), new(Kilo, "KGM")],
            TaxRates: [new(Vat14, "t1", "v009", null, null, null), new(Zero, "T1", "V003", null, null, null)]));

        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
        var codes = await CodesAsync(db);
        Assert.Equal(0, codes.MissingTotal);
        Assert.Equal(("EG-123456789-CHIPS", FiscalCodeSource.Saved), (codes.Products.Single(p => p.ProductId == Chips.ProductId).Code, codes.Products.Single(p => p.ProductId == Chips.ProductId).Source));
        Assert.Equal("EA", codes.Units.Single(u => u.UnitId == Piece).Code);
        Assert.Equal(("T1", "V009"), (codes.TaxRates.Single(t => t.TaxRateId == Vat14).EgyptTaxType, codes.TaxRates.Single(t => t.TaxRateId == Vat14).EgyptSubType));

        await SaveAsync(db, new SaveFiscalCodesCommand(Products: [new(Chips.ProductId, FiscalItemCodeType.EGS, "")]));   // removed again
        Assert.Equal(1, (await CodesAsync(db)).MissingProducts);
    }

    [Fact]
    public async Task Saudi_Arabia_needs_only_the_tax_rates_category_and_a_reason_unless_standard()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnAsync(db, FiscalCountry.SaudiArabia);

        var before = await CodesAsync(db);
        Assert.Equal((0, 0, 2), (before.MissingProducts, before.MissingUnits, before.MissingTaxRates));

        Assert.Equal("Fiscal.Code.CategoryInvalid", (await SaveAsync(db, new SaveFiscalCodesCommand(TaxRates: [new(Vat14, null, null, "X", null, null)]))).Error.Code);
        await SaveAsync(db, new SaveFiscalCodesCommand(TaxRates: [new(Vat14, null, null, "S", "IGNORED", "ignored"), new(Zero, null, null, "Z", null, null)]));
        var after = await CodesAsync(db);
        Assert.Equal(1, after.MissingTaxRates);                                          // zero rated needs its reason
        Assert.Null(after.TaxRates.Single(t => t.TaxRateId == Vat14).SaudiExemptionCode); // standard keeps no reason

        await SaveAsync(db, new SaveFiscalCodesCommand(TaxRates: [new(Zero, null, null, "Z", "VATEX-SA-34-4", "Supply of a qualifying medicine")]));
        Assert.Equal(0, (await CodesAsync(db)).MissingTaxRates);
    }

    [Fact]
    public async Task The_country_part_receives_each_lines_codes_when_it_builds_the_document()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnAsync(db);
        await SaveAsync(db, new SaveFiscalCodesCommand(Units: [new(Piece, "EA")], TaxRates: [new(Vat14, "T1", "V009", null, null, null)]));
        var saleId = Guid.NewGuid();

        await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, saleId, "S-1",
            DateTime.UtcNow, [new FiscalLineRequest(Cola.ProductId, Cola.Sku, Cola.Name, 2m, 2.5m, 0m, 5m, 0.14m, 0.61m),
                              new FiscalLineRequest(Chips.ProductId, Chips.Sku, Chips.Name, 1m, 3m, 0m, 3m, 0.14m, 0.37m)], 8m, 0.98m, [])));

        var codes = Assert.Single(_part.Built).LineCodes!;
        Assert.Equal(new FiscalLineCodes(FiscalItemCodeType.GS1, "5449000000996", "EA", "T1", "V009", null, null, null), codes[Cola.ProductId]);
        Assert.Equal(new FiscalLineCodes(null, null, "EA", "T1", "V009", null, null, null), codes[Chips.ProductId]);   // no code yet: the part refuses (11c)
    }

    [Fact]
    public async Task The_EGS_codes_are_exported_for_the_ETA_portal()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnAsync(db);
        await SaveAsync(db, new SaveFiscalCodesCommand(Products: [new(Chips.ProductId, FiscalItemCodeType.EGS, "EG-123456789-CHIPS")]));

        var csv = (await db.InScopeAsync(sp => sp.GetRequiredService<ExportEgsCodesQueryHandler>().HandleAsync(new ExportEgsCodesQuery()))).Value;

        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("Code Type,Parent Code,Item Code", lines[0]);
        var row = Assert.Single(lines.Skip(1));
        Assert.StartsWith("EGS,,EG-123456789-CHIPS,Potato chips,", row);
        Assert.DoesNotContain("5449000000996", csv);                                   // GS1 products need no registration
    }
}
