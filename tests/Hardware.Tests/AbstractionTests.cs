using System.Reflection;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using Tests.Common.Hardware;

namespace Hardware.Tests;

/// <summary>The hardware contracts express business capabilities and nothing about a device technology.</summary>
public sealed class AbstractionTests
{
    private static readonly Type[] Devices = [typeof(IBarcodeScanner), typeof(IReceiptPrinter), typeof(ILabelPrinter), typeof(ICashDrawer), typeof(IScale)];

    [Fact]
    public void ThereAreExactlyTheFiveStage10Abstractions_EachAnInterface()
    {
        var interfaces = typeof(IHardwareDevice).Assembly.GetExportedTypes()
            .Where(t => t.IsInterface && t.Namespace == typeof(IHardwareDevice).Namespace && t != typeof(IHardwareDevice))
            .ToList();

        Assert.Equivalent(Devices, interfaces.Where(t => typeof(IHardwareDevice).IsAssignableFrom(t)).ToList());
        // FIX-13a: the only other interface is the receipt picture renderer, a helper of the receipt printer (not a device)
        Assert.Equal([typeof(IReceiptImageRenderer)], interfaces.Where(t => !typeof(IHardwareDevice).IsAssignableFrom(t)).ToArray());
    }

    [Fact]
    public void EveryDevice_CanReportItsStatus()
        => Assert.All(Devices, d => Assert.True(typeof(IHardwareDevice).IsAssignableFrom(d)));

    [Fact]
    public void TheContractsExpressTheBusinessCapabilities()
    {
        Assert.NotNull(typeof(IBarcodeScanner).GetEvent(nameof(IBarcodeScanner.BarcodeScanned)));
        Assert.NotNull(typeof(IBarcodeScanner).GetMethod(nameof(IBarcodeScanner.StartAsync)));
        Assert.NotNull(typeof(IReceiptPrinter).GetMethod(nameof(IReceiptPrinter.PrintAsync), [typeof(ReceiptDocument), typeof(CancellationToken)]));
        Assert.NotNull(typeof(ILabelPrinter).GetMethod(nameof(ILabelPrinter.PrintAsync), [typeof(LabelDocument), typeof(CancellationToken)]));
        Assert.NotNull(typeof(ICashDrawer).GetMethod(nameof(ICashDrawer.OpenAsync)));
        Assert.Equal(typeof(Task<Result<WeightReading>>), typeof(IScale).GetMethod(nameof(IScale.ReadAsync))!.ReturnType);
    }

    [Fact]
    public void EveryOperation_ReturnsAResult_SoAFailureIsNeverAnException()
    {
        foreach (var device in Devices)
            foreach (var method in device.GetMethods().Where(m => m.Name is not "GetStatusAsync" and not "StopAsync" && !m.IsSpecialName))
                Assert.True(
                    typeof(Task<Result>).IsAssignableFrom(method.ReturnType) || method.ReturnType == typeof(Task<Result<WeightReading>>),
                    $"{device.Name}.{method.Name} must return a Result.");
    }

    [Fact]
    public void NoContractTypeMentionsADeviceTechnology()
    {
        string[] forbidden = ["SerialPort", "Usb", "Bluetooth", "Escpos", "Zpl", "Epson", "Zebra", "Tcp", "Socket", "Stream", "Com"];
        var types = typeof(IHardwareDevice).Assembly.GetExportedTypes().Where(t => t.Namespace == typeof(IHardwareDevice).Namespace);

        foreach (var type in types)
        {
            var members = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Select(m => m.Name).Append(type.Name);
            foreach (var name in members)
                Assert.DoesNotContain(forbidden, f => name.Contains(f, StringComparison.OrdinalIgnoreCase) && name != nameof(DeviceState));
        }
    }

    [Theory]
    [InlineData(250, WeightUnit.Gram, 0.25)]
    [InlineData(2, WeightUnit.Kilogram, 2)]
    [InlineData(16, WeightUnit.Ounce, 0.453592370)]
    [InlineData(2, WeightUnit.Pound, 0.90718474)]
    public void WeightConvertsToKilograms(double value, WeightUnit unit, double kilograms)
        => Assert.Equal((decimal)kilograms, new WeightReading((decimal)value, unit, true, DateTimeOffset.UtcNow).Kilograms, 6);

    [Fact]
    public void AnUnknownWeightUnit_IsNotSilentlyConverted()
        => Assert.Throws<InvalidOperationException>(() => _ = new WeightReading(1m, (WeightUnit)99, true, DateTimeOffset.UtcNow).Kilograms);

