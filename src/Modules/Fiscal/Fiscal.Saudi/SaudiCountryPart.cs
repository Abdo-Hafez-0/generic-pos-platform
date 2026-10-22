using System.Globalization;
using System.Text;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Saudi;

/// <summary>
/// MISS-11d: Saudi Arabia's part of the Fiscal module - ZATCA's e-invoicing phase 2, simplified invoices. 11d-1 builds the invoices and
/// credit notes (UBL XML, ICV and PIH, the hash); MISS-11d-2 stamps them with the till's key and ZATCA's certificate when the till has them
/// (the full QR) - before onboarding the invoice keeps the QR's first five fields and is stamped later (owner's decision); the receipt
/// carries the title and VAT number on top, the invoice's totals and labels in Arabic and English. Onboarding and reporting come with
/// 11d-3 and 11d-4; until then every invoice waits in the queue (owner's decision: keep selling).
/// </summary>
public sealed class SaudiCountryPart(SaudiInvoiceOptions? options = null, IZatcaKeyStore? keys = null, IFiscalHttpTransport? transport = null,
    TimeProvider? timeProvider = null) : IFiscalCountryPart
{
    private readonly SaudiInvoiceOptions _options = options ?? new SaudiInvoiceOptions();

    /// <summary>MISS-11d-3: Matjar's version in the certificate's serial ("1-Matjar|2-{version}|3-{serial}").</summary>
    internal static string Version => typeof(SaudiCountryPart).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    internal const string NoKeyStore = "This computer cannot keep a signing key in the Windows key store, so the till cannot be onboarded here.";

    public FiscalCountry Country => FiscalCountry.SaudiArabia;

    /// <summary>ZATCA takes a simplified invoice reported after 24 hours, with a warning (BR-KSA-98): nothing is held as Late.</summary>
    public bool HoldsLateDocuments => false;

    internal const string InvoiceTitle = "فاتورة ضريبية مبسطة";
    internal const string InvoiceTitleEnglish = "Simplified Tax Invoice";
    internal const string CreditNoteTitle = "إشعار دائن مبسط";
    internal const string CreditNoteTitleEnglish = "Simplified Credit Note";

    internal const string NotOnboarded = "This till is not onboarded with ZATCA yet.";

    public FiscalReceiptMark Mark(string hash, string qrPayload) => Mark(hash, qrPayload, FiscalDocumentKind.Sale);

    /// <summary>
    /// Owner's choices (drawn option A, 11d-2): the title in Arabic and English and the VAT number on top, nothing under the QR, the
    /// invoice's own totals (read from the invoice, or from the QR when it is not given) and every label in Arabic and English.
    /// </summary>
    public FiscalReceiptMark Mark(string hash, string qrPayload, FiscalDocumentKind kind, string? payload = null)
    {
        var tags = Tags(qrPayload);
        var vatNumber = tags.GetValueOrDefault((byte)2);
        decimal? Amount(string? text) => decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
        var (excluding, vat, including) = Totals(payload) is { } fromInvoice
            ? fromInvoice
            : (Amount(tags.GetValueOrDefault((byte)4)) - Amount(tags.GetValueOrDefault((byte)5)), Amount(tags.GetValueOrDefault((byte)5)), Amount(tags.GetValueOrDefault((byte)4)));
        return new FiscalReceiptMark(qrPayload, string.Empty, string.Empty)
        {
            TopLines =
            [
                kind == FiscalDocumentKind.Return ? CreditNoteTitle : InvoiceTitle,
                kind == FiscalDocumentKind.Return ? CreditNoteTitleEnglish : InvoiceTitleEnglish,
                .. vatNumber is null ? Array.Empty<string>() : ["الرقم الضريبي VAT No. " + vatNumber],
            ],
            TotalExcludingTax = excluding,
            TaxTotal = vat,
            TotalIncludingTax = including,
            BilingualLabels = true,
        };
    }

    /// <summary>The invoice's totals excluding VAT, the VAT and including VAT (BT-109, BT-110, BT-112), or null when it cannot be read.</summary>
    private static (decimal?, decimal?, decimal?)? Totals(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            var root = System.Xml.Linq.XDocument.Parse(payload).Root!;
            var money = root.Element(ZatcaXml.Cac + "LegalMonetaryTotal");
            decimal? Read(System.Xml.Linq.XElement? e) => e is not null && decimal.TryParse(e.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
            return (Read(money?.Element(ZatcaXml.Cbc + "TaxExclusiveAmount")), Read(root.Element(ZatcaXml.Cac + "TaxTotal")?.Element(ZatcaXml.Cbc + "TaxAmount")),
                Read(money?.Element(ZatcaXml.Cbc + "TaxInclusiveAmount")));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
    {
        var built = ZatcaInvoiceBuilder.Build(input, _options);
        if (built.IsFailure) return Result.Failure<FiscalBuiltDocument>(built.Error);

        // onboarded: stamped at once (the full QR is printed); otherwise it keeps the first five fields and is stamped later
        using var signer = Signer(input.Certificate);
        if (signer is null) return Result.Success(new FiscalBuiltDocument(built.Value.Xml, built.Value.Hash, built.Value.Qr));
        var stamped = ZatcaStamp.Apply(built.Value.Xml, signer, input.Request.IssuedAtUtc, _options);
        return Result.Success(new FiscalBuiltDocument(stamped.Xml, stamped.Hash, stamped.Qr));
    }

    /// <summary>
    /// MISS-11d-3 (owner's decision: the invoices made before onboarding are signed at its end): stamps an unstamped invoice with the till's key
    /// and <paramref name="certificate"/>; null when it is already stamped or the till has no key here.
    /// </summary>
    public Result<FiscalBuiltDocument>? Stamp(string payload, string certificate, DateTime nowUtc)
    {
        if (payload.Contains("UBLExtensions", StringComparison.Ordinal)) return null;
        using var signer = Signer(certificate);
        if (signer is null) return null;
        var stamped = ZatcaStamp.Apply(payload, signer, nowUtc, _options);
        return Result.Success(new FiscalBuiltDocument(stamped.Xml, stamped.Hash, stamped.Qr));
    }

    /// <summary>
    /// MISS-11d-3 (owner's choice A: one button, the steps ticked): a new key in the Windows key store, the CSR, the compliance CSID, the
    /// compliance checks and the production CSID.
    /// </summary>
    public async Task<Result<FiscalOnboarded>> OnboardAsync(FiscalOnboardingInput input, IProgress<FiscalOnboardingProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (keys is null) return Result.Failure<FiscalOnboarded>(Error.Conflict("Fiscal.Onboarding.NoKeyStore", NoKeyStore));
        progress?.Report(new(FiscalOnboardingStep.Key, false));
        using var key = keys.Create();
        progress?.Report(new(FiscalOnboardingStep.Key, true));

        var onboarded = await new ZatcaOnboarding(_options, new ZatcaApi(_options, transport), timeProvider)
            .RunAsync(key, input.Settings, Unit(input), input.DeviceSerial, Version, input.Environment, input.Otp, progress, cancellationToken);
        return onboarded.IsFailure ? Result.Failure<FiscalOnboarded>(onboarded.Error) : Result.Success(Onboarded(onboarded.Value));
    }

    /// <summary>MISS-11d-3: renewal - a new CSR for the same key with the portal's new OTP, signed in with the current production CSID.</summary>
    public async Task<Result<FiscalOnboarded>> RenewAsync(FiscalOnboardingInput input, FiscalOnboarded current, CancellationToken cancellationToken = default)
    {
        using var key = keys?.Open();
        if (key is null) return Result.Failure<FiscalOnboarded>(Error.Conflict("Fiscal.Onboarding.NoKey", "This till's signing key is not on this computer: onboard it again."));
        var token = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(current.Certificate));
        var renewed = await new ZatcaOnboarding(_options, new ZatcaApi(_options, transport), timeProvider)
            .RenewAsync(key, new ZatcaCsid(string.Empty, token, current.Secret), input.Settings, Unit(input), input.DeviceSerial, Version, input.Environment, input.Otp, cancellationToken);
        return renewed.IsFailure ? Result.Failure<FiscalOnboarded>(renewed.Error) : Result.Success(Onboarded(renewed.Value));
    }

    private static ZatcaUnit Unit(FiscalOnboardingInput input) => new(input.UnitName, input.Branch, input.Industry, input.Location);

    /// <summary>The certificate (DER, Base64 - the token is the Base64 of that text) and the secret.</summary>
    private static FiscalOnboarded Onboarded(ZatcaOnboarded onboarded)
        => new(System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(onboarded.Production.Token)), onboarded.CertificateExpiresAtUtc, onboarded.Production.Secret);

    /// <summary>The till's signer: its key from the Windows key store and ZATCA's certificate; null before onboarding or without the key here.</summary>
    private ZatcaSigner? Signer(string? certificate)
    {
        if (string.IsNullOrWhiteSpace(certificate) || keys?.Open() is not { } key) return null;
        return new ZatcaSigner(key, System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificate)));
    }

    public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents.Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent,
            Reason: input.Registration == FiscalTillRegistration.Registered ? NotReportingYet : NotOnboarded)).ToList());

    /// <summary>MISS-11d-3: onboarded, but reporting to ZATCA comes with the next step (11d-4): the invoices wait, signed.</summary>
    internal const string NotReportingYet = "Signed and waiting: Matjar does not report invoices to ZATCA yet.";

    public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(
            input.Documents.Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.StillWaiting)).ToList());

    /// <summary>The QR's text fields by tag (tags 1-5 are UTF-8 text); an unreadable QR gives none.</summary>
    private static Dictionary<byte, string> Tags(string qrPayload)
    {
        try
        {
            return ZatcaQr.Decode(qrPayload).Where(f => f.Tag <= 5).GroupBy(f => f.Tag).ToDictionary(g => g.Key, g => Encoding.UTF8.GetString(g.First().Value));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return [];
        }
    }
}
