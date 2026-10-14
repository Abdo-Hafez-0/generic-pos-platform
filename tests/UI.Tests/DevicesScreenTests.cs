using System.Text;
using Client.Desktop.Resources;
using Client.Desktop.Screens;
using Client.Desktop.Screens.Devices;
using Client.Desktop.Shell;
using Client.Hardware;
using Client.Hardware.Configuration;
using Client.Hardware.Setup;
using Client.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Hardware;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using POS.Application.Devices;
using POS.Application.Security;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Tests.Common.Security;

namespace UI.Tests;

/// <summary>
/// MISS-07b: Administration > Devices through the POS module's handlers and the real device adapters (a file stands in for the printer, as
/// a device path set in the configuration would): the saved choices are shown, tests use what is entered, saving asks for a restart, the
/// scanner test shows what the scanner reads while the screen is open, and nothing works without devices.manage.
/// </summary>
public sealed class DevicesScreenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genericpos-ui-devices-" + Guid.NewGuid().ToString("N"));
    private readonly List<ServiceProvider> _providers = [];
    private readonly Restarter _restarter = new();
    private readonly ScannerInput _scanner = new();

    public DevicesScreenTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class Restarter : IApplicationRestarter
    {
        public int Restarts { get; private set; }

        public void Restart() => Restarts++;
    }

    /// <summary>The POS scanner input with no cart bound: every scan comes back as "open a cart first", with its code.</summary>
    private sealed class ScannerInput : IPOSBarcodeInput
    {
        public bool Started { get; private set; }

        public event EventHandler<POSScanOutcome>? ScanProcessed;

        public void BindCart(Guid? cartId) { }

        public Task<POSOperationResult> StartAsync(CancellationToken cancellationToken = default)
        {
            Started = true;
            return Task.FromResult(POSOperationResult.Success());
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Started = false;
            return Task.CompletedTask;
        }

        public void Scan(string code)
        {
            if (Started) ScanProcessed?.Invoke(this, new POSScanOutcome(code, false, null, "POS.Scan.NoActiveCart", "Open a cart before scanning."));
        }
    }

    private string Receipt => Path.Combine(_root, "receipt.bin");

    private LocalSettingsFile Local => new(Path.Combine(_root, LocalSettingsFile.FileName));

    private DevicesViewModel Screen(params string[] allowed)
    {
        File.WriteAllBytes(Receipt, []);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hardware:ReceiptPrinter:Type"] = "EscPosFile",   // set by the installer; the screen keeps what it does not offer
            ["Hardware:ReceiptPrinter:Path"] = Receipt,
            ["Hardware:CashDrawer:Type"] = "ViaReceiptPrinter",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        new HardwareHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration }, services);
        services.AddSingleton(Local);
        services.AddSingleton<IDeviceSetup>(sp => new DeviceSetupAdapter(sp.GetRequiredService<HardwareOptions>(), Local, () => ["TM-T20 Receipt"]));
        services.AddSingleton<IAuthorizationService>(new ScriptedAuthorizationService(allowed));
        services.AddTransient<GetDeviceSetupQueryHandler>();
        services.AddTransient<CheckDevicesCommandHandler>();
        services.AddTransient<TestDeviceCommandHandler>();
        services.AddTransient<SaveDeviceSettingsCommandHandler>();
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _providers.Add(provider);
        return new DevicesViewModel(new UiActionRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance), _restarter, _scanner);
    }

    private static async Task Run(DevicesViewModel vm, System.Windows.Input.ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command should be available");
        command.Execute(null);
        for (var i = 0; i < 500 && vm.IsBusy; i++) await Task.Delay(10);
    }

    [Fact]
    public void The_screen_opens_with_devices_manage_which_no_license_state_locks()
    {
        var screen = Assert.Single(new DesktopScreens().GetScreens(), s => s.Id == "devices.devices");

        Assert.Equal(POSCapabilities.ManageDevices, screen.RequiredCapability);
        Assert.Equal(ScreenGroups.Administration, screen.Group);
        Assert.Equal(DevicesText.ScreenTitle, screen.Title());
    }

    [Fact]
    public async Task The_shop_tests_the_printer_and_drawer_as_entered_then_saves_a_windows_printer_and_restarts()
    {
        var vm = Screen(POSCapabilities.ManageDevices);
        await vm.OnNavigatedToAsync();

        Assert.Equal(POSPrinterLink.Configured, vm.ReceiptKind!.Value);   // shown as set in the configuration, not lost
        Assert.Equal(DevicesText.KindConfigured, vm.ReceiptKind.Text);
        Assert.True(vm.DrawerUsed);
        Assert.Equal(DevicesText.StateReady, vm.ReceiptState);
        Assert.Equal(DevicesText.StateNotUsed, vm.LabelState);
        Assert.Equal(["TM-T20 Receipt"], vm.WindowsPrinters);

        await Run(vm, vm.TestReceiptCommand);
        Assert.Equal(DevicesText.TestReceiptDone, vm.StatusMessage);
        Assert.Contains("Test receipt", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(Receipt)));
        await Run(vm, vm.OpenDrawerCommand);
        Assert.Equal(DevicesText.DrawerOpened, vm.StatusMessage);

        // a network printer that is not there: the check says why
        vm.ReceiptKind = vm.PrinterKinds.Single(k => k.Value == POSPrinterLink.Network);
        vm.ReceiptHost = "127.0.0.1";
        vm.ReceiptPort = "9";
        await Run(vm, vm.CheckCommand);
        Assert.Contains("127.0.0.1:9", vm.ReceiptState);

        // the shop moves to the USB printer installed in Windows
        vm.ReceiptKind = vm.PrinterKinds.Single(k => k.Value == POSPrinterLink.WindowsPrinter);
        Assert.True(vm.IsReceiptWindows);
        vm.ReceiptPrinterName = null;
        await Run(vm, vm.SaveCommand);
        Assert.Equal("Choose the Windows printer for the receipt printer.", vm.ErrorMessage);
        Assert.False(vm.RestartNeeded);

        vm.ReceiptPrinterName = "TM-T20 Receipt";
        vm.CharactersPerLine = 48;
        await Run(vm, vm.SaveCommand);
        Assert.Equal(DevicesText.Saved, vm.StatusMessage);
        Assert.True(vm.RestartNeeded);
        var written = Local.ReadSection<HardwareOptions>(HardwareOptions.SectionName)!;
        Assert.Equal(("EscPosWindows", "TM-T20 Receipt", 48), (written.ReceiptPrinter.Type, written.ReceiptPrinter.PrinterName, written.ReceiptPrinter.CharactersPerLine));

        await Run(vm, vm.RestartNowCommand);
        Assert.Equal(1, _restarter.Restarts);
    }

    [Fact]
    public async Task The_scanner_test_shows_what_the_scanner_reads_while_the_screen_is_open()
    {
        var vm = Screen(POSCapabilities.ManageDevices);
        await vm.OnNavigatedToAsync();
        Assert.Equal(DevicesText.ScanNone, vm.LastScan);
        Assert.True(_scanner.Started);

        _scanner.Scan("5449000000996");
        Assert.Contains("5449000000996", vm.LastScan);
        Assert.Contains("13", vm.LastScan);

        await vm.OnNavigatedFromAsync();
        Assert.False(_scanner.Started);
        var shown = vm.LastScan;
        _scanner.Scan("1111");
        Assert.Equal(shown, vm.LastScan);
    }

    [Fact]
    public async Task Without_devices_manage_nothing_is_shown_tested_or_saved()
    {
        var vm = Screen();
        await vm.OnNavigatedToAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.WindowsPrinters);
        Assert.Empty(await File.ReadAllBytesAsync(Receipt));
        Assert.False(File.Exists(Local.Path));
    }
}
