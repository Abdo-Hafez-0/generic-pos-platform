namespace Fiscal.Contracts.Models
{
    /// <summary>The tax authority whose electronic receipts the installation issues (MISS-11). None = electronic receipts are off.</summary>
    public enum FiscalCountryContract
    {
        None = 0,
        Egypt = 1,
        SaudiArabia = 2,
    }

    /// <summary>MISS-11a-2: what a fiscal document records.</summary>
    public enum FiscalDocumentKindContract
    {
        Sale = 1,
        Return = 2,
    }

    /// <summary>
    /// MISS-11a-2: one line of a sale or a return as the authority's document needs it. Prices include tax: <see cref="Total"/> is what is
    /// charged (or refunded) for the line after its discount, <see cref="Tax"/> the tax contained in it.
    /// </summary>
    public sealed record FiscalLineRequest(Guid ProductId, string ProductCode, string Name, decimal Quantity, decimal UnitPrice, decimal Discount,
        decimal Total, decimal TaxRate, decimal Tax);

    /// <summary>MISS-11a-2: how the money was paid (or given back): "Cash", "Card", "Other".</summary>
    public sealed record FiscalPaymentRequest(string Method, decimal Amount);

    /// <summary>
    /// MISS-11a-2: a sale or a return to record for the tax authority. <see cref="SourceId"/> is the sale's or the return's id (a second
    /// request for the same source returns the document already made); a return names its sale (<see cref="OriginalSourceId"/>,
    /// <see cref="OriginalReference"/>). Amounts are as charged, in the shop's currency.
    /// </summary>
    public sealed record FiscalDocumentRequest(
        FiscalDocumentKindContract Kind,
        Guid SourceId,
        string Reference,
        DateTime IssuedAtUtc,
        IReadOnlyList<FiscalLineRequest> Lines,
        decimal Total,
        decimal TaxTotal,
        IReadOnlyList<FiscalPaymentRequest> Payments,
        string? Cashier = null,
        string? BuyerName = null,
        Guid? OriginalSourceId = null,
        string? OriginalReference = null,
        string? BuyerNationalId = null);   // MISS-11c: Egypt - a person buyer's national ID, asked at the till above the threshold

    /// <summary>
    /// MISS-11c (owner's decision: asked at the till): a sale whose total is at or above <see cref="Threshold"/> must name its buyer - a name
    /// and a national ID of <see cref="NationalIdLength"/> digits (Egypt: 150,000 EGP for receipts v1.2, 14 digits).
    /// </summary>
    public sealed record FiscalBuyerRule(decimal Threshold, int NationalIdLength)
    {
        public bool Applies(decimal total) => total >= Threshold;

        /// <summary>The buyer given is enough: a name and a national ID of the right number of digits.</summary>
        public bool IsSatisfiedBy(string? name, string? nationalId)
            => !string.IsNullOrWhiteSpace(name) && nationalId is { } id && id.Trim().Length == NationalIdLength && id.Trim().All(char.IsAsciiDigit);
    }

    /// <summary>
    /// MISS-11a-2: the outcome. <see cref="IsSuccess"/> false only when the document could not be SAVED (the caller's transaction must not
    /// commit). Saved: <see cref="IsIssued"/> when it was built and chained (its number and QR); not issued when receipts are off, the
    /// country's part is not installed, or the part could not build it (<see cref="NotBuiltReason"/>; the sale stands, the document waits
    /// for attention).
    /// </summary>
    /// <summary>
    /// MISS-11a-3: what the till says about electronic receipts (owner's decision: a short line on the till): how many have waited longer than
    /// the till's limit (12 hours) to reach the authority, and since when.
    /// </summary>
    public sealed record FiscalTillNotice(int OverdueCount, DateTime OldestAtUtc, double LimitHours);

    /// <summary>
    /// MISS-11c-2: the tax authority's mark on a printed receipt: the QR code's text, a title and a caption printed under it (Egypt:
    /// "إيصال إلكتروني" and "UUID 68e656b2...f368f320").
    /// </summary>
    public sealed record FiscalReceiptMark(string QrPayload, string Title, string Caption);

    /// <summary>
    /// MISS-11c-3: one call to a tax authority's web service, as a country part asks for it (the part knows the authority's addresses and
    /// shapes; the transport only carries it). <see cref="Body"/> is sent as <see cref="ContentType"/> (form or JSON); null for a GET.
    /// </summary>
    public sealed record FiscalHttpRequest(string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body = null,
        string ContentType = "application/json");

    /// <summary>
    /// MISS-11c-3: the authority's answer. <see cref="StatusCode"/> 0 = no answer at all (no connection, a timeout) with the reason in
    /// <see cref="Body"/>; <see cref="RetryAfter"/> when the authority asked to wait.
    /// </summary>
    public sealed record FiscalHttpResponse(int StatusCode, string Body, TimeSpan? RetryAfter = null)
    {
        public bool Answered => StatusCode > 0;
        public bool IsSuccess => StatusCode is >= 200 and < 300;
    }

    public sealed record FiscalDocumentResult(bool IsSuccess, bool IsIssued, Guid? DocumentId, long? Number, string? QrPayload,
        string? NotBuiltReason, string? ErrorCode, string? ErrorMessage)
    {
        /// <summary>MISS-11c-2: what the printed receipt carries for the authority (issued documents only).</summary>
        public FiscalReceiptMark? Mark { get; init; }

        public static FiscalDocumentResult NothingToIssue() => new(true, false, null, null, null, null, null, null);

        public static FiscalDocumentResult Issued(Guid documentId, long number, string? qrPayload) => new(true, true, documentId, number, qrPayload, null, null, null);

        public static FiscalDocumentResult NotBuilt(Guid documentId, string reason) => new(true, false, documentId, null, null, reason, null, null);

        public static FiscalDocumentResult Failure(string errorCode, string errorMessage) => new(false, false, null, null, null, null, errorCode, errorMessage);
    }
}

