using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fiscal.Application.Abstractions;
using Fiscal.Application.Commands;
using Fiscal.Application.Queries;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.DependencyInjection;
using Fiscal.Infrastructure.Persistence;
using Fiscal.Infrastructure.Services;
using Fiscal.Saudi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Auditing;
using Tests.Common;
using Tests.Common.Security;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11d-4 (owner's decisions: reported right after each sale; accepted with ZATCA's warnings shown; "Correct and resend" as a new invoice
/// at the end of the chain): reporting each signed invoice to ZATCA and reading its answer.
/// MISS-11d-3 (owner's choices: one button with the steps ticked; simulation or production; the earlier invoices signed at its end; the
/// certificate's expiry warned 30 days before and on the till in the last 7): onboarding this till with ZATCA through the module - the CSR,
/// the compliance CSID and checks, the production CSID, the certificate kept on the till and its secret protected, invoices signed from
/// then on, renewal - against a stand-in ZATCA that issues real certificates for the till's key.
/// </summary>
public sealed class ZatcaOnboardingTests : IDisposable
{
    private readonly FakeZatca _zatca = new();
    private readonly SaudiStampTests.TestKeyStore _keys = new(null);
    private readonly TestClock _clock = new(DateTimeOffset.UtcNow);
    private readonly List<BusinessEvent> _events = [];
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matjar-zatca-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Water = Guid.NewGuid(), Vat15 = Guid.NewGuid();

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
    }

    private sealed class Events(List<BusinessEvent> recorded) : IBusinessEventSink
    {
        public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
        {
            recorded.Add(businessEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class Ticks : IProgress<FiscalOnboardingProgress>
    {
        public List<FiscalOnboardingProgress> Seen { get; } = [];
        public void Report(FiscalOnboardingProgress value) => Seen.Add(value);
    }

    private sealed class StubCatalog : Catalog.Contracts.Interfaces.ICatalogListing
    {
        public Task<IReadOnlyList<Catalog.Contracts.Models.CatalogProductListItem>> ListProductsAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Catalog.Contracts.Models.CatalogProductListItem>>([new(Water, "WATER", "Water", Guid.Empty, "Piece", "pc", [], true)]);

        public Task<IReadOnlyList<Catalog.Contracts.Models.CatalogUnitListItem>> ListUnitsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Catalog.Contracts.Models.CatalogUnitListItem>>([]);
    }

    private sealed class StubTax : Pricing.Contracts.Interfaces.ITaxRateListing, Pricing.Contracts.Interfaces.ITaxRateResolver
    {
        public Task<IReadOnlyList<Pricing.Contracts.Models.TaxRateListItem>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<Pricing.Contracts.Models.TaxRateListItem>>([new(Vat15, "VAT15", "VAT 15%", 0.15m, true, true)]);

        public Task<Pricing.Contracts.Models.TaxResolutionResult> ResolveAsync(Guid productId, CancellationToken cancellationToken = default)
            => Task.FromResult(new Pricing.Contracts.Models.TaxResolutionResult(true, 0.15m, Vat15, "VAT15", "VAT 15%"));
    }

    private async Task<TestModuleDatabase<FiscalDbContext>> DatabaseAsync()
    {
        Directory.CreateDirectory(_folder);
        var db = await TestModuleDatabase<FiscalDbContext>.CreateAsync(s =>
        {
            s.AddFiscalCore();
            s.AddSingleton<Catalog.Contracts.Interfaces.ICatalogListing, StubCatalog>();
            var tax = new StubTax();
            s.AddSingleton<Pricing.Contracts.Interfaces.ITaxRateListing>(tax);
            s.AddSingleton<Pricing.Contracts.Interfaces.ITaxRateResolver>(tax);
            s.AddSingleton<IFiscalCountryPart>(new SaudiCountryPart(keys: _keys, transport: _zatca, timeProvider: _clock));
            s.AddSingleton<TimeProvider>(_clock);
            s.AddSingleton<IFiscalSecretStore>(new ProtectedFiscalSecretStore(Path.Combine(_folder, "till-secrets.bin"), new FakeProtector()));
            s.AddSingleton<IBusinessEventSink>(new Events(_events));
        });
        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalSettingsCommandHandler>().HandleAsync(new SaveFiscalSettingsCommand(
            FiscalCountry.SaudiArabia, "300000000000003", null, null, "متجر النور", SaudiInvoiceTests.Riyadh, "CRN", "1010010000")))).IsSuccess);
        Assert.True((await db.InScopeAsync(sp => sp.GetRequiredService<SaveFiscalCodesCommandHandler>().HandleAsync(new SaveFiscalCodesCommand(
            TaxRates: [new FiscalTaxCodeChange(Vat15, null, null, "S", null, null)])))).IsSuccess);
        return db;
    }

    private static OnboardTillCommand Command(string? otp = FakeZatca.Otp, string? unit = "Till 1")
        => new(FiscalEnvironment.PreProduction, otp, unit, "Olaya branch", "Retail", "RRRD2929");

    private static Task<Platform.Core.Results.Result<FiscalOnboardingSummary>> OnboardAsync(TestModuleDatabase<FiscalDbContext> db, OnboardTillCommand command, Ticks? ticks = null)
        => db.InScopeAsync(sp => sp.GetRequiredService<OnboardTillCommandHandler>().HandleAsync(command, ticks));

    /// <summary>A sale of water at 15% (its VAT category S through the code book).</summary>
    private FiscalDocumentRequest Sale(string reference)
        => new(FiscalDocumentKindContract.Sale, Guid.NewGuid(), reference, _clock.GetUtcNow().UtcDateTime,
            [new FiscalLineRequest(Water, "WATER", "Water", 2m, 1.15m, 0m, 2.30m, 0.15m, 0.30m)], 2.30m, 0.30m, [new FiscalPaymentRequest("Cash", 2.30m)]);

    [Fact]
    public void The_CSR_names_the_unit_the_taxpayer_and_the_environment_and_is_signed_by_the_tills_key()
    {
        using var key = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
        var pem = ZatcaCsr.Create(key, new ZatcaUnit("Till 1", "Olaya branch", "Retail", "RRRD2929"), "300000000000003", "متجر النور", "MTJ-1A2B-3C4D-5E6F", "1.0.0",
            "PREZATCA-Code-Signing");

        var csr = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);   // the signature verifies
        Assert.Equal("C=SA, OU=Olaya branch, O=متجر النور, CN=Till 1", csr.SubjectName.Name);   // in the order of ZATCA's template
        Assert.Equal(key.ExportSubjectPublicKeyInfo(), csr.PublicKey.ExportSubjectPublicKeyInfo());

        var template = csr.CertificateExtensions.Single(e => e.Oid!.Value == "1.3.6.1.4.1.311.20.2");
        Assert.Equal("PREZATCA-Code-Signing", new AsnReader(template.RawData, AsnEncodingRules.DER).ReadCharacterString(UniversalTagNumber.PrintableString));

        var san = new AsnReader(csr.CertificateExtensions.Single(e => e.Oid!.Value == "2.5.29.17").RawData, AsnEncodingRules.DER).ReadSequence();
        var directory = new X500DistinguishedName(san.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 4, true)).ReadEncodedValue().ToArray());
        var fields = directory.EnumerateRelativeDistinguishedNames().ToDictionary(n => n.GetSingleElementType().Value!, n => n.GetSingleElementValue());
        Assert.Equal("1-Matjar|2-1.0.0|3-MTJ-1A2B-3C4D-5E6F", fields["2.5.4.4"]);
        Assert.Equal("300000000000003", fields["0.9.2342.19200300.100.1.1"]);
        Assert.Equal(("0100", "RRRD2929", "Retail"), (fields["2.5.4.12"], fields["2.5.4.26"], fields["2.5.4.15"]));
    }

    [Fact]
    public async Task Onboarding_ticks_every_step_keeps_the_certificate_and_its_secret_and_signs_the_earlier_invoices()
    {
        await using var db = await DatabaseAsync();
        var before = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale("S-BEFORE")));
        Assert.Equal(5, ZatcaQr.Decode(before.QrPayload!).Count);   // not onboarded: the short QR

        var ticks = new Ticks();
        var onboarded = await OnboardAsync(db, Command(), ticks);

        Assert.True(onboarded.IsSuccess, onboarded.IsFailure ? onboarded.Error.Description : null);
        Assert.Equal(1, onboarded.Value.EarlierSigned);
        Assert.Equal(
            [FiscalOnboardingStep.Key, FiscalOnboardingStep.Request, FiscalOnboardingStep.ComplianceCertificate, FiscalOnboardingStep.ComplianceChecks,
             FiscalOnboardingStep.ProductionCertificate, FiscalOnboardingStep.EarlierInvoices],
            ticks.Seen.Where(t => t.Done).Select(t => t.Step));
        Assert.Equal("3 passed", ticks.Seen.Single(t => t is { Step: FiscalOnboardingStep.ComplianceChecks, Done: true }).Detail);
        Assert.Equal(3, _zatca.CheckedInvoices.Count);
        Assert.Equal(["388", "381", "383"], _zatca.CheckedInvoices.Select(x => System.Xml.Linq.XDocument.Parse(x).Root!.Element(ZatcaXml.Cbc + "InvoiceTypeCode")!.Value));
        Assert.All(_zatca.Requests, r => Assert.StartsWith("https://gw-fatoora.zatca.gov.sa/e-invoicing/simulation/", r.Url));

        // the till: onboarded in the simulation with ZATCA's certificate for its own key; the secret protected, not in the database
        var till = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        Assert.Equal((FiscalTillRegistration.Registered, FiscalEnvironment.PreProduction, "Till 1", "Olaya branch"), (till.Registration, till.Environment, till.UnitName, till.UnitBranch));
        using (var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(till.Certificate!)))
        using (var key = _keys.Open()!)
            Assert.Equal(key.ExportSubjectPublicKeyInfo(), certificate.PublicKey.ExportSubjectPublicKeyInfo());
        Assert.Equal(onboarded.Value.CertificateExpiresAtUtc, till.CertificateExpiresAt);
        var secrets = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalSecretStore>().ReadAsync());
        Assert.StartsWith("production-secret-", secrets!.ClientSecret);
        Assert.DoesNotContain("production-secret", System.Text.Json.JsonSerializer.Serialize(till));   // not on the till's record

        // the invoice made before is signed now, at its place: the same hash, the full QR
        var earlier = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().SingleAsync());
        Assert.Contains("<ext:UBLExtensions>", earlier.Payload);
        Assert.Equal(9, ZatcaQr.Decode(earlier.QrPayload!).Count);
        Assert.Equal(ZatcaXml.Hash(ZatcaXml.Read(earlier.Payload!)), earlier.Hash);

        // from now on each sale is signed at once
        var after = await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale("S-AFTER")));
        Assert.Equal(9, ZatcaQr.Decode(after.QrPayload!).Count);
        Assert.Contains(_events, e => e.Action == "fiscal.till-onboarded" && (e.Summary ?? "").Contains("1 earlier document(s) signed", StringComparison.Ordinal));
        Assert.DoesNotContain(_events, e => ((e.Summary ?? "") + e.Details).Contains(FakeZatca.Otp, StringComparison.Ordinal) || ((e.Summary ?? "") + e.Details).Contains("secret-", StringComparison.Ordinal));

        // MISS-11d-4: both are reported to ZATCA
        var round = await db.InScopeAsync(sp => sp.GetRequiredService<SendFiscalDocumentsCommandHandler>().ExecuteAsync(force: true));
        Assert.Equal(2, round.Accepted);
        Assert.Equal(["S-BEFORE", "S-AFTER"], _zatca.Reported);
    }

    [Theory]
    [InlineData(null, "Till 1", "Fiscal.Onboarding.OtpRequired")]
    [InlineData("12ab", "Till 1", "Fiscal.Onboarding.OtpRequired")]
    [InlineData(FakeZatca.Otp, " ", "Fiscal.Onboarding.FieldRequired")]
    public async Task The_OTP_and_the_units_details_are_required(string? otp, string unit, string code)
    {
        await using var db = await DatabaseAsync();
        Assert.Equal(code, (await OnboardAsync(db, Command(otp, unit))).Error.Code);
        Assert.Empty(_zatca.Requests);
    }

    [Fact]
    public async Task A_refused_step_stops_onboarding_with_ZATCAs_words_and_the_till_stays_as_it_was()
    {
        await using var db = await DatabaseAsync();

        var wrongOtp = await OnboardAsync(db, Command(otp: "999999"));
        Assert.Contains("The provided OTP is invalid", wrongOtp.Error.Description);

        _zatca.RefuseCheck = (2, "[BR-KSA-56] the credit note must name its invoice");
        var ticks = new Ticks();
        var refused = await OnboardAsync(db, Command(), ticks);
        Assert.Contains("ZATCA refused the simplified credit note: BR-KSA-XX: [BR-KSA-56] the credit note must name its invoice", refused.Error.Description);
        Assert.Equal(FiscalOnboardingStep.ComplianceChecks, ticks.Seen.Single(t => t.Failure is not null).Step);
        Assert.DoesNotContain(ticks.Seen, t => t.Step == FiscalOnboardingStep.ProductionCertificate);

        var till = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        Assert.Equal((FiscalTillRegistration.NotRegistered, (string?)null), (till.Registration, till.Certificate));
        Assert.Null(await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalSecretStore>().ReadAsync()));
        Assert.Equal(2, _events.Count(e => e.Action == "fiscal.till-onboarding-failed"));

        _zatca.Offline = true;
        Assert.Contains("could not be reached", (await OnboardAsync(db, Command())).Error.Description);
    }

    [Fact]
    public async Task Renewal_takes_a_new_OTP_and_keeps_the_key()
    {
        await using var db = await DatabaseAsync();
        Assert.True((await OnboardAsync(db, Command())).IsSuccess);
        var first = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        _clock.Advance(TimeSpan.FromDays(10));

        Assert.Equal("Fiscal.Onboarding.OtpRequired", (await db.InScopeAsync(sp => sp.GetRequiredService<RenewTillCertificateCommandHandler>().HandleAsync(new RenewTillCertificateCommand(""))))
            .Error.Code);
        var renewed = await db.InScopeAsync(sp => sp.GetRequiredService<RenewTillCertificateCommandHandler>().HandleAsync(new RenewTillCertificateCommand(FakeZatca.Otp)));

        Assert.True(renewed.IsSuccess, renewed.IsFailure ? renewed.Error.Description : null);
        var till = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        Assert.NotEqual(first.Certificate, till.Certificate);
        using (var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(till.Certificate!)))
        using (var key = _keys.Open()!)
            Assert.Equal(key.ExportSubjectPublicKeyInfo(), certificate.PublicKey.ExportSubjectPublicKeyInfo());   // the same key
        Assert.Equal("PATCH", _zatca.Requests[^1].Method);
        Assert.StartsWith("Basic ", _zatca.Requests[^1].Headers["Authorization"]);
        Assert.Equal(FakeZatca.Otp, _zatca.Requests[^1].Headers["OTP"]);
        Assert.StartsWith("production-secret-", (await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalSecretStore>().ReadAsync()))!.ClientSecret);
        Assert.Contains(_events, e => e.Action == "fiscal.certificate-renewed");
    }

    [Fact]
    public async Task The_certificates_expiry_is_warned_to_managers_30_days_before_and_on_the_till_in_the_last_7()
    {
        await using var db = await DatabaseAsync();
        Assert.True((await OnboardAsync(db, Command())).IsSuccess);
        var expires = (await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync())).CertificateExpiresAt!.Value;
        Task<Fiscal.Application.DTOs.FiscalAlertsDto?> AlertsAsync() => db.InScopeAsync(async sp => (await sp.GetRequiredService<GetFiscalAlertsQueryHandler>().HandleAsync(new GetFiscalAlertsQuery())).Value);
        Task<FiscalTillNotice?> NoticeAsync() => db.InScopeAsync(sp => sp.GetRequiredService<IFiscalNotices>().GetTillNoticeAsync());

        Assert.Null((await AlertsAsync())!.CertificateExpiresAt);
        Assert.Null(await NoticeAsync());

        _clock.Advance(expires - _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(29));
        Assert.Equal(expires, (await AlertsAsync())!.CertificateExpiresAt);
        Assert.True((await AlertsAsync())!.NeedsAttention);
        Assert.Null(await NoticeAsync());   // the till: only in the last 7 days

        _clock.Advance(TimeSpan.FromDays(23));
        Assert.Equal((0, expires), ((await NoticeAsync())!.OverdueCount, (await NoticeAsync())!.CertificateExpiresAtUtc));

        _clock.Advance(TimeSpan.FromDays(10));   // expired: still said
        Assert.NotNull((await NoticeAsync())!.CertificateExpiresAtUtc);
    }

    // ---- MISS-11d-4: reporting

    private static Task<Fiscal.Application.DTOs.FiscalSendSummary> RoundAsync(TestModuleDatabase<FiscalDbContext> db)
        => db.InScopeAsync(sp => sp.GetRequiredService<SendFiscalDocumentsCommandHandler>().ExecuteAsync(force: true));

    private static Task<List<Fiscal.Domain.Entities.FiscalDocument>> DocumentsAsync(TestModuleDatabase<FiscalDbContext> db)
        => db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Documents.AsNoTracking().OrderBy(d => d.Number).ThenBy(d => d.CreatedAt).ToListAsync());

    private async Task<TestModuleDatabase<FiscalDbContext>> OnboardedAsync()
    {
        var db = await DatabaseAsync();
        Assert.True((await OnboardAsync(db, Command())).IsSuccess);
        return db;
    }

    [Fact]
    public async Task A_sale_wakes_the_outbox_to_report_it_right_away()
    {
        await using var db = await OnboardedAsync();
        var signal = await db.InScopeAsync(sp => Task.FromResult(sp.GetRequiredService<FiscalOutboxSignal>()));
        while (await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None)) { }   // onboarding's own pokes

        await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale("S-1")));

        Assert.True(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));
        Assert.False(await signal.WaitAsync(TimeSpan.Zero, CancellationToken.None));   // one poke, however many sales
        Assert.True(new SaudiCountryPart().SendsRightAway);
        Assert.False(((IFiscalCountryPart)new Fiscal.Egypt.EgyptCountryPart()).SendsRightAway);   // Egypt: batches every 5 minutes
    }

    [Fact]
    public async Task Each_invoice_is_reported_with_the_tills_certificate_and_ZATCAs_answer_is_kept()
    {
        await using var db = await OnboardedAsync();
        foreach (var reference in new[] { "S-1", "S-2", "S-3" })
            await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale(reference)));
        _zatca.WarnReport["S-2"] = "[BR-KSA-98] The simplified invoice should be submitted within 24 hours of issuing the invoice";
        _zatca.RefuseReport["S-3"] = "[BR-KSA-31] the seller's identification is not valid";

        var round = await RoundAsync(db);

        Assert.Equal((2, 1, 0), (round.Accepted, round.Rejected, round.NotSent));
        var documents = await DocumentsAsync(db);
        Assert.Equal((FiscalDocumentState.Accepted, (string?)null), (documents[0].State, documents[0].StateReason));
        Assert.Equal(FiscalDocumentState.Accepted, documents[1].State);   // owner's decision: accepted, its warnings shown
        Assert.Equal("ZATCA's warnings: BR-KSA-98: [BR-KSA-98] The simplified invoice should be submitted within 24 hours of issuing the invoice", documents[1].StateReason);
        Assert.Equal(FiscalDocumentState.Rejected, documents[2].State);
        Assert.Contains("BR-KSA-31", documents[2].StateReason);
        Assert.Equal(System.Xml.Linq.XDocument.Parse(documents[0].Payload!).Root!.Element(ZatcaXml.Cbc + "UUID")!.Value, documents[0].AuthorityReference);

        var report = _zatca.Requests.First(r => r.Url.EndsWith("/invoices/reporting/single", StringComparison.Ordinal));
        Assert.Equal(("V2", "0", "en"), (report.Headers["Accept-Version"], report.Headers["Clearance-Status"], report.Headers["Accept-Language"]));
        Assert.StartsWith("https://gw-fatoora.zatca.gov.sa/e-invoicing/simulation/", report.Url);
        Assert.NotNull((await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync())).FirstSentAt);   // the environment is locked

        // nothing is reported twice
        Assert.False((await RoundAsync(db)).Ran && _zatca.Reported.Count > 2);
        Assert.Equal(["S-1", "S-2"], _zatca.Reported);
    }

    [Theory]
    [InlineData(401, SaudiCountryPart.CertificateRefused)]
    [InlineData(429, "Too many requests")]
    [InlineData(503, "ZATCA answered 503")]
    public async Task A_refused_sign_in_or_a_busy_ZATCA_leaves_the_invoices_waiting(int status, string reason)
    {
        await using var db = await OnboardedAsync();
        foreach (var reference in new[] { "S-1", "S-2" })
            await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale(reference)));
        _zatca.NextReportStatus = status;

        var round = await RoundAsync(db);

        Assert.Equal((0, 2), (round.Accepted, round.NotSent));   // the round stops at the first: the second is not tried
        Assert.Contains(reason, round.LastReason);
        Assert.Equal(1, _zatca.Requests.Count(r => r.Url.EndsWith("/invoices/reporting/single", StringComparison.Ordinal)));
        Assert.All(await DocumentsAsync(db), d => Assert.Equal(FiscalDocumentState.Waiting, d.State));
        Assert.Null((await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync())).FirstSentAt);

        Assert.Equal(2, (await RoundAsync(db)).Accepted);   // the next round reports them
    }

    [Fact]
    public async Task Without_the_till_key_on_this_computer_an_invoice_waits_unsigned()
    {
        await using var db = await OnboardedAsync();
        _keys.Delete();
        await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale("S-1")));

        var round = await RoundAsync(db);

        Assert.Equal((0, 1, SaudiCountryPart.NotSigned), (round.Accepted, round.NotSent, round.LastReason));
        Assert.Empty(_zatca.Reported);
    }

    [Fact]
    public async Task A_refused_invoice_is_corrected_as_a_new_invoice_at_the_end_of_the_chain()
    {
        await using var db = await OnboardedAsync();
        foreach (var reference in new[] { "S-1", "S-2" })
            await db.InScopeAsync(sp => sp.GetRequiredService<IFiscalRecorder>().RecordAsync(Sale(reference)));
        _zatca.RefuseReport["S-1"] = "[BR-KSA-31] the seller's identification is not valid";
        await RoundAsync(db);
        var refused = (await DocumentsAsync(db)).Single(d => d.Reference == "S-1");
        Assert.Equal(FiscalDocumentState.Rejected, refused.State);

        _zatca.RefuseReport.Clear();   // the shop fixed what ZATCA said
        var corrected = await db.InScopeAsync(sp => sp.GetRequiredService<CorrectFiscalDocumentCommandHandler>().HandleAsync(new CorrectFiscalDocumentCommand(refused.Id)));
        Assert.True(corrected.IsSuccess, corrected.IsFailure ? corrected.Error.Description : null);

        var documents = await DocumentsAsync(db);
        var copy = documents.Single(d => d.ReplacesDocumentId == refused.Id);
        var last = documents.Single(d => d.Reference == "S-2");
        Assert.Equal(FiscalDocumentState.Replaced, documents.Single(d => d.Id == refused.Id).State);
        Assert.Equal((last.Number + 1, last.Hash), (copy.Number, copy.PreviousHash));   // a new invoice at the END of the chain
        var xml = System.Xml.Linq.XDocument.Parse(copy.Payload!).Root!;
        Assert.Equal((copy.Number!.Value.ToString(), last.Hash), (xml.Elements(ZatcaXml.Cac + "AdditionalDocumentReference").Single(r => r.Element(ZatcaXml.Cbc + "ID")!.Value == "ICV").Element(ZatcaXml.Cbc + "UUID")!.Value,
            xml.Elements(ZatcaXml.Cac + "AdditionalDocumentReference").Single(r => r.Element(ZatcaXml.Cbc + "ID")!.Value == "PIH").Descendants(ZatcaXml.Cbc + "EmbeddedDocumentBinaryObject").Single().Value));
        Assert.NotEqual(refused.Hash, copy.Hash);
        Assert.Contains("<ext:UBLExtensions>", copy.Payload);   // signed at once

        Assert.Equal(1, (await RoundAsync(db)).Accepted);
        Assert.Equal(["S-2", "S-1"], _zatca.Reported);
        var till = await db.InScopeAsync(sp => sp.GetRequiredService<FiscalDbContext>().Tills.AsNoTracking().SingleAsync());
        Assert.Equal((copy.Number, copy.Hash), ((long?)till.Counter, till.LastDocumentHash));
    }
}
