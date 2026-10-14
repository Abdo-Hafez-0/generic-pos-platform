using Platform.Core.Results;

namespace Platform.Application.Abstractions.Hardware;

/// <summary>How a printer is reached, in words a shop understands (MISS-07b). The adapter maps it to its own technology.</summary>
public enum DeviceLink
{
    None,

    /// <summary>A printer installed in Windows (usually USB with the maker's driver), by its Windows name.</summary>
    WindowsPrinter,

    /// <summary>A printer on the shop's network, by address and port.</summary>
    Network,

    /// <summary>Something set in the configuration that the setup screen does not offer (for example a device path): kept as it is.</summary>
    Configured,
}

public sealed record PrinterSetup(DeviceLink Link, string? PrinterName = null, string? Host = null, int Port = 9100);

/// <summary>The peripherals as a setup screen shows them; everything else the adapter knows is kept as it is.</summary>
public sealed record DeviceSetupSettings(
    PrinterSetup ReceiptPrinter,
    int CharactersPerLine,
    bool CutPaper,
    bool DrawerThroughReceiptPrinter,
    int DrawerPin,
    bool KeyboardScanner,
    PrinterSetup LabelPrinter);

/// <summary>Each device's state for a set of settings.</summary>
public sealed record DeviceSetupCheck(DeviceStatus ReceiptPrinter, DeviceStatus CashDrawer, DeviceStatus LabelPrinter, DeviceStatus Scanner);

/// <summary>
/// MISS-07b: choosing and testing the peripherals. Implemented by the hardware adapters (no security there); the POS module authorizes and
/// audits every call. Tests use the settings given (devices made for that one test); saved settings apply at the next start - the devices in
/// use are never replaced while the application runs.
/// </summary>
public interface IDeviceSetup
{
    /// <summary>The settings that will be used at the next start.</summary>
    DeviceSetupSettings Saved { get; }

    /// <summary>The saved settings differ from the ones in use (a restart applies them).</summary>
    bool RestartNeeded { get; }

    /// <summary>The printers installed in Windows (empty elsewhere).</summary>
    IReadOnlyList<string> WindowsPrinters();

    Task<DeviceSetupCheck> CheckAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default);

    Task<Result> PrintTestReceiptAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default);

    Task<Result> OpenDrawerAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default);

    Task<Result> PrintTestLabelAsync(DeviceSetupSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Checks the settings make sense and keeps them for the next start; a plain validation error otherwise.</summary>
    Result Save(DeviceSetupSettings settings);
}
