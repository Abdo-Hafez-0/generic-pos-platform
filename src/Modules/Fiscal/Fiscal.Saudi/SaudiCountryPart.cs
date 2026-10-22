using System.Globalization;
using System.Text;
using Fiscal.Application.Abstractions;
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
public sealed class SaudiCountryPart(SaudiInvoiceOptions? options = null, IZatcaCredentials? credentials = null) : IFiscalCountryPart
{
    private readonly SaudiInvoiceOptions _options = options ?? new SaudiInvoiceOptions();

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
    /// invoice's own totals (from the QR's tags 4 and 5) and every label in Arabic and English.
    /// </summary>
    public FiscalReceiptMark Mark(string hash, string qrPayload, FiscalDocumentKind kind)
    {
        var tags = Tags(qrPayload);
        var vatNumber = tags.GetValueOrDefault((byte)2);
        decimal? Amount(byte tag) => decimal.TryParse(tags.GetValueOrDefault(tag), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
        var (including, vat) = (Amount(4), Amount(5));
        return new FiscalReceiptMark(qrPayload, string.Empty, string.Empty)
        {
            TopLines =
            [
                kind == FiscalDocumentKind.Return ? CreditNoteTitle : InvoiceTitle,
                kind == FiscalDocumentKind.Return ? CreditNoteTitleEnglish : InvoiceTitleEnglish,
                .. vatNumber is null ? Array.Empty<string>() : ["الرقم الضريبي VAT No. " + vatNumber],
            ],
            TotalIncludingTax = including,
            TaxTotal = vat,
            TotalExcludingTax = including - vat,
            BilingualLabels = true,
        };
    }

    public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
    {
        var built = ZatcaInvoiceBuilder.Build(input, _options);
        if (built.IsFailure) return Result.Failure<FiscalBuiltDocument>(built.Error);

        // onboarded: stamped at once (the full QR is printed); otherwise it keeps the first five fields and is stamped later
        using var signer = credentials?.Open();
        if (signer is null) return Result.Success(new FiscalBuiltDocument(built.Value.Xml, built.Value.Hash, built.Value.Qr));
        var stamped = ZatcaStamp.Apply(built.Value.Xml, signer, input.Request.IssuedAtUtc, _options);
        return Result.Success(new FiscalBuiltDocument(stamped.Xml, stamped.Hash, stamped.Qr));
    }

    public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(
            input.Documents.Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: NotOnboarded)).ToList());

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
