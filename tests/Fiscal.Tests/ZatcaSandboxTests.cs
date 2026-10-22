using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Client.Fiscal.Http;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Saudi;
using Xunit.Abstractions;

namespace Fiscal.Tests;

/// <summary>Runs only with GENERICPOS_ZATCA_SANDBOX=1: it calls ZATCA's public developer sandbox over the internet.</summary>
public sealed class ZatcaSandboxFactAttribute : FactAttribute
{
    public ZatcaSandboxFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GENERICPOS_ZATCA_SANDBOX") != "1")
            Skip = "Calls ZATCA's public developer sandbox: set GENERICPOS_ZATCA_SANDBOX=1 to run it (MISS-11d-3, owner's decision).";
    }
}

/// <summary>
/// MISS-11d-3 (owner's decision: tested against ZATCA's public developer sandbox - no account, its fixed OTP, ZATCA's test VAT number and a
/// made-up shop): a whole onboarding through the real HTTPS transport - the CSR, the compliance CSID, the compliance checks of Matjar's
/// stamped invoice, credit note and debit note (ZATCA's own validation of the XML, the hash, the signature and the QR), the production CSID
/// and its renewal.
/// </summary>
public sealed class ZatcaSandboxTests(ITestOutputHelper output)
{
    private const string Otp = "123345";   // the developer sandbox's fixed OTP

    internal static FiscalSettings TestShop()
    {
        var settings = FiscalSettings.Default();
        Assert.True(settings.Update(FiscalCountry.SaudiArabia, "399999999900003", null, null, "Matjar Test", SaudiInvoiceTests.Riyadh, false, "test", DateTime.UtcNow,
            "CRN", "1010010000").IsSuccess);
        return settings;
    }

    [ZatcaSandboxFact]
    public async Task A_till_is_onboarded_in_ZATCAs_sandbox_and_its_invoices_pass_the_compliance_checks()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var options = new SaudiInvoiceOptions { UseDeveloperSandbox = true };
        var api = new ZatcaApi(options, new HttpFiscalTransport(http));
        using var key = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
        var steps = new List<FiscalOnboardingProgress>();
        var progress = new SynchronousProgress(p => { steps.Add(p); output.WriteLine($"{p.Step} done={p.Done} {p.Failure} {p.Detail}"); });

        var onboarded = await new ZatcaOnboarding(options, api).RunAsync(key, TestShop(), new ZatcaUnit("Till 1", "Olaya branch", "Retail", "RRRD2929"),
            "MTJ-1A2B-3C4D-5E6F", "1.0", FiscalEnvironment.PreProduction, Otp, progress, CancellationToken.None);

        Assert.True(onboarded.IsSuccess, onboarded.IsFailure ? onboarded.Error.Description : null);
        Assert.Contains(steps, s => s is { Step: FiscalOnboardingStep.ComplianceChecks, Done: true });
        using var certificate = onboarded.Value.Production.Certificate();
        output.WriteLine($"certificate: {certificate.Subject} / {certificate.Issuer} until {certificate.NotAfter:u}");