namespace Fiscal.Contracts.Interfaces
{
    using Fiscal.Contracts.Models;

    /// <summary>
    /// MISS-11a: what other modules may know of the fiscal setup. Implemented by Fiscal.Infrastructure; the Fiscal module is optional, so
    /// consumers resolve it as optional.
    /// </summary>
    public interface IFiscalSetupReader
    {
        /// <summary>The chosen country (None when electronic receipts are off or nothing was set up).</summary>
        Task<FiscalCountryContract> GetCountryAsync(CancellationToken cancellationToken = default);

        /// <summary>MISS-11c: when a sale must name its buyer (null when receipts are off or the chosen country asks for no buyer).</summary>
        Task<FiscalBuyerRule?> GetBuyerRuleAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// MISS-11a-2: records the fiscal document of a sale or a return. Called by POS INSIDE its checkout / refund transaction (the document is
    /// saved with the sale, or neither is); runs within an operation the user was already authorized for. Optional module: resolved as optional.
    /// </summary>
    public interface IFiscalRecorder
    {
        Task<FiscalDocumentResult> RecordAsync(FiscalDocumentRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// MISS-11c-2: the mark of a sale's or a return's electronic receipt, to print it again (null when it has none: receipts off, made before
    /// they were on, or not built). Called inside an operation the user was authorized for (a reprint). Optional module: resolved as optional.
    /// </summary>
    public interface IFiscalReceiptMarks
    {
        Task<FiscalReceiptMark?> GetMarkAsync(FiscalDocumentKindContract kind, Guid sourceId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// MISS-11c-3: carries the country parts' calls to the tax authority over HTTPS. Implemented outside the business module (the client's
    /// HTTP transport project), so the Fiscal module itself never reaches the network and works offline; without it nothing is sent.
    /// Never throws for a network problem: the answer says so (status 0).
    /// </summary>
    public interface IFiscalHttpTransport
    {
        Task<FiscalHttpResponse> SendAsync(FiscalHttpRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>MISS-11a-3: the till's short warning (null when nothing has waited longer than the till's limit, or receipts are off).</summary>
    public interface IFiscalNotices
    {
        Task<FiscalTillNotice?> GetTillNoticeAsync(CancellationToken cancellationToken = default);
    }
}
