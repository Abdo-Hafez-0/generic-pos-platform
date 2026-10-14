namespace POS.Contracts.Models;

/// <summary>How the till reaches a printer (MISS-07b). Configured: set in the configuration in a way the Devices screen does not offer; kept.</summary>
public enum POSPrinterLink
{
    None,
    WindowsPrinter,
    Network,
    Configured,
}

public sealed record POSPrinterSetup(POSPrinterLink Link, string? PrinterName = null, string? Host = null, int Port = 9100);

/// <summary>The till's devices as the Devices screen shows them.</summary>
public sealed record POSDeviceSettings(
    POSPrinterSetup ReceiptPrinter,
    int CharactersPerLine,
    bool CutPaper,
    bool DrawerThroughReceiptPrinter,
    int DrawerPin,
    bool KeyboardScanner,
    POSPrinterSetup LabelPrinter);

/// <summary>A device's state: used or not, ready or not, and why not in plain words.</summary>
public sealed record POSDeviceState(bool IsUsed, bool IsReady, string? Problem);

public sealed record POSDeviceCheck(POSDeviceState ReceiptPrinter, POSDeviceState CashDrawer, POSDeviceState LabelPrinter, POSDeviceState Scanner);

public sealed record POSDeviceSetupOverview(POSDeviceSettings Saved, IReadOnlyList<string> WindowsPrinters, bool RestartNeeded);
