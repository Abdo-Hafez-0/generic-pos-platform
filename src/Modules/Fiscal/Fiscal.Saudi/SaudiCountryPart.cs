using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Saudi;

/// <summary>
/// MISS-11d: Saudi Arabia's part of the Fiscal module - ZATCA's e-invoicing phase 2, simplified invoices. 11d-1 builds the invoices and
/// credit notes (UBL XML, ICV and PIH, the hash, the QR's first five fields); the stamp, onboarding and reporting come with 11d-2..11d-4.
/// Until the till is onboarded nothing is reported: every invoice waits in the queue (owner's decision: keep selling, sign later).
/// </summary>
public sealed class SaudiCountryPart(SaudiInvoiceOptions? options = null) : IFiscalCountryPart
{
    private readonly SaudiInvoiceOptions _options = options ?? new SaudiInvoiceOptions();

    public FiscalCountry Country => FiscalCountry.SaudiArabia;

    /// <summary>ZATCA takes a simplified invoice reported after 24 hours, with a warning (BR-KSA-98): nothing is held as Late.</summary>
    public bool HoldsLateDocuments => false;

    /// <summary>"فاتورة ضريبية مبسطة" and the short hash under the QR code (the receipt's layout comes with 11d-2).</summary>
    public FiscalReceiptMark Mark(string hash, string qrPayload) => new(qrPayload, ReceiptTitle, FiscalMarks.Short(hash));

    internal const string ReceiptTitle = "فاتورة ضريبية مبسطة";

    internal const string NotOnboarded = "This till is not onboarded with ZATCA yet.";

    public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
    {
        var built = ZatcaInvoiceBuilder.Build(input, _options);
        return built.IsFailure
            ? Result.Failure<FiscalBuiltDocument>(built.Error)
            : Result.Success(new FiscalBuiltDocument(built.Value.Xml, built.Value.Hash, built.Value.Qr));
    }

    public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(
            input.Documents.Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: NotOnboarded)).ToList());

    public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(
            input.Documents.Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.StillWaiting)).ToList());
}
