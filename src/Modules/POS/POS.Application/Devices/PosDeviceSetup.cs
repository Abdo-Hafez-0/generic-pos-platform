using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Security;
using POS.Contracts.Models;

namespace POS.Application.Devices;

/// <summary>
/// MISS-07b: the till's devices on the Devices screen. Every entry point checks devices.manage first (sensitive: a test can open the drawer;
/// no license needed - a shop can always set up its printer), then asks the platform's <see cref="IDeviceSetup"/> (the adapters, which
/// carry no security), and records tests and saves in the audit log. Without device support on this installation it says so plainly.
/// </summary>
internal static class PosDeviceSetupMapping
{
    public static DeviceSetupSettings ToPlatform(POSDeviceSettings s)
        => new(Printer(s.ReceiptPrinter), s.CharactersPerLine, s.CutPaper, s.DrawerThroughReceiptPrinter, s.DrawerPin, s.KeyboardScanner, Printer(s.LabelPrinter));

    public static POSDeviceSettings ToPos(DeviceSetupSettings s)
        => new(Printer(s.ReceiptPrinter), s.CharactersPerLine, s.CutPaper, s.DrawerThroughReceiptPrinter, s.DrawerPin, s.KeyboardScanner, Printer(s.LabelPrinter));

    public static POSDeviceState State(DeviceStatus status)
        => new(status.State != DeviceState.NotConfigured, status.State == DeviceState.Ready, status.State is DeviceState.Ready or DeviceState.NotConfigured ? null : status.Message);

    private static PrinterSetup Printer(POSPrinterSetup p) => new((DeviceLink)(int)p.Link, p.PrinterName, p.Host, p.Port);

    private static POSPrinterSetup Printer(PrinterSetup p) => new((POSPrinterLink)(int)p.Link, p.PrinterName, p.Host, p.Port);

    public static string Describe(POSPrinterSetup p) => p.Link switch
    {
        POSPrinterLink.WindowsPrinter => $"Windows printer '{p.PrinterName}'",
        POSPrinterLink.Network => $"network {p.Host}:{p.Port}",
        POSPrinterLink.Configured => "as configured",
        _ => "none",
    };

    public static Result Unavailable() => Result.Failure(Error.Failure("POS.Devices.Unavailable", "Device setup is not available on this installation."));
}

public sealed record GetDeviceSetupQuery;

public sealed class GetDeviceSetupQueryHandler(IAuthorizationService authorization, IDeviceSetup? setup = null)
{
    public async Task<Result<POSDeviceSetupOverview>> HandleAsync(GetDeviceSetupQuery query, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POSCapabilities.ManageDevices, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<POSDeviceSetupOverview>(allowed.Error);
        if (setup is null) return Result.Failure<POSDeviceSetupOverview>(PosDeviceSetupMapping.Unavailable().Error);

        return new POSDeviceSetupOverview(PosDeviceSetupMapping.ToPos(setup.Saved), setup.WindowsPrinters(), setup.RestartNeeded);
    }
}

public sealed record CheckDevicesCommand(POSDeviceSettings Settings);

public sealed class CheckDevicesCommandHandler(IAuthorizationService authorization, IDeviceSetup? setup = null)
{
    public async Task<Result<POSDeviceCheck>> HandleAsync(CheckDevicesCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POSCapabilities.ManageDevices, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<POSDeviceCheck>(allowed.Error);
        if (setup is null) return Result.Failure<POSDeviceCheck>(PosDeviceSetupMapping.Unavailable().Error);

        var check = await setup.CheckAsync(PosDeviceSetupMapping.ToPlatform(command.Settings), cancellationToken);
        return new POSDeviceCheck(PosDeviceSetupMapping.State(check.ReceiptPrinter), PosDeviceSetupMapping.State(check.CashDrawer),
            PosDeviceSetupMapping.State(check.LabelPrinter), PosDeviceSetupMapping.State(check.Scanner));
    }
}

public enum POSDeviceTest
{
    Receipt,
    Drawer,
    Label,
}

public sealed record TestDeviceCommand(POSDeviceSettings Settings, POSDeviceTest Test);

public sealed class TestDeviceCommandHandler(IAuthorizationService authorization, IDeviceSetup? setup = null, IBusinessEventSink? events = null)
{
    public async Task<Result> HandleAsync(TestDeviceCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POSCapabilities.ManageDevices, cancellationToken);
        if (allowed.IsFailure) return allowed;
        if (setup is null) return PosDeviceSetupMapping.Unavailable();

        var settings = PosDeviceSetupMapping.ToPlatform(command.Settings);
        var (outcome, action, summary) = command.Test switch
        {
            POSDeviceTest.Receipt => (await setup.PrintTestReceiptAsync(settings, cancellationToken), "devices.test-receipt", "A test receipt was printed"),
            POSDeviceTest.Drawer => (await setup.OpenDrawerAsync(settings, cancellationToken), "devices.drawer-opened", "The cash drawer was opened from the Devices screen (a test, no sale)"),
            _ => (await setup.PrintTestLabelAsync(settings, cancellationToken), "devices.test-label", "A test label was printed"),
        };

        await events.TryRecordAsync(BusinessEvent.Create(POSCapabilities.Module, action, "devices", null,
            outcome.IsSuccess ? summary + "." : $"{summary} - it failed: {outcome.Error.Description}"));
        return outcome;
    }
}

public sealed record SaveDeviceSettingsCommand(POSDeviceSettings Settings);

/// <summary>Keeps the settings for the next start; the result says whether a restart is needed to use them.</summary>
public sealed class SaveDeviceSettingsCommandHandler(IAuthorizationService authorization, IDeviceSetup? setup = null, IBusinessEventSink? events = null)
{
    public async Task<Result<bool>> HandleAsync(SaveDeviceSettingsCommand command, CancellationToken cancellationToken = default)
    {
        var allowed = await authorization.AuthorizeAsync(POSCapabilities.ManageDevices, cancellationToken);
        if (allowed.IsFailure) return Result.Failure<bool>(allowed.Error);
        if (setup is null) return Result.Failure<bool>(PosDeviceSetupMapping.Unavailable().Error);

        var saved = setup.Save(PosDeviceSetupMapping.ToPlatform(command.Settings));
        if (saved.IsFailure) return Result.Failure<bool>(saved.Error);

        var s = command.Settings;
        await events.TryRecordAsync(BusinessEvent.Create(POSCapabilities.Module, "devices.settings-saved", "devices", null,
            $"Device settings saved: receipt printer {PosDeviceSetupMapping.Describe(s.ReceiptPrinter)}, cash drawer {(s.DrawerThroughReceiptPrinter ? "through the receipt printer" : "none")}, " +
            $"scanner {(s.KeyboardScanner ? "keyboard mode" : "none")}, label printer {PosDeviceSetupMapping.Describe(s.LabelPrinter)}."));
        return setup.RestartNeeded;
    }
}
