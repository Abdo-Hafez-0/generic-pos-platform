using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Egypt;

/// <summary>
/// MISS-11c: Egypt's part of the Fiscal module - the Egyptian Tax Authority's B2C e-receipt. 11c-1 builds the receipts (JSON, UUID chain,
/// QR); handing them to the ETA needs the till's registration (11c-3), so until then every document waits in the queue (owner's decision:
/// keep selling).
/// </summary>
public sealed class EgyptCountryPart(EgyptReceiptOptions? options = null) : IFiscalCountryPart
{
    private readonly EgyptReceiptOptions _options = options ?? new EgyptReceiptOptions();

    public FiscalCountry Country => FiscalCountry.Egypt;

    /// <summary>Owner's decision: the till asks for a person's name and national ID at or above the threshold.</summary>
    public FiscalBuyerRule? BuyerRule => new(_options.BuyerIdThreshold, EgyptReceiptBuilder.NationalIdLength);

    public Result<FiscalBuiltDocument> Build(FiscalBuildInput input)
    {
        var built = EgyptReceiptBuilder.Build(input, _options);
        return built.IsFailure
            ? Result.Failure<FiscalBuiltDocument>(built.Error)
            : Result.Success(new FiscalBuiltDocument(built.Value.Json, built.Value.Uuid, built.Value.Qr));
    }

    public Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents
            .Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: NotRegistered)).ToList());

    public Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FiscalSendOutcome>>(input.Documents
            .Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.StillWaiting)).ToList());

    internal const string NotRegistered = "This till is not registered with the Egyptian Tax Authority yet.";
}
