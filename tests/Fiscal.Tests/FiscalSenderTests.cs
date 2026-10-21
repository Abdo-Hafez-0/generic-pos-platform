using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Application.Queries;
using Fiscal.Application.Security;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.DependencyInjection;
using Fiscal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Tests.Common;
using Tests.Common.Security;

namespace Fiscal.Tests;

/// <summary>MISS-11a-3: the outbox - documents handed to the authority in chain order, answers checked, retries with a growing pause, warnings.</summary>
public sealed class FiscalSenderTests
{
    private readonly TestCountryPart _part = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    private Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync(Action<IServiceCollection>? more = null)
        => TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            s.AddSingleton<IFiscalCountryPart>(_part);
            s.AddSingleton<TimeProvider>(_clock);
            more?.Invoke(s);
        });

    private static async Task SwitchOnEgyptAsync(TestModuleDatabase<FiscalDbContext> db)
        => Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", new Fiscal.Domain.Entities.FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))))).IsSuccess);

    private async Task<Guid> SellAsync(TestModuleDatabase<FiscalDbContext> db, decimal total = 10m)
    {
        var id = Guid.NewGuid();
        var made = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, id,
            "S-" + id.ToString("N")[..8].ToUpperInvariant(), _clock.GetUtcNow().UtcDateTime, [], total, 0m, [])));
        return made.DocumentId!.Value;
    }

    private static Task<Fiscal.Application.DTOs.FiscalSendSummary> RoundAsync(TestModuleDatabase<FiscalDbContext> db, bool force = false)
        => db.InScopeAsync(sp => sp.GetRequiredService<SendFiscalDocumentsCommandHandler>().ExecuteAsync(force));

    private static Task<Fiscal.Domain.Entities.FiscalDocument> DocumentAsync(TestModuleDatabase<FiscalDbContext> db, Guid id)
        => db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync(d => d.Id == id));

    [Fact]
    public async Task Waiting_documents_are_handed_over_in_chain_order_and_their_answers_are_checked()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        var first = await SellAsync(db);
        var second = await SellAsync(db);

        var round = await RoundAsync(db);

        Assert.Equal((true, 2, 2), (round.Ran, round.Sent, round.Accepted));          // handed over, then checked in the same round
        Assert.Equal([1L, 2L], Assert.Single(_part.Sends).Select(d => d.Number));
        var document = await DocumentAsync(db, first);
        Assert.Equal((FiscalDocumentState.Accepted, "SUB-1", 1), (document.State, document.AuthorityReference, document.Attempts));
        Assert.NotNull(document.AnsweredAt);

        var nothing = await RoundAsync(db);
        Assert.False(nothing.Ran);
        Assert.Single(_part.Sends);
        Assert.Equal(FiscalDocumentState.Accepted, (await DocumentAsync(db, second)).State);
    }

    [Fact]
    public async Task An_answer_that_is_not_there_yet_keeps_the_document_sent_until_a_later_round()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        var id = await SellAsync(db);
        _part.OnCheck = d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.StillWaiting);

        await RoundAsync(db);
        Assert.Equal(FiscalDocumentState.Sent, (await DocumentAsync(db, id)).State);

        _part.OnCheck = null;
        await RoundAsync(db);
        Assert.Equal(FiscalDocumentState.Accepted, (await DocumentAsync(db, id)).State);
        Assert.Single(_part.Sends);                                                       // handed over once
    }

    [Fact]
    public async Task Not_handed_over_documents_wait_a_growing_pause_before_the_next_try_unless_sent_now()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        var id = await SellAsync(db);
        _part.OnSend = d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: "This till is not registered with the tax authority.");

        var first = await RoundAsync(db);
        var document = await DocumentAsync(db, id);
        Assert.Equal((1, "This till is not registered with the tax authority."), (first.NotSent, first.LastReason));
        Assert.Equal((FiscalDocumentState.Waiting, 1, _clock.GetUtcNow().UtcDateTime.AddMinutes(1)), (document.State, document.Attempts, document.NextAttemptAt));

        Assert.False((await RoundAsync(db)).Ran);                                         // not due yet
        _clock.Advance(TimeSpan.FromMinutes(1));
        await RoundAsync(db);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(2), (await DocumentAsync(db, id)).NextAttemptAt);   // 1, 2, 4 ... minutes

        Assert.True((await RoundAsync(db, force: true)).Ran);                             // "Send now" does not wait
        for (var i = 0; i < 10; i++) await RoundAsync(db, force: true);
        var late = await DocumentAsync(db, id);
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddMinutes(30), late.NextAttemptAt);  // never more than 30 minutes

        _part.OnSend = null;
        _clock.Advance(TimeSpan.FromMinutes(30));
        await RoundAsync(db);
        Assert.Equal(FiscalDocumentState.Accepted, (await DocumentAsync(db, id)).State);
    }

    [Fact]
    public async Task A_broken_connection_answers_nothing_and_everything_is_tried_again()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        var id = await SellAsync(db);
        _part.SendThrows = true;

        var round = await RoundAsync(db);

        Assert.Equal((1, "No connection to the tax authority."), (round.NotSent, round.LastReason));
        Assert.Equal(FiscalDocumentState.Waiting, (await DocumentAsync(db, id)).State);
    }

    [Fact]
    public async Task A_refused_document_keeps_the_authoritys_reasons_and_the_warnings_count_what_needs_attention()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        var refused = await SellAsync(db);
        _part.OnSend = d => d.DocumentId == refused
            ? new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Rejected, Reason: "Item code EG-123 is not active.")
            : new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: "offline");
        await RoundAsync(db);
        _part.Refuse = "'Cola' has no item code.";
        await SellAsync(db);                                                               // not built
        _part.Refuse = null;
        await SellAsync(db);                                                               // waits

        var document = await DocumentAsync(db, refused);
        Assert.Equal((FiscalDocumentState.Rejected, "Item code EG-123 is not active."), (document.State, document.StateReason));

        var alerts = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery()))).Value!;
        Assert.Equal((1, 1, 1, 0, true), (alerts.Outstanding, alerts.NotBuilt, alerts.Rejected, alerts.OverLimit, alerts.NeedsAttention));

        _clock.Advance(TimeSpan.FromHours(7));                                             // over the managers' 6 hours
        alerts = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery()))).Value!;
        Assert.Equal(1, alerts.OverLimit);
        Assert.Null(await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalNotices>().GetTillNoticeAsync()));   // not yet the till's 12

        _clock.Advance(TimeSpan.FromHours(6));
        var notice = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalNotices>().GetTillNoticeAsync());
        Assert.Equal((1, 12d), (notice!.OverdueCount, notice.LimitHours));

        var attention = (await db.InScopeAsync(sp => sp.GetRequiredService<ListFiscalDocumentsQueryHandler>()
            .HandleAsync(new ListFiscalDocumentsQuery([FiscalDocumentState.NotBuilt, FiscalDocumentState.Rejected])))).Value;
        Assert.Equal(2, attention.Count);
        Assert.Equal(3, (await db.InScopeAsync(sp => sp.GetRequiredService<ListFiscalDocumentsQueryHandler>().HandleAsync(new ListFiscalDocumentsQuery()))).Value.Count);
    }

    [Fact]
    public async Task With_receipts_off_there_is_nothing_to_send_and_no_warning()
    {
        await using var db = await DatabaseAsync();

        Assert.False((await RoundAsync(db, force: true)).Ran);
        Assert.Null((await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery()))).Value);
        Assert.Null(await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalNotices>().GetTillNoticeAsync()));
    }

    [Fact]
    public async Task Send_now_and_the_documents_list_need_the_fiscal_manage_permission()
    {
        var authorization = new ScriptedAuthorizationService();
        await using var db = await DatabaseAsync(s => s.AddSingleton<IAuthorizationService>(authorization));

        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SendFiscalDocumentsCommandHandler>().HandleAsync())).IsFailure);
        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<ListFiscalDocumentsQueryHandler>().HandleAsync(new ListFiscalDocumentsQuery()))).IsFailure);
        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery()))).IsFailure);
        Assert.All(authorization.Asked, c => Assert.Equal(FiscalCapabilities.Manage, c));
    }
}
