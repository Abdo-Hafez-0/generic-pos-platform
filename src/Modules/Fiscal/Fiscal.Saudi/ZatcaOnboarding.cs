using System.Security.Cryptography;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Saudi;

/// <summary>MISS-11d-3: what onboarding gave: the production CSID (the certificate in its token and the API's secret).</summary>
internal sealed record ZatcaOnboarded(ZatcaCsid Production, DateTime CertificateExpiresAtUtc);

/// <summary>
/// MISS-11d-3: onboarding a till with ZATCA (the Fatoora portal's OTP and the developer portal's API): the CSR signed with the till's key, the
/// compliance CSID, the compliance checks - a simplified invoice, a credit note and a debit note made by Matjar for the shop and stamped with
/// the compliance certificate - and the production CSID. Renewal sends a new CSR with a new OTP. Each step reports its progress; the first
/// failure stops with ZATCA's words.
/// </summary>
internal sealed class ZatcaOnboarding(SaudiInvoiceOptions options, ZatcaApi api, TimeProvider? timeProvider = null)
{
    public const string SampleItem = "Compliance check";
    private static readonly Guid SampleProduct = Guid.Parse("5a3d2c1b-0000-4000-8000-00000000c0de");

    public async Task<Result<ZatcaOnboarded>> RunAsync(ECDsa key, FiscalSettings settings, ZatcaUnit unit, string serial, string version, FiscalEnvironment environment,
        string otp, IProgress<FiscalOnboardingProgress>? progress, CancellationToken cancellationToken)
    {
        // the request (the key is made by the caller: step 1)
        progress?.Report(new(FiscalOnboardingStep.Request, false));
        var csr = ZatcaCsr.Create(key, unit, settings.TaxNumber!, settings.SellerNameArabic!, serial, version, api.Host(environment).Template);
        progress?.Report(new(FiscalOnboardingStep.Request, true));

        progress?.Report(new(FiscalOnboardingStep.ComplianceCertificate, false));
        var compliance = await api.ComplianceCsidAsync(environment, otp, csr, cancellationToken);
        if (compliance.IsFailure) return Fail(progress, FiscalOnboardingStep.ComplianceCertificate, compliance.Error);
        progress?.Report(new(FiscalOnboardingStep.ComplianceCertificate, true));

        progress?.Report(new(FiscalOnboardingStep.ComplianceChecks, false));
        var checks = await ChecksAsync(key, settings, environment, compliance.Value, cancellationToken);
        if (checks.IsFailure) return Fail(progress, FiscalOnboardingStep.ComplianceChecks, checks.Error);
        progress?.Report(new(FiscalOnboardingStep.ComplianceChecks, true, Detail: checks.Value));

        progress?.Report(new(FiscalOnboardingStep.ProductionCertificate, false));
        var production = await api.ProductionCsidAsync(environment, compliance.Value, cancellationToken);
        if (production.IsFailure) return Fail(progress, FiscalOnboardingStep.ProductionCertificate, production.Error);
        using var certificate = production.Value.Certificate();
        progress?.Report(new(FiscalOnboardingStep.ProductionCertificate, true));
        return Result.Success(new ZatcaOnboarded(production.Value, certificate.NotAfter.ToUniversalTime()));
    }

    /// <summary>Renewal: a new CSR (with the new key) and the portal's new OTP give a new production CSID.</summary>
    public async Task<Result<ZatcaOnboarded>> RenewAsync(ECDsa key, ZatcaCsid current, FiscalSettings settings, ZatcaUnit unit, string serial, string version,
        FiscalEnvironment environment, string otp, CancellationToken cancellationToken)
    {
        var csr = ZatcaCsr.Create(key, unit, settings.TaxNumber!, settings.SellerNameArabic!, serial, version, api.Host(environment).Template);
        var renewed = await api.RenewAsync(environment, current, otp, csr, cancellationToken);
        if (renewed.IsFailure) return Result.Failure<ZatcaOnboarded>(renewed.Error);
        using var certificate = renewed.Value.Certificate();
        return Result.Success(new ZatcaOnboarded(renewed.Value, certificate.NotAfter.ToUniversalTime()));
    }

    /// <summary>
    /// The compliance checks for a unit that issues simplified invoices: an invoice, a credit note and a debit note (a chain of their own),
    /// stamped with the compliance certificate. Returns "3 passed" (or with ZATCA's warnings).
    /// </summary>
    private async Task<Result<string>> ChecksAsync(ECDsa key, FiscalSettings settings, FiscalEnvironment environment, ZatcaCsid compliance, CancellationToken cancellationToken)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var codes = new Dictionary<Guid, FiscalLineCodes> { [SampleProduct] = new(null, null, "PCE", null, null, "S", null, null) };
        var sale = new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, Guid.NewGuid(), "CHECK-" + now.ToString("yyyyMMddHHmmss"), now,
            [new FiscalLineRequest(SampleProduct, "CHECK", SampleItem, 1m, 1.15m, 0m, 1.15m, 0.15m, 0.15m)], 1.15m, 0.15m, [new FiscalPaymentRequest("Cash", 1.15m)]);
        var note = sale with { Kind = FiscalDocumentKindContract.Return, SourceId = Guid.NewGuid(), OriginalReference = sale.Reference, Reason = SampleItem };

        string? previous = null;
        var warnings = new List<string>();
        var tillId = Guid.NewGuid();
        var samples = new (FiscalDocumentRequest Request, bool Debit, string Name)[]
        {
            (sale, false, "the simplified invoice"),
            (note with { Reference = sale.Reference + "-C" }, false, "the simplified credit note"),
            (note with { Reference = sale.Reference + "-D", SourceId = Guid.NewGuid() }, true, "the simplified debit note"),
        };
        using var certificate = compliance.Certificate();
        for (var i = 0; i < samples.Length; i++)
        {
            var (request, debit, name) = samples[i];
            var built = ZatcaInvoiceBuilder.Build(new FiscalBuildInput(request, settings, tillId, "compliance", i + 1, previous, codes), options, debit);
            if (built.IsFailure) return Result.Failure<string>(built.Error);
            var stamped = ZatcaStamp.Apply(built.Value.Xml, key, certificate, now, options);
            var checkedOne = await api.CheckAsync(environment, compliance, stamped.Hash, built.Value.Uuid, stamped.Xml, cancellationToken);
            if (checkedOne.IsFailure) return Result.Failure<string>(Error.Failure(checkedOne.Error.Code, $"{Capital(name)}: {checkedOne.Error.Description}"));
            if (!checkedOne.Value.Passed)
                return Result.Failure<string>(Error.Failure("Fiscal.Zatca.CheckFailed", $"ZATCA refused {name}: " + string.Join("; ", checkedOne.Value.Errors.Take(5))));
            warnings.AddRange(checkedOne.Value.Warnings);
            previous = stamped.Hash;
        }

        return Result.Success(warnings.Count == 0 ? "3 passed" : "3 passed with warnings: " + string.Join("; ", warnings.Distinct().Take(3)));
    }

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    private static Result<ZatcaOnboarded> Fail(IProgress<FiscalOnboardingProgress>? progress, FiscalOnboardingStep step, Error error)
    {
        progress?.Report(new(step, false, error.Description));
        return Result.Failure<ZatcaOnboarded>(error);
    }
}
