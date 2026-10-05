using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Tests.Common.Hardware;

/// <summary>How a fake device behaves. Lets a test make any peripheral work, disappear, time out or blow up.</summary>
public enum FakeMode
{
    Works,
    NotConfigured,
    Unavailable,
    Timeout,

    /// <summary>The device throws an exception from every call (a misbehaving driver).</summary>
    Throws
}

internal static class FakeBehavior
{
    public static Result Apply(FakeMode mode, string device)
        => mode switch
        {
            FakeMode.Works => Result.Success(),
            FakeMode.NotConfigured => Result.Failure(HardwareErrors.NotConfigured(device)),
            FakeMode.Unavailable => Result.Failure(HardwareErrors.Unavailable(device, "disconnected")),
            FakeMode.Timeout => Result.Failure(HardwareErrors.Timeout(device)),
            _ => throw new InvalidOperationException($"{device} driver exploded")
        };

    public static DeviceStatus Status(FakeMode mode)
        => mode switch
        {
            FakeMode.Works => DeviceStatus.Ready(),
            FakeMode.NotConfigured => DeviceStatus.NotConfigured(),
            FakeMode.Throws => throw new InvalidOperationException("status check exploded"),
            _ => DeviceStatus.Unavailable("disconnected")
        };
}

public sealed class FakeBarcodeScanner : IBarcodeScanner
{
    public FakeMode Mode { get; set; } = FakeMode.Works;
    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public bool IsStarted { get; private set; }

    public event EventHandler<BarcodeScannedEventArgs>? BarcodeScanned;

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(FakeBehavior.Status(Mode));

    public Task<Result> StartAsync(CancellationToken cancellationToken = default)
    {
        StartCalls++;
        var result = FakeBehavior.Apply(Mode, "barcode scanner");
        IsStarted = result.IsSuccess;
        return Task.FromResult(result);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        StopCalls++;
        IsStarted = false;
        return Task.CompletedTask;
    }

    /// <summary>Simulates the user scanning a code (only while started, like a real scanner).</summary>
    public void Scan(string code)
    {
        if (!IsStarted) return;
        BarcodeScanned?.Invoke(this, new BarcodeScannedEventArgs(new BarcodeScan(code, DateTimeOffset.UtcNow)));
    }

    public int SubscriberCount => BarcodeScanned?.GetInvocationList().Length ?? 0;
}

public sealed class FakeReceiptPrinter : IReceiptPrinter
{
    public FakeMode Mode { get; set; } = FakeMode.Works;
    public List<ReceiptDocument> Printed { get; } = [];
    public int Attempts { get; private set; }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(FakeBehavior.Status(Mode));

    public Task<Result> PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default)
    {
        Attempts++;
        var result = FakeBehavior.Apply(Mode, "receipt printer");
        if (result.IsSuccess) Printed.Add(receipt);
        return Task.FromResult(result);
    }
}

public sealed class FakeLabelPrinter : ILabelPrinter
{
    public FakeMode Mode { get; set; } = FakeMode.Works;
    public List<LabelDocument> Printed { get; } = [];
    public int Attempts { get; private set; }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(FakeBehavior.Status(Mode));

    public Task<Result> PrintAsync(LabelDocument label, CancellationToken cancellationToken = default)
    {
        Attempts++;
        var result = FakeBehavior.Apply(Mode, "label printer");
        if (result.IsSuccess) Printed.Add(label);
        return Task.FromResult(result);
    }
}

public sealed class FakeCashDrawer : ICashDrawer
{
    public FakeMode Mode { get; set; } = FakeMode.Works;
    public int Opened { get; private set; }
    public int Attempts { get; private set; }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(FakeBehavior.Status(Mode));

    public Task<Result> OpenAsync(CancellationToken cancellationToken = default)
    {
        Attempts++;
        var result = FakeBehavior.Apply(Mode, "cash drawer");
        if (result.IsSuccess) Opened++;
        return Task.FromResult(result);
    }
}

public sealed class FakeScale : IScale
{
    public FakeMode Mode { get; set; } = FakeMode.Works;
    public WeightReading? Reading { get; set; } = new(0.750m, WeightUnit.Kilogram, true, DateTimeOffset.UtcNow);
    public int Reads { get; private set; }

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(FakeBehavior.Status(Mode));

    public Task<Result<WeightReading>> ReadAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        var result = FakeBehavior.Apply(Mode, "scale");
        return Task.FromResult(result.IsSuccess ? Result.Success(Reading!) : Result.Failure<WeightReading>(result.Error));
    }
}
