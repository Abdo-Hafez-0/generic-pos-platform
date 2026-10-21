using System.Security.Cryptography;
using System.Text;
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
using Fiscal.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Auditing;
using Platform.Core.Results;
using Tests.Common;
using Tests.Common.Security;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11c-3: the till's registration (owner's choices: a window, secrets protected by Windows outside the database, the environment locked
/// after the first send) and Egypt's sending - the sign-in for a token, batches of up to 500 receipts without a signature (owner's
/// decision), refusals in the ETA's words, reading a submission back - against a fake ETA behind the transport contract.
/// </summary>
public sealed class EtaSendingTests : IDisposable
{
    private readonly FakeEta _eta = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matjar-fiscal-" + Guid.NewGuid().ToString("N"));
    private readonly List<BusinessEvent> _events = [];

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static FiscalTillConnection Connection(FiscalEnvironment environment = FiscalEnvironment.PreProduction)
        => new(environment, "client-id-1", FakeEta.Secret, "MTJ-1A2B-3C4D-5E6F", "Windows 10.0.19045", "1", FakeEta.Key);

    private static FiscalOutgoingDocument Document(int number, string? submission = null, int padding = 0)
    {
        var uuid = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("receipt " + number)));
        var payload = "{\"header\":{\"uuid\":\"" + uuid + "\"},\"note\":\"" + new string('x', padding) + "\"}";
        return new FiscalOutgoingDocument(Guid.NewGuid(), FiscalDocumentKind.Sale, "S-" + number, number, uuid, payload, DateTime.UtcNow, submission);
    }

    private static FiscalSendInput Input(IReadOnlyList<FiscalOutgoingDocument> documents, FiscalTillConnection? connection)
        => new(documents, FiscalSettings.Default(), Guid.NewGuid(), "SHOP-PC", FiscalTillRegistration.Registered, connection);

    private EgyptCountryPart Part(IFiscalHttpTransport? transport = null) => new(new EgyptReceiptOptions(), transport ?? _eta, _clock);

    // ---- the sign-in

    [Fact]
    public async Task The_sign_in_sends_the_client_credentials_and_the_POS_headers_to_the_environments_identity_service()
    {
        Assert.True((await Part().TestSignInAsync(Connection())).IsSuccess);
        var request = Assert.Single(_eta.Requests);
        Assert.Equal(("POST", "https://id.preprod.eta.gov.eg/connect/token", "application/x-www-form-urlencoded"), (request.Method, request.Url, request.ContentType));
        Assert.Equal($"grant_type=client_credentials&client_id=client-id-1&client_secret={FakeEta.Secret}", request.Body);
        Assert.Equal(("MTJ-1A2B-3C4D-5E6F", "Windows 10.0.19045", "1", FakeEta.Key),
            (request.Headers["posserial"], request.Headers["pososversion"], request.Headers["posmodelframework"], request.Headers["presharedkey"]));

        await Part().TestSignInAsync(Connection(FiscalEnvironment.Production));
        Assert.Equal("https://id.eta.gov.eg/connect/token", _eta.Requests[1].Url);
    }

    [Theory]
    [InlineData("invalid_presharedkey", "the pre-shared key is not right")]
    [InlineData("invalid_posserial", "the POS serial is not the one registered for this client ID")]
    [InlineData("invalid_client", "the client ID or the client secret is not right")]
    public async Task A_refused_sign_in_says_why_in_plain_words(string error, string why)
    {
        _eta.SignInError = error;
        var refused = await Part().TestSignInAsync(Connection());
        Assert.Equal($"The tax authority refused the sign-in: {why}.", refused.Error.Description);
    }

    [Fact]
    public async Task Without_a_connection_the_sign_in_says_so()
    {
        _eta.Offline = true;
        Assert.Contains("could not be reached", (await Part().TestSignInAsync(Connection())).Error.Description);
        Assert.Equal(EtaService.NoTransport, (await new EgyptCountryPart().TestSignInAsync(Connection())).Error.Description);
    }

    // ---- sending

    [Fact]
    public async Task A_batch_goes_with_a_token_and_no_signature_and_each_receipt_is_handed_over_or_refused_with_the_ETAs_words()
    {
        var documents = new[] { Document(1), Document(2) };
        _eta.RefuseAtSubmission[documents[1].Hash] = "Item code is invalid";

        var outcomes = await Part().SendAsync(Input(documents, Connection()));

        var submit = _eta.Requests[1];
        Assert.Equal(("POST", "https://api.preprod.invoicing.eta.gov.eg/api/v1/receiptsubmissions", "Bearer token-1"), (submit.Method, submit.Url, submit.Headers["Authorization"]));
        using (var body = JsonDocument.Parse(submit.Body!))
        {
            Assert.Equal(["receipts"], body.RootElement.EnumerateObject().Select(p => p.Name));   // owner's decision: no signature for now
            Assert.Equal(2, body.RootElement.GetProperty("receipts").GetArrayLength());
        }

        var submission = Assert.Single(_eta.Submissions).Id;
        Assert.Equal((FiscalSendResult.Sent, submission), (outcomes[0].Result, outcomes[0].AuthorityReference));
        Assert.Equal(FiscalSendResult.Rejected, outcomes[1].Result);
        Assert.Equal("Validation error: Item code is invalid (receipt.itemData[0].itemCode)", outcomes[1].Reason);
    }

    [Fact]
    public async Task The_token_is_kept_until_shortly_before_it_expires_and_asked_again_when_refused()
    {
        var part = Part();
        await part.SendAsync(Input([Document(1)], Connection()));
        await part.SendAsync(Input([Document(2)], Connection()));
        Assert.Equal(1, _eta.TokensIssued);

        _clock.Advance(TimeSpan.FromMinutes(59));                 // two minutes before the hour: asked again
        await part.SendAsync(Input([Document(3)], Connection()));
        Assert.Equal(2, _eta.TokensIssued);

        _eta.ExpireNextToken = true;                              // refused: signed in again once, then sent
        var sent = Assert.Single(await part.SendAsync(Input([Document(4)], Connection())));
        Assert.Equal((FiscalSendResult.Sent, 3), (sent.Result, _eta.TokensIssued));
    }

    [Theory]
    [InlineData(422, "{\"error\":{\"code\":\"DuplicateSubmission\"}}", "same batch less than 10 minutes ago")]
    [InlineData(400, "{\"error\":{\"code\":\"BadStructure\",\"message\":\"The receipts are not valid JSON\"}}", "refused the batch (400): The receipts are not valid JSON")]
    [InlineData(500, "", "refused the batch (500).")]
    public async Task A_refused_batch_waits_with_the_reason(int status, string body, string reason)
    {
        _eta.NextSubmission = (status, body);
        var outcome = Assert.Single(await Part().SendAsync(Input([Document(1)], Connection())));
        Assert.Equal(FiscalSendResult.NotSent, outcome.Result);
        Assert.Contains(reason, outcome.Reason);
    }

    [Fact]
    public async Task Offline_unregistered_or_without_a_transport_nothing_is_sent_and_each_says_why()
    {
        _eta.Offline = true;
        Assert.All(await Part().SendAsync(Input([Document(1), Document(2)], Connection())), o =>
            Assert.Equal((FiscalSendResult.NotSent, true), (o.Result, o.Reason!.Contains("could not be reached"))));
        Assert.Equal(EgyptCountryPart.NotRegistered, Assert.Single(await Part().SendAsync(Input([Document(1)], null))).Reason);
        Assert.Equal(EtaService.NoTransport, Assert.Single(await new EgyptCountryPart().SendAsync(Input([Document(1)], Connection()))).Reason);
    }

    [Fact]
    public async Task More_than_500_receipts_or_1_4_MB_go_in_several_submissions_in_the_chains_order()
    {
        var many = Enumerable.Range(1, 501).Select(n => Document(n)).ToList();
        var outcomes = await Part().SendAsync(Input(many, Connection()));
        Assert.Equal([500, 1], _eta.Submissions.Select(s => s.Uuids.Count));
        Assert.Equal(many.Select(d => d.Hash), _eta.Submissions.SelectMany(s => s.Uuids));
        Assert.All(outcomes, o => Assert.Equal(FiscalSendResult.Sent, o.Result));

        var large = Enumerable.Range(1, 3).Select(n => Document(n, padding: 600_000)).ToList();
        Assert.Equal([2, 1], EtaService.Batches(large).Select(b => b.Count));
    }

    // ---- reading a submission back

    [Fact]
    public async Task A_submission_is_read_back_page_by_page_valid_accepted_invalid_refused_and_unanswered_left_waiting()
    {
        var documents = Enumerable.Range(1, 150).Select(n => Document(n)).ToList();
        var part = Part();
        var handed = await part.SendAsync(Input(documents, Connection()));
        var sent = documents.Zip(handed, (d, o) => d with { AuthorityReference = o.AuthorityReference }).ToList();
        _eta.Verdicts[sent[0].Hash] = "Valid";
        _eta.Verdicts[sent[1].Hash] = "Invalid";
        _eta.Verdicts[sent[2].Hash] = "Cancelled";
        _eta.Verdicts[sent[149].Hash] = "Valid";                 // on the second page

        var answers = (await part.CheckAsync(Input(sent, Connection()))).ToDictionary(o => o.DocumentId);

        Assert.Equal(4, answers.Count);                           // the rest: no answer yet
        Assert.Equal(FiscalSendResult.Accepted, answers[sent[0].DocumentId].Result);
        Assert.Equal((FiscalSendResult.Rejected, "The item code is not registered (itemData[0].itemCode)"), (answers[sent[1].DocumentId].Result, answers[sent[1].DocumentId].Reason));
        Assert.Equal("The receipt was cancelled at the tax authority.", answers[sent[2].DocumentId].Reason);
        Assert.Equal(FiscalSendResult.Accepted, answers[sent[149].DocumentId].Result);
        Assert.Contains(_eta.Requests, r => r.Url.EndsWith("details?PageNo=2&PageSize=100", StringComparison.Ordinal));

        _eta.Offline = true;
        Assert.Empty(await part.CheckAsync(Input(sent, Connection())));
    }

    // ---- the registration and a whole round on the real Fiscal core

    /// <summary>Builds a stand-in receipt (its uuid in the header, like Egypt's); sends and checks through Egypt's part and the fake ETA.</summary>
    private sealed class SendingPart(EgyptCountryPart egypt) : IFiscalCountryPart
    {
        public FiscalCountry Country => FiscalCountry.Egypt;

        public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Request.Reference + input.PreviousHash)));
            return Result.Success(new FiscalBuiltDocument("{\"header\":{\"uuid\":\"" + hash + "\"}}", hash, "QR:" + hash));
        }

        public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default) => egypt.SendAsync(input, cancellationToken);
        public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default) => egypt.CheckAsync(input, cancellationToken);
        public Task<Result> TestSignInAsync(FiscalTillConnection connection, CancellationToken cancellationToken = default) => egypt.TestSignInAsync(connection, cancellationToken);
    }

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
            s.AddSingleton<IFiscalCountryPart>(new SendingPart(Part()));
            s.AddSingleton<TimeProvider>(_clock);
            s.AddSingleton<IFiscalSecretStore>(new ProtectedFiscalSecretStore(Path.Combine(_folder, "till-secrets.bin"), new FakeProtector()));
            s.AddSingleton<IBusinessEventSink>(new Events(_events));
        });

    private static async Task SwitchOnEgyptAsync(TestModuleDatabase<FiscalDbContext> db)
        => Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>()
            .HandleAsync(new SaveFiscalSettingsCommand(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", new FiscalAddress("القاهرة", "مدينة نصر", "شارع عباس العقاد", "12"))))).IsSuccess);

    private static Task<Result> RegisterAsync(TestModuleDatabase<FiscalDbContext> db, FiscalEnvironment environment = FiscalEnvironment.PreProduction,
        string? secret = FakeEta.Secret, string? key = FakeEta.Key)
        => db.InScopeAsync(sp => sp.GetRequiredService<SaveTillRegistrationCommandHandler>()
            .HandleAsync(new SaveTillRegistrationCommand(environment, "client-id-1", secret, "Windows 10.0.19045", "1", key)));

    private static Task<FiscalTill> TillAsync(TestModuleDatabase<FiscalDbContext> db)
        => db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());

    [Fact]
    public async Task The_registration_keeps_the_secrets_protected_outside_the_database_and_never_in_the_audit()
    {
        await using var db = await DatabaseAsync();
        Assert.Equal("Fiscal.Till.SecretRequired", (await RegisterAsync(db, secret: null)).Error.Code);
        Assert.Equal("Fiscal.Till.ClientIdInvalid", (await db.InScopeAsync(sp => sp.GetRequiredService<SaveTillRegistrationCommandHandler>()
            .HandleAsync(new SaveTillRegistrationCommand(FiscalEnvironment.PreProduction, " ", "s", "Windows", "1", "k")))).Error.Code);
        Assert.Equal("Fiscal.Till.EnvironmentRequired", (await RegisterAsync(db, FiscalEnvironment.None)).Error.Code);

        Assert.True((await RegisterAsync(db)).IsSuccess);
        var till = await TillAsync(db);
        Assert.Equal((FiscalTillRegistration.Registered, FiscalEnvironment.PreProduction, "client-id-1", "Windows 10.0.19045", "1"),
            (till.Registration, till.Environment, till.ClientId, till.PosOsVersion, till.PosModelFramework));

        var file = await File.ReadAllBytesAsync(Path.Combine(_folder, "till-secrets.bin"));
        Assert.DoesNotContain(FakeEta.Secret, Encoding.UTF8.GetString(file));
        var columns = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Database.SqlQueryRaw<string>(
            "SELECT group_concat(name, ',') AS Value FROM pragma_table_info('fis_Tills')").SingleAsync());
        Assert.DoesNotContain("Secret", columns);
        Assert.DoesNotContain("Preshared", columns);
        var audited = Assert.Single(_events, e => e.Action == "fiscal.till-registered");
        Assert.DoesNotContain(FakeEta.Secret, audited.Summary + audited.Details);
        Assert.DoesNotContain(FakeEta.Key, audited.Summary + audited.Details);

        Assert.True((await RegisterAsync(db, secret: null, key: null)).IsSuccess);   // the kept secrets stay
        var setup = (await db.InScopeAsync(sp => sp.GetRequiredService<GetFiscalSetupQueryHandler>().HandleAsync(new GetFiscalSetupQuery()))).Value;
        Assert.Equal((true, true, false), (setup.Connection!.HasSecrets, setup.Connection.CanProtectSecrets, setup.Connection.EnvironmentLocked));
    }

    [Fact]
    public async Task A_whole_round_signs_in_sends_reads_the_answers_back_and_locks_the_environment()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        var ids = new List<Guid>();
        for (var i = 0; i < 2; i++)
            ids.Add((await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(new FiscalDocumentRequest(FiscalDocumentKindContract.Sale,
                Guid.NewGuid(), "S-" + i, _clock.GetUtcNow().UtcDateTime, [], 10m, 0m, [])))).DocumentId!.Value);
        Task<FiscalSendSummaryResult> RoundAsync() => db.InScopeAsync(async sp => new FiscalSendSummaryResult(
            await sp.GetRequiredService<SendFiscalDocumentsCommandHandler>().ExecuteAsync(force: true)));
        Task<List<FiscalDocument>> DocumentsAsync() => db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().OrderBy(d => d.Number).ToListAsync());

        // not registered: they wait, saying why
        var unregistered = await RoundAsync();
        Assert.Equal(2, unregistered.Summary.NotSent);
        Assert.Equal(EgyptCountryPart.NotRegistered, (await DocumentsAsync())[0].StateReason);
        Assert.Empty(_eta.Requests);

        // registered: signed in, handed over, the answers read back
        Assert.True((await RegisterAsync(db)).IsSuccess);
        var documents = await DocumentsAsync();
        _eta.Verdicts[documents[0].Hash!] = "Valid";
        _eta.Verdicts[documents[1].Hash!] = "Invalid";
        var round = await RoundAsync();
        Assert.Equal((2, 1, 1), (round.Summary.Sent, round.Summary.Accepted, round.Summary.Rejected));
        documents = await DocumentsAsync();
        Assert.Equal((FiscalDocumentState.Accepted, _eta.Submissions[0].Id), (documents[0].State, documents[0].AuthorityReference));
        Assert.Equal(FiscalDocumentState.Rejected, documents[1].State);
        Assert.Contains("The item code is not registered", documents[1].StateReason);

        // receipts reached the authority: the environment is locked (the same one may be saved again)
        Assert.NotNull((await TillAsync(db)).FirstSentAt);
        Assert.Equal("Fiscal.Till.EnvironmentLocked", (await RegisterAsync(db, FiscalEnvironment.Production)).Error.Code);
        Assert.True((await RegisterAsync(db, FiscalEnvironment.PreProduction, null, null)).IsSuccess);
    }

    [Fact]
    public async Task Test_sign_in_records_the_answer_on_the_till_and_in_the_audit()
    {
        await using var db = await DatabaseAsync();
        await SwitchOnEgyptAsync(db);
        Task<Result> TestAsync() => db.InScopeAsync(sp => sp.GetRequiredService<TestFiscalSignInCommandHandler>().HandleAsync(new TestFiscalSignInCommand()));

        Assert.Equal("Fiscal.SignIn.NotRegistered", (await TestAsync()).Error.Code);
        Assert.True((await RegisterAsync(db)).IsSuccess);
        Assert.True((await TestAsync()).IsSuccess);
        Assert.Equal((true, _clock.GetUtcNow().UtcDateTime), ((await TillAsync(db)).LastSignInOk!.Value, (await TillAsync(db)).LastSignInAt!.Value));

        _eta.SignInError = "invalid_presharedkey";
        Assert.Contains("pre-shared key", (await TestAsync()).Error.Description);
        Assert.False((await TillAsync(db)).LastSignInOk);
        Assert.Equal(2, _events.Count(e => e.Action == "fiscal.sign-in-tested"));
    }

    private sealed record FiscalSendSummaryResult(Fiscal.Application.DTOs.FiscalSendSummary Summary);
}
