using Platform.Core.Results;

namespace Platform.Application.Abstractions.Hardware;

/// <summary>Whether a peripheral can be used right now.</summary>
public enum DeviceState
{
    /// <summary>No device of this kind is configured. This is normal: hardware is optional.</summary>
    NotConfigured = 0,

    /// <summary>The device is configured and responded to the last check.</summary>
    Ready = 1,

    /// <summary>The device is configured but cannot be reached (disconnected, offline, wrong address...).</summary>
    Unavailable = 2
}

/// <summary>A device's current state and, when it is not ready, a human-readable reason.</summary>
public sealed record DeviceStatus(DeviceState State, string? Message = null)
{
    public bool IsReady => State == DeviceState.Ready;

    public static DeviceStatus NotConfigured(string? message = null) => new(DeviceState.NotConfigured, message ?? "No device is configured.");

    public static DeviceStatus Ready() => new(DeviceState.Ready);

    public static DeviceStatus Unavailable(string message) => new(DeviceState.Unavailable, message);
}

/// <summary>Common to every peripheral abstraction.</summary>
public interface IHardwareDevice
{
    /// <summary>Checks the device. Never throws: a device problem is reported as a status.</summary>
    Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The error vocabulary of hardware operations. A hardware problem is a normal, reportable outcome (a failed <see cref="Result"/>),
/// never a business-data problem: callers keep their already-persisted business state and surface the message.
/// </summary>
public static class HardwareErrors
{
    public const string NotConfiguredCode = "Hardware.NotConfigured";
    public const string UnavailableCode = "Hardware.Unavailable";
    public const string FailedCode = "Hardware.Failed";
    public const string TimeoutCode = "Hardware.Timeout";
    public const string InvalidDataCode = "Hardware.InvalidData";

    public static Error NotConfigured(string device) => Error.Failure(NotConfiguredCode, $"No {device} is configured.");

    public static Error Unavailable(string device, string? detail = null)
        => Error.Failure(UnavailableCode, detail is null ? $"The {device} is not available." : $"The {device} is not available: {detail}");

    public static Error Failed(string device, string? detail = null)
        => Error.Failure(FailedCode, detail is null ? $"The {device} reported a failure." : $"The {device} reported a failure: {detail}");

    public static Error Timeout(string device) => Error.Failure(TimeoutCode, $"The {device} did not respond in time.");

    public static Error InvalidData(string device, string detail) => Error.Validation(InvalidDataCode, $"The {device} returned unusable data: {detail}");

    /// <summary>True when the error only says "there is no such device" (the caller should usually skip silently).</summary>
    public static bool IsNotConfigured(Error error) => error.Code == NotConfiguredCode;
}

/// <summary>
/// Runs a hardware call so that NOTHING a device does can escape as an exception into business code. Adapters are expected
/// not to throw; this is the defence in depth for callers (and for third-party adapters).
/// Cancellation is not a hardware failure and is propagated.
/// </summary>
public static class HardwareGuard
{
    public static async Task<Result> RunAsync(string device, Func<Task<Result>> call)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure(HardwareErrors.Failed(device, ex.Message));
        }
    }

    public static async Task<Result<T>> RunAsync<T>(string device, Func<Task<Result<T>>> call)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Result.Failure<T>(HardwareErrors.Failed(device, ex.Message));
        }
    }

    public static async Task<DeviceStatus> StatusAsync(IHardwareDevice device, CancellationToken cancellationToken = default)
    {
        try
        {
            return await device.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return DeviceStatus.Unavailable(ex.Message);
        }
    }
}
