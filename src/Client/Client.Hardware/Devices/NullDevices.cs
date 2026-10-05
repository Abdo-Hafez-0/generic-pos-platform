using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Devices;

// Placeholders used when a device is not configured (the normal case) or its configuration is unusable. Every operation fails with
// a clear hardware error and nothing throws, so the POS and every caller keep working without the peripheral.

internal static class NullDevice
{
    public static Error Error(string device, DeviceStatus status)
        => status.State == DeviceState.NotConfigured
            ? HardwareErrors.NotConfigured(device)
            : HardwareErrors.Unavailable(device, status.Message);
}

public sealed class NullBarcodeScanner(DeviceStatus status) : IBarcodeScanner
{
    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned { add { } remove { } }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

    public Task<Result> StartAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NullDevice.Error("barcode scanner", status)));

    public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class NullReceiptPrinter(DeviceStatus status) : IReceiptPrinter
{
    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

    public Task<Result> PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NullDevice.Error("receipt printer", status)));
}

public sealed class NullLabelPrinter(DeviceStatus status) : ILabelPrinter
{
    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

    public Task<Result> PrintAsync(LabelDocument label, CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NullDevice.Error("label printer", status)));
}

public sealed class NullCashDrawer(DeviceStatus status) : ICashDrawer
{
    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

    public Task<Result> OpenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure(NullDevice.Error("cash drawer", status)));
}

public sealed class NullScale(DeviceStatus status) : IScale
{
    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(status);

    public Task<Result<WeightReading>> ReadAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(Result.Failure<WeightReading>(NullDevice.Error("scale", status)));
}
