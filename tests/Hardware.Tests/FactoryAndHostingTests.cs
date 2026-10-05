using Client.Hardware;
using Client.Hardware.Configuration;
using Client.Hardware.Devices;
using Client.Hardware.Printing;
using Client.Hardware.Scanner;
using Client.Host.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Hardware;

namespace Hardware.Tests;

public sealed class HardwareFactoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hardware-tests-" + Guid.NewGuid().ToString("N"));

    public HardwareFactoryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private string Device(string name = "printer.dev")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private static ReceiptDocument Receipt() => new(null, [], "R-1", DateTimeOffset.UtcNow, null, [new ReceiptLine("x", 1, 1, 1)], 1, null, []);

    // ---- nothing configured is the normal case ------------------------------------------------------------------

    [Fact]
    public async Task WithNothingConfigured_EveryDeviceIsNotConfigured_AndEveryOperationFailsCleanly()
    {
        var o = new HardwareOptions();
        var scanner = HardwareFactory.CreateScanner(o.Scanner);
        var receipt = HardwareFactory.CreateReceiptPrinter(o.ReceiptPrinter);
        var label = HardwareFactory.CreateLabelPrinter(o.LabelPrinter);
        var drawer = HardwareFactory.CreateCashDrawer(o.CashDrawer, o.ReceiptPrinter);
        var scale = HardwareFactory.CreateScale(o.Scale);

        foreach (var device in new IHardwareDevice[] { scanner, receipt, label, drawer, scale })
            Assert.Equal(DeviceState.NotConfigured, (await device.GetStatusAsync()).State);

        Assert.Equal(HardwareErrors.NotConfiguredCode, (await scanner.StartAsync()).Error.Code);
        Assert.Equal(HardwareErrors.NotConfiguredCode, (await receipt.PrintAsync(Receipt())).Error.Code);
        Assert.Equal(HardwareErrors.NotConfiguredCode, (await label.PrintAsync(new LabelDocument("x", "1", 1m))).Error.Code);
        Assert.Equal(HardwareErrors.NotConfiguredCode, (await drawer.OpenAsync()).Error.Code);
        Assert.Equal(HardwareErrors.NotConfiguredCode, (await scale.ReadAsync()).Error.Code);
        await scanner.StopAsync();
    }

    [Fact]
    public void TheDefaultConfiguration_IsEveryDeviceNone()
    {
        var o = new HardwareOptions();

        Assert.Equal(["None", "None", "None", "None", "None"],
            [o.Scanner.Type, o.ReceiptPrinter.Type, o.LabelPrinter.Type, o.CashDrawer.Type, o.Scale.Type]);
    }

    // ---- misconfiguration never stops the app: the device explains itself ------------------------------------------

    [Fact]
    public async Task AnUnknownType_IsAnUnavailableDeviceThatSaysWhy()
    {
        var receipt = HardwareFactory.CreateReceiptPrinter(new ReceiptPrinterOptions { Type = "Bluetoothy" });
        var scale = HardwareFactory.CreateScale(new ScaleOptions { Type = "Mettler" });

        var status = await receipt.GetStatusAsync();
        Assert.Equal(DeviceState.Unavailable, status.State);
        Assert.Contains("Bluetoothy", status.Message);
        var failed = await receipt.PrintAsync(Receipt());
        Assert.Equal(HardwareErrors.UnavailableCode, failed.Error.Code);
        Assert.Contains("Bluetoothy", failed.Error.Description);
        Assert.Equal(HardwareErrors.UnavailableCode, (await scale.ReadAsync()).Error.Code);
    }

    [Theory]
    [InlineData("EscPosTcp", null, null)]
    [InlineData("EscPosTcp", "", null)]
    [InlineData("EscPosFile", null, null)]
    [InlineData("EscPosFile", null, "   ")]
    public async Task AnIncompleteConnection_IsUnavailable_WithAReason(string type, string? host, string? path)
    {
        var printer = HardwareFactory.CreateReceiptPrinter(new ReceiptPrinterOptions { Type = type, Host = host, Path = path });

        var status = await printer.GetStatusAsync();

        Assert.Equal(DeviceState.Unavailable, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Message));
        Assert.Equal(HardwareErrors.UnavailableCode, (await printer.PrintAsync(Receipt())).Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task AnInvalidPort_IsUnavailable(int port)
    {
        var printer = HardwareFactory.CreateReceiptPrinter(new ReceiptPrinterOptions { Type = "EscPosTcp", Host = "127.0.0.1", Port = port });

        Assert.Equal(DeviceState.Unavailable, (await printer.GetStatusAsync()).State);
    }

    [Fact]
    public void TypesAreMatchedCaseInsensitively_AndTrimmed()
    {
        Assert.IsType<KeyboardWedgeBarcodeScanner>(HardwareFactory.CreateScanner(new ScannerOptions { Type = " keyboardwedge " }));
        Assert.IsType<NullBarcodeScanner>(HardwareFactory.CreateScanner(new ScannerOptions { Type = "NONE" }));
        Assert.IsType<NullBarcodeScanner>(HardwareFactory.CreateScanner(new ScannerOptions { Type = "" }));
    }

    // ---- real adapters, end to end through a device path ----------------------------------------------------------

    [Fact]
    public async Task AnEscPosReceiptPrinter_IsBuiltFromConfiguration_AndPrints()
    {
        var path = Device();
        var printer = HardwareFactory.CreateReceiptPrinter(new ReceiptPrinterOptions { Type = "EscPosFile", Path = path, CharactersPerLine = 32, CutPaper = true });

        Assert.IsType<EscPosReceiptPrinter>(printer);
        Assert.True((await printer.GetStatusAsync()).IsReady);
        Assert.True((await printer.PrintAsync(Receipt())).IsSuccess);
        Assert.Equal(new byte[] { 0x1B, 0x40 }, File.ReadAllBytes(path)[..2]);
    }

    [Fact]
    public async Task AZplLabelPrinter_IsBuiltFromConfiguration_AndPrints()
    {
        var path = Device("label.dev");
        var printer = HardwareFactory.CreateLabelPrinter(new LabelPrinterOptions { Type = "ZplFile", Path = path, WidthDots = 500, HeightDots = 300 });

        Assert.IsType<ZplLabelPrinter>(printer);
        Assert.True((await printer.PrintAsync(new LabelDocument("Juice", "SKU-9", 3.5m))).IsSuccess);
        var text = File.ReadAllText(path);
        Assert.Contains("^PW500^LL300", text);
        Assert.Contains("SKU-9", text);
    }

    [Fact]
    public async Task ADrawerCanKickThroughTheReceiptPrinter()
    {
        var path = Device();
        var drawer = HardwareFactory.CreateCashDrawer(
            new CashDrawerOptions { Type = "ViaReceiptPrinter", Pin = 1, OnTimeMilliseconds = 100, OffTimeMilliseconds = 400 },
            new ReceiptPrinterOptions { Type = "EscPosFile", Path = path });

        Assert.IsType<EscPosCashDrawer>(drawer);
        Assert.True((await drawer.OpenAsync()).IsSuccess);
        Assert.Equal(new byte[] { 0x1B, 0x70, 0x01, 50, 200 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ADrawerCanHaveItsOwnConnection()
    {
        var path = Device("drawer.dev");
        var drawer = HardwareFactory.CreateCashDrawer(new CashDrawerOptions { Type = "EscPosFile", Path = path }, new ReceiptPrinterOptions());

        Assert.True((await drawer.OpenAsync()).IsSuccess);
        Assert.Equal(0x70, File.ReadAllBytes(path)[1]);
    }

    [Fact]
    public async Task ViaReceiptPrinter_WithoutAReceiptPrinter_ExplainsItself()
    {
        var drawer = HardwareFactory.CreateCashDrawer(new CashDrawerOptions { Type = "ViaReceiptPrinter" }, new ReceiptPrinterOptions());

        var status = await drawer.GetStatusAsync();

        Assert.Equal(DeviceState.Unavailable, status.State);
        Assert.Contains("receipt printer", status.Message);
        Assert.Equal(HardwareErrors.UnavailableCode, (await drawer.OpenAsync()).Error.Code);
    }

    [Fact]
    public async Task APrinterThatIsOffline_FailsWithAClearError_NeverAnException()
    {
        var printer = HardwareFactory.CreateReceiptPrinter(new ReceiptPrinterOptions { Type = "EscPosFile", Path = Path.Combine(_dir, "gone.dev") });

        var result = await printer.PrintAsync(Receipt());

        Assert.Equal(HardwareErrors.UnavailableCode, result.Error.Code);
        Assert.Equal(DeviceState.Unavailable, (await printer.GetStatusAsync()).State);
    }

    [Fact]
    public async Task TheNullDevices_NeverThrow_AndTheScannerEventIsInert()
    {
        var scanner = new NullBarcodeScanner(DeviceStatus.NotConfigured());
        scanner.BarcodeScanned += (_, _) => { };
        scanner.BarcodeScanned -= (_, _) => { };

        Assert.True((await scanner.StartAsync()).IsFailure);
        await scanner.StopAsync();
    }
}

public sealed class HardwareHostingTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddClientHardware(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task WithoutAnyConfiguration_AllFiveAbstractionsResolve_AsNotConfigured()
    {
        using var sp = Build(Config());

        foreach (var device in new IHardwareDevice[]
                 {
                     sp.GetRequiredService<IBarcodeScanner>(), sp.GetRequiredService<IReceiptPrinter>(), sp.GetRequiredService<ILabelPrinter>(),
                     sp.GetRequiredService<ICashDrawer>(), sp.GetRequiredService<IScale>()
                 })
            Assert.Equal(DeviceState.NotConfigured, (await device.GetStatusAsync()).State);
    }

    [Fact]
    public void TheAdaptersAreChosenByConfiguration()
    {
        using var sp = Build(Config(
            ("Hardware:Scanner:Type", "KeyboardWedge"),
            ("Hardware:ReceiptPrinter:Type", "EscPosTcp"), ("Hardware:ReceiptPrinter:Host", "192.168.1.50"), ("Hardware:ReceiptPrinter:CharactersPerLine", "48"),
            ("Hardware:LabelPrinter:Type", "ZplTcp"), ("Hardware:LabelPrinter:Host", "192.168.1.51"),
            ("Hardware:CashDrawer:Type", "ViaReceiptPrinter")));

        Assert.IsType<KeyboardWedgeBarcodeScanner>(sp.GetRequiredService<IBarcodeScanner>());
        Assert.IsType<EscPosReceiptPrinter>(sp.GetRequiredService<IReceiptPrinter>());
        Assert.IsType<ZplLabelPrinter>(sp.GetRequiredService<ILabelPrinter>());
        Assert.IsType<EscPosCashDrawer>(sp.GetRequiredService<ICashDrawer>());
        Assert.IsType<NullScale>(sp.GetRequiredService<IScale>());
        Assert.Equal(48, sp.GetRequiredService<HardwareOptions>().ReceiptPrinter.CharactersPerLine);
        Assert.Equal(9100, sp.GetRequiredService<HardwareOptions>().ReceiptPrinter.Port);
    }

    [Fact]
    public void AReplacementIsAConfigurationChange_NotACodeChange()
    {
        using var network = Build(Config(("Hardware:ReceiptPrinter:Type", "EscPosTcp"), ("Hardware:ReceiptPrinter:Host", "10.0.0.5")));
        using var usb = Build(Config(("Hardware:ReceiptPrinter:Type", "EscPosFile"), ("Hardware:ReceiptPrinter:Path", "/dev/usb/lp0")));

        Assert.IsType<EscPosReceiptPrinter>(network.GetRequiredService<IReceiptPrinter>());
        Assert.IsType<EscPosReceiptPrinter>(usb.GetRequiredService<IReceiptPrinter>());
        Assert.NotSame(network.GetRequiredService<IReceiptPrinter>(), usb.GetRequiredService<IReceiptPrinter>());
    }

    [Fact]
    public void DevicesAreSingletons_NotGlobalStatics()
    {
        using var a = Build(Config(("Hardware:Scanner:Type", "KeyboardWedge")));
        using var b = Build(Config(("Hardware:Scanner:Type", "KeyboardWedge")));

        Assert.Same(a.GetRequiredService<IBarcodeScanner>(), a.GetRequiredService<IBarcodeScanner>());
        Assert.NotSame(a.GetRequiredService<IBarcodeScanner>(), b.GetRequiredService<IBarcodeScanner>());
    }

    [Fact]
    public void TheKeyboardSink_IsTheWedgeScanner_OrAHarmlessNoOp()
    {
        using var wedge = Build(Config(("Hardware:Scanner:Type", "KeyboardWedge")));
        using var none = Build(Config());

        Assert.Same(wedge.GetRequiredService<IBarcodeScanner>(), wedge.GetRequiredService<IKeyboardInputSink>());
        none.GetRequiredService<IKeyboardInputSink>().OnCharacter('x');          // no scanner: ignored, never throws
    }

    [Fact]
    public void ResolvingAnUnreachableNetworkPrinter_DoesNotTouchTheNetwork()
    {
        using var sp = Build(Config(("Hardware:ReceiptPrinter:Type", "EscPosTcp"), ("Hardware:ReceiptPrinter:Host", "203.0.113.9"), ("Hardware:ReceiptPrinter:TimeoutMilliseconds", "60000")));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var printer = sp.GetRequiredService<IReceiptPrinter>();
        sp.GetRequiredService<ICashDrawer>();

        Assert.NotNull(printer);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "Creating a device must not connect to it.");
    }

    [Fact]
    public void TheHostingModule_RegistersTheHardware_LikeEveryOtherClientComponent()
    {
        var services = new ServiceCollection();
        IHostingModule module = new HardwareHostingModule();
        var context = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = Config(("Hardware:Scale:Type", "None")) };

        module.RegisterServices(context, services);
        using var sp = services.BuildServiceProvider();

        Assert.IsType<NullScale>(sp.GetRequiredService<IScale>());
        Assert.NotNull(sp.GetRequiredService<IReceiptPrinter>());
    }
}
