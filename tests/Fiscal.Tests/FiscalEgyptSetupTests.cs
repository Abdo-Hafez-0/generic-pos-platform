using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Application.Queries;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Egypt;
using Fiscal.Infrastructure.DependencyInjection;
using Fiscal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Tests.Common;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11c: what Egypt's receipt needs from the setup - the branch address in the ETA's fields, the till's serial number, the buyer rule the
/// till reads, and a return built with its sale's document.
/// </summary>
public sealed class FiscalEgyptSetupTests
{
    private static readonly FiscalAddress Cairo = new("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12", "11371", "2", "5", "أمام البنك", "بجوار المسجد");

    private static Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync(IFiscalCountryPart? part = null)
        => TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            if (part is not null) s.AddSingleton(part);
        });

    private static Task<Platform.Core.Results.Result> SaveAsync(TestModuleDatabase<FiscalDbContext> db, FiscalCountry country, FiscalAddress? address)
        => db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(country, country == FiscalCountry.Egypt ? "123456789" : "300000000000003", "0", "4711", "متجر", address)));

    [Fact]
    public async Task Egypt_needs_the_governorate_from_its_list_and_the_city_street_and_building_number()
    {
        await using var db = await DatabaseAsync();

        Assert.Equal("Fiscal.Settings.GovernorateRequired", (await SaveAsync(db, FiscalCountry.Egypt, Cairo with { Governorate = null })).Error.Code);
        Assert.Equal("Fiscal.Settings.GovernorateRequired", (await SaveAsync(db, FiscalCountry.Egypt, Cairo with { Governorate = "Cairo" })).Error.Code);
        Assert.Equal("Fiscal.Settings.AddressRequired", (await SaveAsync(db, FiscalCountry.Egypt, Cairo with { BuildingNumber = " " })).Error.Code);
        Assert.Equal("Fiscal.Settings.GovernorateRequired", (await SaveAsync(db, FiscalCountry.Egypt, null)).Error.Code);
        Assert.True((await SaveAsync(db, FiscalCountry.SaudiArabia, null)).IsSuccess);   // Saudi Arabia's address comes with 11d

        Assert.True((await SaveAsync(db, FiscalCountry.Egypt, Cairo with { City = "  مدينة نصر  ", Room = "" })).IsSuccess);
        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;
        Assert.Equal(Cairo with { Room = null }, setup.Address);
        Assert.Equal(27, FiscalAddress.EgyptGovernorates.Count);
    }

    [Fact]
    public async Task The_till_gets_its_serial_number_once_and_keeps_it()
    {
        await using var db = await DatabaseAsync();
        var first = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;
        var again = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;

        Assert.Matches("^MTJ-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}$", first.DeviceSerial);
        Assert.Equal(first.DeviceSerial, again.DeviceSerial);
        var till = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        Assert.StartsWith("MTJ-" + till.Id.ToString("N")[..4].ToUpperInvariant(), till.DeviceSerial);
    }

    [Fact]
    public async Task A_till_made_before_its_serial_is_given_one_at_its_next_use()
    {
        await using var db = await DatabaseAsync();
        await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()));
        await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Database.ExecuteSqlRawAsync("UPDATE fis_Tills SET DeviceSerial = NULL"));

        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;
        Assert.Matches("^MTJ-", setup.DeviceSerial);
        Assert.Equal(setup.DeviceSerial, await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().Select(t => t.DeviceSerial).SingleAsync()));
    }

    [Fact]
    public async Task The_buyer_rule_comes_from_the_chosen_countrys_part()
    {
        await using var db = await DatabaseAsync(new EgyptCountryPart(new EgyptReceiptOptions { BuyerIdThreshold = 1000m }));
        Task<FiscalBuyerRule?> RuleAsync() => db.InScopeAsync(sp => sp.GetRequiredService<IFiscalSetupReader>().GetBuyerRuleAsync());

        Assert.Null(await RuleAsync());   // receipts off
        Assert.True((await SaveAsync(db, FiscalCountry.Egypt, Cairo)).IsSuccess);
        var rule = await RuleAsync();
        Assert.Equal(new FiscalBuyerRule(1000m, 14), rule);
        Assert.True(rule!.Applies(1000m));
        Assert.False(rule.Applies(999.99m));
        Assert.True(rule.IsSatisfiedBy("أحمد", " 29901011234567 "));
        Assert.False(rule.IsSatisfiedBy("أحمد", "2990101123456X"));
        Assert.False(rule.IsSatisfiedBy(" ", "29901011234567"));
    }

    [Fact]
    public async Task A_return_is_built_with_its_sales_document_and_the_tills_serial()
    {
        var part = new TestCountryPart();
        await using var db = await DatabaseAsync(part);
        Assert.True((await SaveAsync(db, FiscalCountry.Egypt, Cairo)).IsSuccess);
        var saleId = Guid.NewGuid();
        var sale = new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, saleId, "S-0000AAAA", DateTime.UtcNow,
            [new FiscalLineRequest(Guid.NewGuid(), "COLA", "Cola", 1m, 10m, 0m, 10m, 0.14m, 1.23m)], 10m, 1.23m, [new FiscalPaymentRequest("Cash", 10m)]);

        await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale));
        await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale with
        {
            Kind = FiscalDocumentKindContract.Return, SourceId = Guid.NewGuid(), Reference = "R-0000BBBB", OriginalSourceId = saleId, OriginalReference = "S-0000AAAA",
        }));
        await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale with
        {
            Kind = FiscalDocumentKindContract.Return, SourceId = Guid.NewGuid(), Reference = "R-0000CCCC", OriginalSourceId = Guid.NewGuid(), OriginalReference = "S-OLD",
        }));

        var (saleInput, returnInput, orphanInput) = (part.Built[0], part.Built[1], part.Built[2]);
        Assert.Null(saleInput.Original);
        Assert.NotNull(returnInput.Original);
        Assert.Equal((returnInput.PreviousHash, saleInput.Request.Reference), (returnInput.Original!.Hash, "S-0000AAAA"));
        Assert.StartsWith("Sale|S-0000AAAA|", returnInput.Original.Payload);
        Assert.Null(orphanInput.Original);   // a sale from before receipts were on: the part decides (Egypt refuses)
        Assert.All(part.Built, b => Assert.Matches("^MTJ-", b.DeviceSerial));
    }

    [Fact]
    public async Task The_receipt_mark_comes_with_the_document_and_is_read_back_for_a_copy()
    {
        // MISS-11c-2: the QR code's text, a title and the short hash under it; none for a document that could not be built
        var part = new TestCountryPart();
        await using var db = await DatabaseAsync(part);
        Assert.True((await SaveAsync(db, FiscalCountry.Egypt, Cairo)).IsSuccess);
        FiscalDocumentRequest Sale() => new(FiscalDocumentKindContract.Sale, Guid.NewGuid(), "S-0000AAAA", DateTime.UtcNow,
            [new FiscalLineRequest(Guid.NewGuid(), "COLA", "Cola", 1m, 10m, 0m, 10m, 0.14m, 1.23m)], 10m, 1.23m, [new FiscalPaymentRequest("Cash", 10m)]);

        var sale = Sale();
        var issued = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale));
        var hash = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().Select(d => d.Hash).SingleAsync());
        Assert.Equal(new FiscalReceiptMark("QR:" + hash, "Electronic receipt", hash![..8] + "..." + hash[^8..]), issued.Mark);
        Assert.Equal(issued.Mark, await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalReceiptMarks>().GetMarkAsync(FiscalDocumentKindContract.Sale, sale.SourceId)));
        Assert.Equal(issued.Mark, (await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(sale))).Mark);   // asked again

        part.Refuse = "no item code";
        var refused = Sale();
        Assert.Null((await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(refused))).Mark);
        Assert.Null(await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalReceiptMarks>().GetMarkAsync(FiscalDocumentKindContract.Sale, refused.SourceId)));
        Assert.Null(await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalReceiptMarks>().GetMarkAsync(FiscalDocumentKindContract.Sale, Guid.NewGuid())));
    }
}
