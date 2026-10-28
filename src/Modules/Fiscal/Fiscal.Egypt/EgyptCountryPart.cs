using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Egypt;

/// <summary>
/// MISS-11c: Egypt's part of the Fiscal module - the Egyptian Tax Authority's B2C e-receipt. It builds the receipts (JSON, UUID chain, QR;
/// 11c-1, 11c-2) and - MISS-11c-3 - signs the registered till in and hands them over in batches, then reads each submission back, through the
/// client's HTTP transport (without one, or before the till is registered, every document waits in the queue: owner's decision, keep selling).
/// </summary>
public sealed class EgyptCountryPart(EgyptReceiptOptions? options = null, IFiscalHttpTransport? transport = null, TimeProvider? timeProvider = null)
    : IFiscalCountryPart
{
    private readonly EgyptReceiptOptions _options = options ?? new EgyptReceiptOptions();
    private readonly EtaService _eta = new(options ?? new EgyptReceiptOptions(), transport, timeProvider);

    public FiscalCountry Country => FiscalCountry.Egypt;

    /// <summary>Owner's decision: the till asks for a person's name and national ID at or above the threshold.</summary>
    public FiscalBuyerRule? BuyerRule => new(_options.BuyerIdThreshold, EgyptReceiptBuilder.NationalIdLength);

    /// <summary>MISS-11e: the ETA takes a return up to 540 days after its sale (owner's decision: the till warns past it).</summary>
    public int? ReturnDaysAllowed => EgyptReceiptBuilder.ReturnDaysAllowed;

    /// <summary>MISS-11e: the Return Receipt Without Reference names its own buyer (owner's decision: asked at the till).</summary>
    public bool ReturnWithoutOriginalNamesBuyer => true;

    /// <summary>Owner's choice (drawn option A): "إيصال إلكتروني" and the short UUID under the QR code.</summary>
    public FiscalReceiptMark Mark(string hash, string qrPayload) => new(qrPayload, ReceiptTitle, "UUID " + FiscalMarks.Short(hash));

    internal const string ReceiptTitle = "إيصال إلكتروني";

    public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
    {
        var built = EgyptReceiptBuilder.Build(input, _options);
        return built.IsFailure
            ? Result.Failure<FiscalBuiltDocument>(built.Error)
            : Result.Success(new FiscalBuiltDocument(built.Value.Json, built.Value.Uuid, built.Value.Qr));
    }

    public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => _eta.SubmitAsync(input, cancellationToken);

    public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => _eta.CheckAsync(input, cancellationToken);

    public Task<Result> TestSignInAsync(FiscalTillConnection connection, CancellationToken cancellationToken = default)
        => _eta.TestSignInAsync(connection, cancellationToken);

    internal const string NotRegistered = "This till is not registered with the Egyptian Tax Authority yet.";
}
