using System.Xml.Linq;
using Catalog.Contracts.Interfaces;
using Catalog.Contracts.Models;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Application.Queries;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.DependencyInjection;
using Fiscal.Infrastructure.Persistence;
using Fiscal.Saudi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pricing.Contracts.Interfaces;
using Pricing.Contracts.Models;
using Tests.Common;
using Tests.Common.Security;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11d-1: what Saudi Arabia's invoice needs from the setup (owner's choice, drawn option A: the seller's identification with its type,
/// CRN first, and the national address), and the Saudi part in the module: the chain of invoices (ICV and PIH), a credit note for a
/// return, and no Late hold (ZATCA takes a late simplified invoice).
/// </summary>
public sealed class FiscalSaudiSetupTests
{
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero));
    private static readonly Guid Water = Guid.NewGuid();
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync(bool withPart = true)
        => TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            if (withPart) s.AddSingleton<IFiscalCountryPart>(new SaudiCountryPart());
            s.AddSingleton<TimeProvider>(_clock);
        });

    private static Task<Platform.Core.Results.Result> SaveAsync(TestModuleDatabase<FiscalDbContext> db, FiscalAddress? address, string? scheme = "CRN", string? id = "1010010000")
        => db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.SaudiArabia, "300000000000003", null, null, "متجر النور", address, scheme, id)));

    [Fact]
    public async Task A_return_in_Saudi_Arabia_asks_nothing_more_at_the_till()
    {
        // MISS-11e: every return already has its credit note (naming the sale's receipt number); no buyer, no limit of days
        await using var db = await DatabaseAsync();
        Task<FiscalReturnRule?> RuleAsync() => db.InScopeAsync(sp => sp.GetRequiredService<IFiscalSetupReader>().GetReturnRuleAsync(Guid.NewGuid()));

        Assert.Null(await RuleAsync());   // receipts off
        Assert.True((await SaveAsync(db, SaudiInvoiceTests.Riyadh)).IsSuccess);
        Assert.Null(await RuleAsync());
    }

    [Theory]
    [InlineData(null, "1010010000", null, "Fiscal.Settings.SellerIdTypeRequired")]
    [InlineData("XYZ", "1010010000", null, "Fiscal.Settings.SellerIdTypeRequired")]
    [InlineData("CRN", " ", null, "Fiscal.Settings.SellerIdInvalid")]
    [InlineData("CRN", "1010-0100", null, "Fiscal.Settings.SellerIdInvalid")]
    [InlineData("CRN", "1010010000", "district", "Fiscal.Settings.AddressRequired")]
    [InlineData("CRN", "1010010000", "building", "Fiscal.Settings.BuildingNumberInvalid")]
    [InlineData("CRN", "1010010000", "postal", "Fiscal.Settings.PostalCodeInvalid")]
    [InlineData("CRN", "1010010000", "additional", "Fiscal.Settings.AdditionalNumberInvalid")]
    public async Task Saudi_Arabia_needs_the_sellers_identification_and_the_national_address(string? scheme, string? id, string? wrong, string code)
    {
        await using var db = await DatabaseAsync();
        var address = wrong switch
        {
            "district" => SaudiInvoiceTests.Riyadh with { District = " " },
            "building" => SaudiInvoiceTests.Riyadh with { BuildingNumber = "232" },
            "postal" => SaudiInvoiceTests.Riyadh with { PostalCode = "1221A" },
            "additional" => SaudiInvoiceTests.Riyadh with { AdditionalNumber = "77" },
            _ => SaudiInvoiceTests.Riyadh,
        };

        Assert.Equal(code, (await SaveAsync(db, address, scheme, id)).Error.Code);
    }

    [Fact]
    public async Task The_Saudi_details_are_kept_and_read_back_and_Egypts_are_dropped()
    {
        await using var db = await DatabaseAsync();
        Assert.True((await SaveAsync(db, SaudiInvoiceTests.Riyadh with { Governorate = "القاهرة", Floor = "2", AdditionalNumber = "" }, " mom ", " 4030000000 ")).IsSuccess);

        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;
        Assert.Equal(("MOM", "4030000000"), (setup.SellerIdScheme, setup.SellerId));
        Assert.Equal(SaudiInvoiceTests.Riyadh with { Governorate = "القاهرة", Floor = "2", AdditionalNumber = null }, setup.Address);
        Assert.Equal(["CRN", "MOM", "MLS", "700", "SAG", "OTH"], FiscalSettings.SaudiSellerIdSchemes);
        Assert.Contains(FiscalCountry.SaudiArabia, setup.InstalledCountryParts);

        // switching to Egypt forgets the Saudi identification
        var settings = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Settings.SingleAsync());
        Assert.True(settings.Update(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", new FiscalAddress("القاهرة", "مدينة نصر", "شارع", "1"), false, null, DateTime.UtcNow,
            "CRN", "1010010000").IsSuccess);
        Assert.Equal(((string?)null, (string?)null), (settings.SellerIdScheme, settings.SellerId));
    }

    [Fact]
    public async Task Sales_and_returns_make_a_chain_of_simplified_invoices_and_credit_notes()
    {
        await using var db = await DatabaseWithCodesAsync(coded: false);   // the 15% rate has no VAT category yet: not built
        var sale = new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, Guid.NewGuid(), "S-0000AAAA", _clock.GetUtcNow().UtcDateTime,
            [new FiscalLineRequest(Water, "WATER", "Water", 2m, 1.15m, 0m, 2.30m, 0.15m, 0.30m)], 2.30m, 0.30m, [new FiscalPaymentRequest("Cash", 2.30m)]);

        var notBuilt = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale));
        Assert.False(notBuilt.IsIssued);
        Assert.Contains("VAT category", notBuilt.NotBuiltReason);

        // with the rate's category (MISS-11b's code book)
        await using var coded = await DatabaseWithCodesAsync();
        var first = await coded.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale));
        var refund = await coded.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale with
        {
            Kind = FiscalDocumentKindContract.Return, SourceId = Guid.NewGuid(), Reference = "R-0000BBBB", OriginalSourceId = sale.SourceId,
            OriginalReference = "S-0000AAAA", Reason = "Wrong size",
        }));

        Assert.True(first.IsIssued && refund.IsIssued);
        var documents = await coded.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().OrderBy(d => d.Number).ToListAsync());
        var (invoice, note) = (XDocument.Parse(documents[0].Payload!), XDocument.Parse(documents[1].Payload!));
        string Pih(XDocument d) => d.Root!.Elements(Cac + "AdditionalDocumentReference").Single(r => r.Element(Cbc + "ID")!.Value == "PIH").Descendants(Cbc + "EmbeddedDocumentBinaryObject").Single().Value;
        string Icv(XDocument d) => d.Root!.Elements(Cac + "AdditionalDocumentReference").Single(r => r.Element(Cbc + "ID")!.Value == "ICV").Element(Cbc + "UUID")!.Value;

        Assert.Equal((ZatcaInvoiceBuilder.FirstPreviousHash, "1"), (Pih(invoice), Icv(invoice)));
        Assert.Equal((documents[0].Hash, "2"), (Pih(note), Icv(note)));
        Assert.Equal(ZatcaXml.Hash(ZatcaXml.Read(documents[1].Payload!)), documents[1].Hash);
        Assert.Equal(("381", "S-0000AAAA", "Wrong size"), (note.Root!.Element(Cbc + "InvoiceTypeCode")!.Value,
            note.Root.Element(Cac + "BillingReference")!.Value, note.Root.Element(Cac + "PaymentMeans")!.Element(Cbc + "InstructionNote")!.Value));
        Assert.Equal("فاتورة ضريبية مبسطة", first.Mark!.TopLines![0]);
        Assert.Equal("إشعار دائن مبسط", refund.Mark!.TopLines![0]);   // MISS-11d-2: the credit note's own title
        Assert.Equal(documents[0].QrPayload, first.Mark.QrPayload);
    }

    [Fact]
    public async Task Saudi_invoices_wait_for_onboarding_and_are_never_held_as_late()
    {
        await using var db = await DatabaseWithCodesAsync();
        var sale = new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, Guid.NewGuid(), "S-0000AAAA", _clock.GetUtcNow().UtcDateTime,
            [new FiscalLineRequest(Water, "WATER", "Water", 1m, 1.15m, 0m, 1.15m, 0.15m, 0.15m)], 1.15m, 0.15m, [new FiscalPaymentRequest("Cash", 1.15m)]);
        var id = (await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale))).DocumentId!.Value;

        _clock.Advance(TimeSpan.FromHours(30));
        var round = await db.InScopeAsync(sp => sp.GetRequiredService<SendFiscalDocumentsCommandHandler>().ExecuteAsync(force: true));

        Assert.Equal((0, 1), (round.Late, round.NotSent));
        var document = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync(d => d.Id == id));
        Assert.Equal((FiscalDocumentState.Waiting, SaudiCountryPart.NotOnboarded), (document.State, document.StateReason));
    }

    private static readonly Guid Vat15 = Guid.NewGuid();

    private sealed class StubCatalog : ICatalogListing
    {
        public Task<IReadOnlyList<CatalogProductListItem>> ListProductsAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CatalogProductListItem>>([new(Water, "WATER", "Water", Guid.Empty, "Piece", "pc", [], true)]);

        public Task<IReadOnlyList<CatalogUnitListItem>> ListUnitsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<CatalogUnitListItem>>([]);
    }

    private sealed class StubTax : ITaxRateListing, ITaxRateResolver
    {
        public Task<IReadOnlyList<TaxRateListItem>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TaxRateListItem>>([new(Vat15, "VAT15", "VAT 15%", 0.15m, true, true)]);

        public Task<TaxResolutionResult> ResolveAsync(Guid productId, CancellationToken cancellationToken = default)
            => Task.FromResult(new TaxResolutionResult(true, 0.15m, Vat15, "VAT15", "VAT 15%"));
    }

    /// <summary>A database with Saudi Arabia on and the 15% tax rate coded S (the products' rates come through the code book).</summary>
    private async Task<TestModuleDatabase<FiscalDbContext>> DatabaseWithCodesAsync(bool coded = true)
    {
        var db = await TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            s.AddSingleton<IFiscalCountryPart>(new SaudiCountryPart());
            s.AddSingleton<TimeProvider>(_clock);
            s.AddSingleton<ICatalogListing, StubCatalog>();
            var tax = new StubTax();
            s.AddSingleton<ITaxRateListing>(tax);
            s.AddSingleton<ITaxRateResolver>(tax);
        });
        Assert.True((await SaveAsync(db, SaudiInvoiceTests.Riyadh)).IsSuccess);
        if (!coded) return db;
        var saved = await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalCodesCommandHandler>().HandleAsync(new SaveFiscalCodesCommand(
            TaxRates: [new FiscalTaxCodeChange(Vat15, null, null, "S", null, null)])));
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
        return db;
    }
}
