namespace POS.Contracts.Models;

/// <summary>
/// A hardware problem that happened AFTER the business operation had already succeeded and been saved (for example the receipt
/// printer was offline after a completed sale). It informs the cashier; it never means the sale failed.
/// </summary>
/// <param name="Device">Which peripheral ("receipt printer", "cash drawer"...).</param>
/// <param name="ErrorCode">Hardware error code (Hardware.Unavailable, Hardware.Timeout, Hardware.Failed...).</param>
/// <param name="Message">A message ready to show to the cashier.</param>
public sealed record POSHardwareNotice(string Device, string ErrorCode, string Message);

/// <summary>What happened to one scanned barcode when it was added to the bound cart.</summary>
public sealed record POSScanOutcome(string Barcode, bool IsSuccess, Guid? ItemId, string? ErrorCode, string? ErrorMessage);

/// <summary>A weight read from the scale, ready for the cashier.</summary>
public sealed record POSWeightResult(
    bool IsSuccess, decimal Value, string Unit, bool IsStable, decimal Kilograms, string? ErrorCode, string? ErrorMessage)
{
    public static POSWeightResult Success(decimal value, string unit, bool isStable, decimal kilograms)
        => new(true, value, unit, isStable, kilograms, null, null);

    public static POSWeightResult Failure(string errorCode, string errorMessage)
        => new(false, 0m, string.Empty, false, 0m, errorCode, errorMessage);
}

/// <summary>The state of one peripheral: State is NotConfigured, Ready or Unavailable.</summary>
public sealed record POSDeviceStatusResult(string Device, string State, string? Message);
