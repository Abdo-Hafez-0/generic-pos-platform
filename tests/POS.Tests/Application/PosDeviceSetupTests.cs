using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Devices;
using POS.Application.Security;
using POS.Contracts.Models;
using Tests.Common.Security;

namespace POS.Tests.Application;

/// <summary>
/// MISS-07b: the POS module owns setting up the till's devices: devices.manage is checked before anything is read, tested or saved; tests
/// and saves go to the audit log (a failed test too); the platform's device setup (the adapters) is optional and its absence is said plainly.
/// </summary>
public sealed class PosDeviceSetupTests
{
    private sealed class Events : IBusinessEventSink
    {
        public List<BusinessEvent> Recorded { get; } = [];

        public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(businessEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSetup : IDeviceSetup
    {
        public DeviceSetupSettings? LastUsed { get; private set; }
        public DeviceSetupSettings? SavedSettings { get; private set; }
        public Result DrawerOutcome { get; set; } = Result.Success();

        public DeviceSetupSettings Saved => SavedSettings ?? new DeviceSetupSettings(new PrinterSetup(DeviceLink.WindowsPrinter, "TM-T20"), 42, true, true, 0, true, new PrinterSetup(DeviceLink.None));
        public bool RestartNeeded => SavedSettings is not null;
        public IReadOnlyList<string> WindowsPrinters() => ["TM-T20"];

        public Task<DeviceSetupCheck> CheckAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default)
        {
            LastUsed = settings;
            return Task.FromResult(new DeviceSetupCheck(DeviceStatus.Ready(), DeviceStatus.Unavailable("The printer 'TM-T20' is out of paper."), DeviceStatus.NotConfigured(), DeviceStatus.Ready()));
        }

        public Task<Result> PrintTestReceiptAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default) { LastUsed = settings; return Task.FromResult(Result.Success()); }
        public Task<Result> OpenDrawerAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default) { LastUsed = settings; return Task.FromResult(DrawerOutcome); }
        public Task<Result> PrintTestLabelAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default) { LastUsed = settings; return Task.FromResult(Result.Success()); }

        public Result Save(DeviceSetupSettings settings)
        {
            SavedSettings = settings;
            return Result.Success();
        }
    }

    private static POSDeviceSettings Entered => new(new POSPrinterSetup(POSPrinterLink.Network, null, "10.0.0.7", 9100), 48, false, true, 1, false, new POSPrinterSetup(POSPrinterLink.WindowsPrinter, "Zebra"));

    [Fact]
    public async Task The_screen_gets_the_saved_choices_and_each_devices_state_and_tests_use_what_is_entered()
    {
        var setup = new FakeSetup();
        var allowed = new ScriptedAuthorizationService(POSCapabilities.ManageDevices);

        var overview = (await new GetDeviceSetupQueryHandler(allowed, setup).HandleAsync(new GetDeviceSetupQuery())).Value;
        Assert.Equal(POSPrinterLink.WindowsPrinter, overview.Saved.ReceiptPrinter.Link);
        Assert.Equal("TM-T20", overview.Saved.ReceiptPrinter.PrinterName);
        Assert.Equal(["TM-T20"], overview.WindowsPrinters);
        Assert.False(overview.RestartNeeded);

        var check = (await new CheckDevicesCommandHandler(allowed, setup).HandleAsync(new CheckDevicesCommand(Entered))).Value;
        Assert.Equal(new POSDeviceState(true, true, null), check.ReceiptPrinter);
        Assert.Equal(new POSDeviceState(true, false, "The printer 'TM-T20' is out of paper."), check.CashDrawer);
        Assert.Equal(new POSDeviceState(false, false, null), check.LabelPrinter);
        Assert.Equal(new DeviceSetupSettings(new PrinterSetup(DeviceLink.Network, null, "10.0.0.7", 9100), 48, false, true, 1, false, new PrinterSetup(DeviceLink.WindowsPrinter, "Zebra")), setup.LastUsed);
    }

    [Fact]
    public async Task Tests_and_saves_are_audited_a_failed_test_too_and_saving_says_a_restart_is_needed()
    {
        var setup = new FakeSetup { DrawerOutcome = Result.Failure(HardwareErrors.Unavailable("Windows printer 'TM-T20'", "is offline")) };
        var events = new Events();
        var allowed = new ScriptedAuthorizationService(POSCapabilities.ManageDevices);

        Assert.True((await new TestDeviceCommandHandler(allowed, setup, events).HandleAsync(new TestDeviceCommand(Entered, POSDeviceTest.Receipt))).IsSuccess);
        Assert.True((await new TestDeviceCommandHandler(allowed, setup, events).HandleAsync(new TestDeviceCommand(Entered, POSDeviceTest.Drawer))).IsFailure);
        Assert.True((await new TestDeviceCommandHandler(allowed, setup, events).HandleAsync(new TestDeviceCommand(Entered, POSDeviceTest.Label))).IsSuccess);
        var saved = await new SaveDeviceSettingsCommandHandler(allowed, setup, events).HandleAsync(new SaveDeviceSettingsCommand(Entered));

        Assert.True(saved.Value);
        Assert.Equal(["devices.test-receipt", "devices.drawer-opened", "devices.test-label", "devices.settings-saved"], events.Recorded.Select(e => e.Action));
        Assert.Contains("it failed", events.Recorded[1].Summary);
        Assert.Contains("network 10.0.0.7:9100", events.Recorded[3].Summary);
        Assert.Contains("Windows printer 'Zebra'", events.Recorded[3].Summary);
        Assert.All(events.Recorded, e => Assert.Equal(POSCapabilities.Module, e.Module));
    }

    [Fact]
    public async Task Without_devices_manage_nothing_is_read_tested_or_saved()
    {
        var setup = new FakeSetup();
        var events = new Events();
        var none = new ScriptedAuthorizationService();

        Assert.True((await new GetDeviceSetupQueryHandler(none, setup).HandleAsync(new GetDeviceSetupQuery())).IsFailure);
        Assert.True((await new CheckDevicesCommandHandler(none, setup).HandleAsync(new CheckDevicesCommand(Entered))).IsFailure);
        foreach (var test in Enum.GetValues<POSDeviceTest>())
            Assert.True((await new TestDeviceCommandHandler(none, setup, events).HandleAsync(new TestDeviceCommand(Entered, test))).IsFailure);
        Assert.True((await new SaveDeviceSettingsCommandHandler(none, setup, events).HandleAsync(new SaveDeviceSettingsCommand(Entered))).IsFailure);

        Assert.Equal(Enumerable.Repeat(POSCapabilities.ManageDevices, 6), none.Asked);
        Assert.Null(setup.LastUsed);
        Assert.Null(setup.SavedSettings);
        Assert.Empty(events.Recorded);
    }

    [Fact]
    public async Task Without_device_support_the_screen_says_so_plainly()
    {
        var allowed = new ScriptedAuthorizationService(POSCapabilities.ManageDevices);

        var overview = await new GetDeviceSetupQueryHandler(allowed).HandleAsync(new GetDeviceSetupQuery());

        Assert.Equal("POS.Devices.Unavailable", overview.Error.Code);
        Assert.Equal("Device setup is not available on this installation.", overview.Error.Description);
    }

    [Fact]
    public void Setting_up_devices_is_sensitive_and_needs_no_license()
    {
        var capability = Assert.Single(POSCapabilities.All, c => c.Code == POSCapabilities.ManageDevices);

        Assert.True(capability.IsSensitive);
        Assert.Equal(LicenseRequirement.None, capability.License);
        Assert.Equal(POSCapabilities.Module, capability.Module);
    }
}
