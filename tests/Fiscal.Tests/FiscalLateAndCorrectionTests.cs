using System.Text.Json;
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
using Platform.Application.Abstractions.Auditing;
using Tests.Common;
using Tests.Common.Security;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11c-4 (owner's decisions): a document unsent 24 hours after its sale is held as Late and goes only with "Send late receipts" (a fresh
/// sign-in); a refused one is corrected - built again with the current codes at the same place of the chain, naming the refused one, which
/// is kept as Replaced; one that could not be built is built at the end of the chain ("Build and send").
/// </summary>
public sealed class FiscalLateAndCorrectionTests
{
    private readonly TestCountryPart _part = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero));
    private readonly List<BusinessEvent> _events = [];

    private sealed class Events(List<BusinessEvent> recorded) : IBusinessEventSink
    {
        public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
        {
            recorded.Add(businessEvent);
            return Task.CompletedTask;
        }
    }

    private Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync()
        => TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            s.AddSingleton<IFiscalCountryPart>(_part);
            s.AddSingleton<TimeProvider>(_clock);
            s.AddSingleton<IBusinessEventSink>(new Events(_events));
        });

    private static async Task SwitchOnEgyptAsync(TestModuleDatabase<FiscalDbContext> db)
        => Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", new FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))))).IsSuccess);

    private FiscalDocumentRequest Sale(string reference, DateTime? at = null)
        => new(FiscalDocumentKindContract.Sale, Guid.NewGuid(), reference, at ?? _clock.GetUtcNow().UtcDateTime,
            [new FiscalLineRequest(Guid.NewGuid(), "COLA", "Cola", 1m, 10m, 0m, 10m, 0.14m, 1.23m)], 10m, 1.23m, [new FiscalPaymentRequest("Cash", 10m)]);

    private static Task<FiscalDocumentResult> RecordAsync(TestModuleDatabase<FiscalDbContext> db, FiscalDocumentRequest request)
        => db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(request));

    private static Task<Fiscal.Application.DTOs.FiscalSendSummary> RoundAsync(TestModuleDatabase<FiscalDbContext> db)
        => db.InScopeAsync(sp => sp.GetRequiredService<SendFiscalDocumentsCommandHandler>().ExecuteAsync(force: true));

    private static Task<FiscalDocument> DocumentAsync(TestModuleDatabase<FiscalDbContext> db, Guid id)
        => db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync(d => d.Id == id));

    private static Task<Platform.Core.Results.Result> CorrectAsync(TestModuleDatabase<FiscalDbContext> db, Guid id)
        => db.InScopeAsync(sp => sp.GetRequiredService<CorrectFiscalDocumentCommandHandler>().HandleAsync(new CorrectFiscalDocumentCommand(id)));

    // ---- late

    [Fact]
    public async Task A_document_unsent_24_hours_after_its_sale_is_held_as_late_and_the_others_still_go()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        _part.OnSend = d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: "offline");
        var old = (await RecordAsync(db, Sale("S-OLD"))).DocumentId!.Value;
        await RoundAsync(db);                                                     // offline: it waits

        _clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
        _part.OnSend = null;
        var fresh = (await RecordAsync(db, Sale("S-NEW"))).DocumentId!.Value;
        var round = await RoundAsync(db);

        Assert.Equal((1, 1), (round.Late, round.Sent));
        var late = await DocumentAsync(db, old);
        Assert.Equal((FiscalDocumentState.Late, SendFiscalDocumentsCommandHandler.LateReason), (late.State, late.StateReason));
        Assert.Equal(["S-NEW"], _part.Sends.Last().Select(d => d.Reference));   // the late one was not handed over
        Assert.Equal(FiscalDocumentState.Accepted, (await DocumentAsync(db, fresh)).State);

        var alerts = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery()))).Value!;
        Assert.Equal((1, true), (alerts.Late, alerts.NeedsAttention));
        var till = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalNotices>().GetTillNoticeAsync());
        Assert.Equal(1, till!.OverdueCount);                                       // the till is told too
        await RoundAsync(db);
        Assert.Equal(FiscalDocumentState.Late, (await DocumentAsync(db, old)).State);   // held: never tried again by itself
    }

    [Fact]
    public async Task Send_late_receipts_releases_them_and_sends_them_at_once_with_a_fresh_sign_in()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        Task<Platform.Core.Results.Result<Fiscal.Application.DTOs.FiscalSendSummary>> SendLateAsync()
            => db.InScopeAsync(sp => sp.GetRequiredService<SendLateFiscalDocumentsCommandHandler>().HandleAsync(new SendLateFiscalDocumentsCommand()));
        Assert.Equal("Fiscal.Late.None", (await SendLateAsync()).Error.Code);

        var id = (await RecordAsync(db, Sale("S-OLD", _clock.GetUtcNow().UtcDateTime.AddHours(-30)))).DocumentId!.Value;
        await RoundAsync(db);
        Assert.Equal(FiscalDocumentState.Late, (await DocumentAsync(db, id)).State);

        var sent = await SendLateAsync();
        Assert.Equal(1, sent.Value.Sent);
        var document = await DocumentAsync(db, id);
        Assert.Equal((FiscalDocumentState.Accepted, true), (document.State, document.SendLate));
        Assert.True(_part.FreshSignIns > 0);
        Assert.Contains(_events, e => e.Action == "fiscal.late-sent");
    }

    // ---- correct and resend, build and send

    [Fact]
    public async Task A_refused_document_is_corrected_at_its_place_of_the_chain_and_the_refused_one_is_kept_as_replaced()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        _part.OnCheck = d => new FiscalSendOutcome(d.DocumentId, d.Reference == "S-1" ? FiscalSendResult.Rejected : FiscalSendResult.Accepted, Reason: "Item code is invalid");
        var sale = Sale("S-1");
        var refusedId = (await RecordAsync(db, sale)).DocumentId!.Value;
        await RecordAsync(db, Sale("S-2"));
        await RoundAsync(db);
        var refused = await DocumentAsync(db, refusedId);
        Assert.Equal(FiscalDocumentState.Rejected, refused.State);

        _clock.Advance(TimeSpan.FromHours(30));                                   // a correction is not held by the 24 hours
        _part.OnCheck = null;
        Assert.True((await CorrectAsync(db, refusedId)).IsSuccess);

        var replaced = await DocumentAsync(db, refusedId);
        var copy = await DocumentAsync(db, replaced.ReplacedByDocumentId!.Value);
        Assert.Equal(FiscalDocumentState.Replaced, replaced.State);
        Assert.Equal((refused.Number, refused.PreviousHash, refusedId, FiscalDocumentState.Waiting), (copy.Number, copy.PreviousHash, copy.ReplacesDocumentId, copy.State));
        Assert.NotEqual(refused.Hash, copy.Hash);
        Assert.Equal(refused.Hash, _part.Built.Last().ReferenceOldHash);           // Egypt: referenceOldUUID
        Assert.Equal(sale.IssuedAtUtc, _part.Built.Last().Request.IssuedAtUtc);     // the sale's own time
        Assert.Equal(2L, await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().Select(t => t.Counter).SingleAsync()));   // the chain did not move

        var mark = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalReceiptMarks>().GetMarkAsync(FiscalDocumentKindContract.Sale, sale.SourceId));
        Assert.Equal("QR:" + copy.Hash, mark!.QrPayload);                          // a copy of the receipt prints the corrected one
        await RoundAsync(db);
        Assert.Equal(FiscalDocumentState.Accepted, (await DocumentAsync(db, copy.Id)).State);
        Assert.Contains(_events, e => e.Action == "fiscal.corrected");

        Assert.Equal("Fiscal.Correct.NotNeeded", (await CorrectAsync(db, copy.Id)).Error.Code);
        Assert.Equal("Fiscal.Correct.NotNeeded", (await CorrectAsync(db, refusedId)).Error.Code);
    }

    [Fact]
    public async Task A_document_that_could_not_be_built_is_built_at_the_end_of_the_chain_once_the_codes_are_there()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        _part.Refuse = "'Cola' has no item code";
        var id = (await RecordAsync(db, Sale("S-1"))).DocumentId!.Value;
        _part.Refuse = null;
        await RecordAsync(db, Sale("S-2"));

        _part.Refuse = "still no item code";
        Assert.Equal(("Fiscal.Correct.NotBuilt", "still no item code"), ((await CorrectAsync(db, id)).Error.Code, (await CorrectAsync(db, id)).Error.Description));
        _part.Refuse = null;
        Assert.True((await CorrectAsync(db, id)).IsSuccess);

        var built = await DocumentAsync(db, id);
        Assert.Equal((FiscalDocumentState.Waiting, 2L, (string?)null), (built.State, built.Number!.Value, built.StateReason));
        var till = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        Assert.Equal((2L, built.Hash), (till.Counter, till.LastDocumentHash));
        Assert.Contains(_events, e => e.Action == "fiscal.built-again");
    }

    [Fact]
    public async Task A_document_made_before_its_content_was_kept_cannot_be_built_again()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        _part.Refuse = "no code";
        var id = (await RecordAsync(db, Sale("S-1"))).DocumentId!.Value;
        _part.Refuse = null;
        await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Database.ExecuteSqlRawAsync("UPDATE fis_Documents SET Request = NULL"));

        Assert.Equal("Fiscal.Correct.NoContent", (await CorrectAsync(db, id)).Error.Code);
    }

    // ---- Egypt's side

    [Fact]
    public void Egypts_corrected_receipt_names_the_refused_one_as_referenceOldUUID()
    {
        var settings = FiscalSettings.Default();
        settings.Update(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", new FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"), false, null, DateTime.UtcNow);
        var product = Guid.NewGuid();
        var codes = new Dictionary<Guid, FiscalLineCodes> { [product] = new(FiscalItemCodeType.EGS, "EG-1-COLA", "EA", "T1", "V009", null, null, null) };
        var request = Sale("S-1") with { Lines = [new FiscalLineRequest(product, "COLA", "Cola", 1m, 10m, 0m, 10m, 0.14m, 1.23m)] };
        var old = new string('a', 64);

        var built = new EgyptCountryPart().Build(new FiscalBuildInput(request, settings, Guid.NewGuid(), "PC", 5, new string('b', 64), codes, null, "MTJ-1", FiscalEnvironment.PreProduction, old));

        using var json = JsonDocument.Parse(built.Value.Payload);
        var header = json.RootElement.GetProperty("header");
        Assert.Equal(old, header.GetProperty("referenceOldUUID").GetString());
        Assert.Equal(["dateTimeIssued", "receiptNumber", "uuid", "previousUUID", "referenceOldUUID", "currency"], header.EnumerateObject().Select(p => p.Name));
        Assert.StartsWith("https://preprod.invoicing.eta.gov.eg/receipts/search/", built.Value.QrPayload);   // the till's environment's portal
    }
}