        using var newKey = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
        var renewed = await new ZatcaOnboarding(options, api).RenewAsync(newKey, onboarded.Value.Production, TestShop(),
            new ZatcaUnit("Till 1", "Olaya branch", "Retail", "RRRD2929"), "MTJ-1A2B-3C4D-5E6F", "1.0", FiscalEnvironment.PreProduction, Otp, CancellationToken.None);
        output.WriteLine(renewed.IsSuccess ? "renewed" : "renewal: " + renewed.Error.Description);
        Assert.True(renewed.IsSuccess, renewed.IsFailure ? renewed.Error.Description : null);
    }

    [ZatcaSandboxFact]
    public async Task Matjars_edge_cases_pass_ZATCAs_checks_without_warnings()
    {
        // a halala of rounding, a zero-rated line with its exemption, a buyer's name, a weighed and discounted line, a credit note, several lines
        using var http = new HttpClient();
        var options = new SaudiInvoiceOptions { UseDeveloperSandbox = true };
        var api = new ZatcaApi(options, new HttpFiscalTransport(http));
        using var key = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
        var settings = ZatcaSandboxTests.TestShop();
        var csr = ZatcaCsr.Create(key, new ZatcaUnit("Till 1", "Olaya", "Retail", "RRRD2929"), settings.TaxNumber!, settings.SellerNameArabic!, "MTJ-1", "1.0", options.Sandbox.Template);
        var compliance = (await api.ComplianceCsidAsync(FiscalEnvironment.PreProduction, "123345", csr, default)).Value;
        using var cert = compliance.Certificate();

        var now = DateTime.UtcNow;
        Guid water = Guid.NewGuid(), dates = Guid.NewGuid(), med = Guid.NewGuid();
        var codes = new Dictionary<Guid, FiscalLineCodes>
        {
            [water] = new(null, null, "PCE", null, null, "S", null, null),
            [dates] = new(null, null, null, null, null, "S", null, null),
            [med] = new(null, null, null, null, null, "Z", "VATEX-SA-35", "Medicines and medical equipment"),
        };
        string? previous = null;
        var number = 0;
        async Task Check(string name, FiscalDocumentRequest request)
        {
            number++;
            var built = ZatcaInvoiceBuilder.Build(new FiscalBuildInput(request, settings, Guid.NewGuid(), "t", number, previous, codes), options);
            Assert.True(built.IsSuccess, built.IsFailure ? built.Error.Description : null);
            var stamped = ZatcaStamp.Apply(built.Value.Xml, key, cert, now, options);
            var r = await api.CheckAsync(FiscalEnvironment.PreProduction, compliance, stamped.Hash, built.Value.Uuid, stamped.Xml, default);
            output.WriteLine(r.IsSuccess ? $"{name}: {r.Value.Status} {string.Join(" | ", r.Value.Warnings)}" : $"{name}: {r.Error.Description}");
            Assert.True(r.IsSuccess, $"{name}: {(r.IsFailure ? r.Error.Description : null)}");
            Assert.Equal(("PASS", 0, 0), (r.Value.Status, r.Value.Errors.Count, r.Value.Warnings.Count));
            previous = stamped.Hash;
        }

        FiscalDocumentRequest Sale(string reference, string? buyer, params FiscalLineRequest[] lines)
            => new(FiscalDocumentKindContract.Sale, Guid.NewGuid(), reference, now, lines, lines.Sum(l => l.Total), lines.Sum(l => l.Tax),
                [new FiscalPaymentRequest("Card", lines.Sum(l => l.Total))], "owner", buyer);

        var rounding = Sale("PROBE-R", null, new FiscalLineRequest(water, "W", "Water", 2m, 1.15m, 0m, 2.30m, 0.15m, 0.30m), new FiscalLineRequest(dates, "D", "تمر", 1m, 2.03m, 0m, 2.03m, 0.15m, 0.26m));
        await Check("rounding -0.01", rounding);
        await Check("zero-rated + standard", Sale("PROBE-Z", null, new FiscalLineRequest(water, "W", "Water", 1m, 1.15m, 0m, 1.15m, 0.15m, 0.15m), new FiscalLineRequest(med, "M", "Medicine", 2m, 5m, 0m, 10m, 0m, 0m)));
        await Check("buyer name", Sale("PROBE-B", "عبدالله", new FiscalLineRequest(water, "W", "Water", 1m, 1.15m, 0m, 1.15m, 0.15m, 0.15m)));
        await Check("weighed, discounted", Sale("PROBE-W", null, new FiscalLineRequest(dates, "D", "Dates", 0.375m, 12m, 0.50m, 4.00m, 0.15m, 0.52m)));
        await Check("credit note", rounding with { Kind = FiscalDocumentKindContract.Return, SourceId = Guid.NewGuid(), Reference = "PROBE-RC", OriginalReference = "PROBE-R", Reason = "Wrong size" });
        await Check("many lines", Sale("PROBE-M", null, Enumerable.Range(1, 7).Select(i => new FiscalLineRequest(dates, "D", "Item " + i, 3m, 0.99m, 0m, 2.97m, 0.15m, 0.39m)).ToArray()));
    }

    private sealed class SynchronousProgress(Action<FiscalOnboardingProgress> report) : IProgress<FiscalOnboardingProgress>
    {
        public void Report(FiscalOnboardingProgress value) => report(value);
    }
}
