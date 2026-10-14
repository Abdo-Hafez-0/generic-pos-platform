using System.Text;
using Client.Hardware;
using Client.Hardware.Configuration;
using Client.Hardware.Setup;
using Client.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Hardware;

namespace Hardware.Tests;

/// <summary>
/// MISS-07b: the adapters' side of the Devices screen (<see cref="DeviceSetupAdapter"/>). The shop's choices map to the adapters' settings
/// and back; tests use the settings as entered (devices made for the test - here device files standing in for printers); saved settings go
/// to the installation's local settings file and become the "Hardware" configuration at the next start; nonsense is refused in plain words.
/// Permission checks and the audit log are the POS module's (POS.Tests).
/// </summary>
public sealed class DeviceSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genericpos-devices-" + Guid.NewGuid().ToString("N"));

    public DeviceSetupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private LocalSettingsFile Local => new(Path.Combine(_root, LocalSettingsFile.FileName));

    private string DeviceFile(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>An installation whose printers were set in the configuration as device paths (the screen shows them as "configured").</summary>
    private DeviceSetupAdapter Adapter(string receipt, string? label = null, LocalSettingsFile? local = null) => new(new HardwareOptions
    {
        ReceiptPrinter = { Type = "EscPosFile", Path = receipt },
        CashDrawer = { Type = "ViaReceiptPrinter" },
        LabelPrinter = { Type = label is null ? "None" : "ZplFile", Path = label },
        Scanner = { Type = "KeyboardWedge" },
    }, local ?? Local, () => ["TM-T20"]);

    [Fact]
    public async Task The_tests_use_the_settings_as_entered()
    {
        var receipt = DeviceFile("receipt.bin");
        var label = DeviceFile("label.zpl");
        var adapter = Adapter(receipt, label);
        var entered = adapter.Saved;
        Assert.Equal(DeviceLink.Configured, entered.ReceiptPrinter.Link);
        Assert.True(entered.DrawerThroughReceiptPrinter);
        Assert.True(entered.KeyboardScanner);

        var check = await adapter.CheckAsync(entered);
        Assert.Equal([DeviceState.Ready, DeviceState.Ready, DeviceState.Ready, DeviceState.Ready],
            new[] { check.ReceiptPrinter.State, check.CashDrawer.State, check.LabelPrinter.State, check.Scanner.State });

        Assert.True((await adapter.PrintTestReceiptAsync(entered)).IsSuccess);
        var text = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(receipt));
        Assert.Contains("Test receipt", text);
        Assert.Contains("If you can read this, the printer works.", text);

        File.WriteAllBytes(receipt, []);
        Assert.True((await adapter.OpenDrawerAsync(entered)).IsSuccess);
        Assert.Equal(new byte[] { 0x1B, (byte)'p' }, (await File.ReadAllBytesAsync(receipt))[..2]);   // ESC p: the drawer pulse

        Assert.True((await adapter.PrintTestLabelAsync(entered)).IsSuccess);
        Assert.StartsWith("^XA", Encoding.ASCII.GetString(await File.ReadAllBytesAsync(label)));

        // the scanner switched off as entered: "not used", nothing else changes
        Assert.Equal(DeviceState.NotConfigured, (await adapter.CheckAsync(entered with { KeyboardScanner = false })).Scanner.State);
    }

    [Fact]
    public async Task A_test_on_a_printer_that_is_not_there_fails_in_plain_words()
    {
        var adapter = Adapter(Path.Combine(_root, "missing", "printer.bin"));

        var printed = await adapter.PrintTestReceiptAsync(adapter.Saved);
        var network = await adapter.CheckAsync(adapter.Saved with { ReceiptPrinter = new PrinterSetup(DeviceLink.Network, Host: "127.0.0.1", Port: 9) });

        Assert.Equal(HardwareErrors.UnavailableCode, printed.Error.Code);
        Assert.Equal(DeviceState.Unavailable, network.ReceiptPrinter.State);
        Assert.Contains("127.0.0.1:9", network.ReceiptPrinter.Message);
    }

    [Theory]
    [InlineData(DeviceLink.WindowsPrinter, null, null, false, "Choose the Windows printer for the receipt printer.")]
    [InlineData(DeviceLink.Network, null, null, false, "Enter the network address and port (1-65535) of the receipt printer.")]
    [InlineData(DeviceLink.None, null, null, true, "A cash drawer opened through the receipt printer needs a receipt printer.")]
    public void Settings_that_cannot_work_are_refused_and_nothing_is_saved(DeviceLink link, string? name, string? host, bool drawer, string why)
    {
        var adapter = Adapter(DeviceFile("receipt.bin"));
        var settings = adapter.Saved with { ReceiptPrinter = new PrinterSetup(link, name, host), DrawerThroughReceiptPrinter = drawer };

        var saved = adapter.Save(settings);

        Assert.Equal("Devices.Invalid", saved.Error.Code);
        Assert.Equal(why, saved.Error.Description);
        Assert.False(File.Exists(Local.Path));
    }

    [Fact]
    public void Saving_keeps_the_choices_and_what_they_do_not_cover_and_asks_for_a_restart()
    {
        var receipt = DeviceFile("receipt.bin");
        File.WriteAllText(Local.Path, """{ "Other": { "Keep": "me" } }""");
        var adapter = Adapter(receipt);
        Assert.False(adapter.RestartNeeded);

        var chosen = adapter.Saved with { ReceiptPrinter = new PrinterSetup(DeviceLink.WindowsPrinter, "TM-T20"), CharactersPerLine = 48, DrawerPin = 1 };
        Assert.True(adapter.Save(chosen).IsSuccess);

        Assert.True(adapter.RestartNeeded);
        var written = Local.ReadSection<HardwareOptions>(HardwareOptions.SectionName)!;
        Assert.Equal(("EscPosWindows", "TM-T20", 48), (written.ReceiptPrinter.Type, written.ReceiptPrinter.PrinterName, written.ReceiptPrinter.CharactersPerLine));
        Assert.Equal(receipt, written.ReceiptPrinter.Path);           // not shown on the screen: kept
        Assert.Equal(("ViaReceiptPrinter", 1), (written.CashDrawer.Type, written.CashDrawer.Pin));
        Assert.Equal("me", Local.ReadSection<Dictionary<string, string>>("Other")!["Keep"]);   // another owner's section is untouched
        Assert.Equal(DeviceLink.WindowsPrinter, adapter.Saved.ReceiptPrinter.Link);
    }

    [Fact]
    public void The_width_of_receipt_pictures_follows_the_paper_unless_it_was_set_by_hand()
    {
        // MISS-07d: Arabic text is printed as a picture; a 576-dot picture does not fit 58 mm paper
        var adapter = Adapter(DeviceFile("receipt.bin"));

        Assert.Equal(DeviceSetupAdapter.Paper58MmDots, adapter.ToOptions(adapter.Saved with { CharactersPerLine = 32 }).ReceiptPrinter.DotsPerLine);
        Assert.Equal(DeviceSetupAdapter.Paper80MmDots, adapter.ToOptions(adapter.Saved with { CharactersPerLine = 48 }).ReceiptPrinter.DotsPerLine);
        Assert.True(adapter.Save(adapter.Saved with { CharactersPerLine = 32 }).IsSuccess);
        Assert.Equal(DeviceSetupAdapter.Paper58MmDots, Local.ReadSection<HardwareOptions>(HardwareOptions.SectionName)!.ReceiptPrinter.DotsPerLine);

        var byHand = new HardwareOptions();
        byHand.ReceiptPrinter.DotsPerLine = 512;   // a 72 mm printer
        Assert.Equal(512, new DeviceSetupAdapter(byHand, null, () => []).ToOptions(DeviceSetupAdapter.ToSetup(byHand) with { CharactersPerLine = 32 }).ReceiptPrinter.DotsPerLine);
    }

    [Fact]
    public void At_the_next_start_the_saved_settings_are_the_hardware_configuration_and_an_environment_override_still_wins()
    {
        var adapter = Adapter(DeviceFile("receipt.bin"));
        Assert.True(adapter.Save(adapter.Saved with { ReceiptPrinter = new PrinterSetup(DeviceLink.WindowsPrinter, "TM-T20") }).IsSuccess);

        // the order Client.Host builds: appsettings, then the local settings file, then environment variables
        IConfiguration Configure(Dictionary<string, string?>? environment = null)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Hardware:ReceiptPrinter:Type"] = "EscPosTcp", ["Hardware:ReceiptPrinter:Host"] = "10.0.0.5" })
                .AddJsonFile(Local.Path, optional: true)
                .AddInMemoryCollection(environment ?? [])
                .Build();

        HardwareOptions InUse(IConfiguration configuration)
        {
            var services = new ServiceCollection().AddLogging();
            new HardwareHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration }, services);
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            Assert.NotNull(provider.GetRequiredService<IDeviceSetup>());
            return provider.GetRequiredService<HardwareOptions>();
        }

        Assert.Equal(("EscPosWindows", "TM-T20"), (InUse(Configure()).ReceiptPrinter.Type, InUse(Configure()).ReceiptPrinter.PrinterName));
        Assert.Equal("EscPosFile", InUse(Configure(new() { ["Hardware:ReceiptPrinter:Type"] = "EscPosFile" })).ReceiptPrinter.Type);
    }

    [Fact]
    public void Without_a_local_settings_file_nothing_can_be_kept()
    {
        var adapter = new DeviceSetupAdapter(new HardwareOptions(), null, () => []);

        Assert.Equal("Devices.Unavailable", adapter.Save(adapter.Saved).Error.Code);
        Assert.False(adapter.RestartNeeded);
    }
}