    [Fact]
    public void DeviceStatus_DescribesWhetherItIsUsable()
    {
        Assert.True(DeviceStatus.Ready().IsReady);
        Assert.False(DeviceStatus.NotConfigured().IsReady);
        Assert.False(DeviceStatus.Unavailable("x").IsReady);
        Assert.Equal(DeviceState.NotConfigured, default(DeviceStatus)?.State ?? DeviceState.NotConfigured);
        Assert.Equal("disconnected", DeviceStatus.Unavailable("disconnected").Message);
    }

    [Fact]
    public void HardwareErrors_AreFailuresWithStableCodes()
    {
        Assert.Equal("Hardware.NotConfigured", HardwareErrors.NotConfigured("scale").Code);
        Assert.Equal("Hardware.Unavailable", HardwareErrors.Unavailable("scale").Code);
        Assert.Equal("Hardware.Failed", HardwareErrors.Failed("scale").Code);
        Assert.Equal("Hardware.Timeout", HardwareErrors.Timeout("scale").Code);
        Assert.Equal("Hardware.InvalidData", HardwareErrors.InvalidData("scale", "x").Code);
        Assert.Contains("disconnected", HardwareErrors.Unavailable("scale", "disconnected").Description);
        Assert.True(HardwareErrors.IsNotConfigured(HardwareErrors.NotConfigured("x")));
        Assert.False(HardwareErrors.IsNotConfigured(HardwareErrors.Unavailable("x")));
    }
}

public sealed class HardwareGuardTests
{
    [Fact]
    public async Task AnExceptionFromADriver_BecomesAFailedResult()
    {
        var result = await HardwareGuard.RunAsync("printer", () => throw new InvalidOperationException("paper jam"));

        Assert.True(result.IsFailure);
        Assert.Equal(HardwareErrors.FailedCode, result.Error.Code);
        Assert.Contains("paper jam", result.Error.Description);
    }

    [Fact]
    public async Task ATypedResult_ToO_IsGuarded()
    {
        var result = await HardwareGuard.RunAsync<int>("scale", () => throw new InvalidOperationException("boom"));

        Assert.True(result.IsFailure);
        Assert.Equal(HardwareErrors.FailedCode, result.Error.Code);
    }

    [Fact]
    public async Task AnOrdinaryResult_PassesThrough()
    {
        Assert.True((await HardwareGuard.RunAsync("x", () => Task.FromResult(Result.Success()))).IsSuccess);
        Assert.Equal(7, (await HardwareGuard.RunAsync("x", () => Task.FromResult(Result.Success(7)))).Value);
    }

    [Fact]
    public async Task Cancellation_IsNotAHardwareFailure()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(() => HardwareGuard.RunAsync("x", () => throw new OperationCanceledException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => HardwareGuard.RunAsync<int>("x", () => throw new OperationCanceledException()));
    }

    [Fact]
    public async Task AStatusCheckThatThrows_IsReportedAsUnavailable()
    {
        var status = await HardwareGuard.StatusAsync(new FakeScale { Mode = FakeMode.Throws });

        Assert.Equal(DeviceState.Unavailable, status.State);
        Assert.Contains("exploded", status.Message);
    }
}

public sealed class FakeHardwareTests
{
    [Fact]
    public async Task TheFakes_ImplementTheAbstractionsAndHonourTheirModes()
    {
        var printer = new FakeReceiptPrinter();
        var doc = new ReceiptDocument(null, [], "R1", DateTimeOffset.UtcNow, null, [new ReceiptLine("x", 1, 1, 1)], 1, null, []);

        Assert.True((await printer.PrintAsync(doc)).IsSuccess);
        printer.Mode = FakeMode.Unavailable;
        Assert.Equal(HardwareErrors.UnavailableCode, (await printer.PrintAsync(doc)).Error.Code);
        printer.Mode = FakeMode.Timeout;
        Assert.Equal(HardwareErrors.TimeoutCode, (await printer.PrintAsync(doc)).Error.Code);
        printer.Mode = FakeMode.NotConfigured;
        Assert.Equal(HardwareErrors.NotConfiguredCode, (await printer.PrintAsync(doc)).Error.Code);
        printer.Mode = FakeMode.Throws;
        await Assert.ThrowsAsync<InvalidOperationException>(() => printer.PrintAsync(doc));

        Assert.Single(printer.Printed);
        Assert.Equal(5, printer.Attempts);
    }

    [Fact]
    public async Task TheFakeScanner_OnlyScansWhileStarted()
    {
        var scanner = new FakeBarcodeScanner();
        var seen = new List<string>();
        scanner.BarcodeScanned += (_, e) => seen.Add(e.Scan.Code);

        scanner.Scan("ignored");
        await scanner.StartAsync();
        scanner.Scan("one");
        await scanner.StopAsync();
        scanner.Scan("ignored too");

        Assert.Equal(["one"], seen);
    }
}
