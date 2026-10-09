namespace Client.Hardware.Configuration;

/// <summary>
/// Hardware configuration (section "Hardware"). It is separate from business configuration, contains no secrets, and every device
/// defaults to "None": a machine with no peripherals is the normal case and the POS runs without any of them.
/// An unknown Type or an incomplete device configuration never stops the application: the device is registered as
/// Unavailable with a reason that is shown to the user when the device is used.
/// </summary>
public sealed class HardwareOptions
{
    public const string SectionName = "Hardware";

    public ScannerOptions Scanner { get; set; } = new();
    public ReceiptPrinterOptions ReceiptPrinter { get; set; } = new();
    public LabelPrinterOptions LabelPrinter { get; set; } = new();
    public CashDrawerOptions CashDrawer { get; set; } = new();
    public ScaleOptions Scale { get; set; } = new();
}

/// <summary>Where a byte-oriented device (printer, drawer) is reached.</summary>
public class DeviceConnectionOptions
{
    /// <summary>Network host or IP (for the Tcp transports), e.g. a network receipt printer on the shop LAN.</summary>
    public string? Host { get; set; }

    /// <summary>Network port (default 9100, the common raw-printing port).</summary>
    public int Port { get; set; } = 9100;

    /// <summary>Device path (for the File transports): a printer share (UNC) or a device node.</summary>
    public string? Path { get; set; }

    /// <summary>How long a connect/write may take before it is reported as a timeout.</summary>
    public int TimeoutMilliseconds { get; set; } = 3000;
}

/// <summary>Type: None | KeyboardWedge.</summary>
public sealed class ScannerOptions
{
    public string Type { get; set; } = "None";

    /// <summary>Shorter input is ignored (stray keystrokes).</summary>
    public int MinimumLength { get; set; } = 4;

    /// <summary>Characters arriving further apart than this are treated as typing, not a scan.</summary>
    public int MaxInterCharacterMilliseconds { get; set; } = 80;
}

/// <summary>Type: None | EscPosTcp | EscPosFile.</summary>
public sealed class ReceiptPrinterOptions : DeviceConnectionOptions
{
    public string Type { get; set; } = "None";

    public int CharactersPerLine { get; set; } = 42;

    public bool CutPaper { get; set; } = true;

    /// <summary>FIX-13a: Auto (a picture only when a text needs more than ASCII, e.g. Arabic) | Always | Never.</summary>
    public string PrintAsImage { get; set; } = "Auto";

    /// <summary>FIX-13a: dots across the paper for pictures (576 for 80 mm, 384 for 58 mm at 203 dpi).</summary>
    public int DotsPerLine { get; set; } = 576;
}

/// <summary>Type: None | ZplTcp | ZplFile.</summary>
public sealed class LabelPrinterOptions : DeviceConnectionOptions
{
    public string Type { get; set; } = "None";

    /// <summary>Label width in printer dots (406 = 2 inch at 203 dpi).</summary>
    public int WidthDots { get; set; } = 406;

    /// <summary>Label height in printer dots (203 = 1 inch at 203 dpi).</summary>
    public int HeightDots { get; set; } = 203;
}

/// <summary>Type: None | ViaReceiptPrinter (kick pulse through the receipt printer) | EscPosTcp | EscPosFile.</summary>
public sealed class CashDrawerOptions : DeviceConnectionOptions
{
    public string Type { get; set; } = "None";

    /// <summary>Drawer connector pin: 0 or 1.</summary>
    public int Pin { get; set; }

    public int OnTimeMilliseconds { get; set; } = 100;

    public int OffTimeMilliseconds { get; set; } = 500;
}

/// <summary>Type: None. No generic scale protocol exists; vendor/serial adapters are future work behind IScale.</summary>
public sealed class ScaleOptions
{
    public string Type { get; set; } = "None";
}
