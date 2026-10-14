using Client.Hardware.Configuration;
using Client.Hardware.Devices;
using Client.Hardware.Printing;
using Client.Hardware.Scanner;
using Client.Hardware.Transport;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Hardware;

namespace Client.Hardware;

/// <summary>
/// Chooses the adapter for each device from configuration. This is the single place that maps a configured Type to a concrete
/// implementation, so replacing hardware is a configuration/registration change and never a business-code change.
/// Anything missing or unknown yields a placeholder that reports why, instead of failing application start-up.
/// </summary>
public static class HardwareFactory
{
    private static bool Is(string? type, string expected) => string.Equals(type?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool IsNone(string? type) => string.IsNullOrWhiteSpace(type) || Is(type, "None");

    private static DeviceStatus Unknown(string device, string? type) => DeviceStatus.Unavailable($"Unknown {device} type '{type}'.");

    /// <summary>How a byte-oriented device is reached: the network, a device path, or (MISS-07a) a printer installed in Windows.</summary>
    internal enum Link
    {
        Tcp,
        File,
        Windows,
    }

    /// <summary>The link a type names by its ending: ...Tcp, ...File or ...Windows (null when it names none).</summary>
    internal static Link? LinkOf(string? type, string protocol)
        => Is(type, protocol + "Tcp") ? Link.Tcp : Is(type, protocol + "File") ? Link.File : Is(type, protocol + "Windows") ? Link.Windows : null;

    /// <summary>Creates a transport for a connection, or explains why the configuration is unusable.</summary>
    internal static (IDeviceTransport? Transport, DeviceStatus? Problem) Transport(Link link, DeviceConnectionOptions options, ILogger? logger)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(options.TimeoutMilliseconds, 100, 60_000));

        if (link == Link.Windows)
        {
            return string.IsNullOrWhiteSpace(options.PrinterName)
                ? (null, DeviceStatus.Unavailable("A Windows printer needs the PrinterName it has under Settings > Printers & scanners."))
                : (new WindowsPrinterTransport(options.PrinterName.Trim(), timeout, logger), null);
        }

        if (link == Link.Tcp)
        {
            if (string.IsNullOrWhiteSpace(options.Host) || options.Port is < 1 or > 65535)
                return (null, DeviceStatus.Unavailable("A network device needs a Host and a Port between 1 and 65535."));

            return (new TcpDeviceTransport(options.Host.Trim(), options.Port, timeout, logger), null);
        }

        return string.IsNullOrWhiteSpace(options.Path)
            ? (null, DeviceStatus.Unavailable("A device-path device needs a Path."))
            : (new FileDeviceTransport(options.Path.Trim(), timeout, logger), null);
    }

    public static IBarcodeScanner CreateScanner(ScannerOptions options, TimeProvider? time = null, ILogger? logger = null)
    {
        if (IsNone(options.Type)) return new NullBarcodeScanner(DeviceStatus.NotConfigured());
        if (Is(options.Type, "KeyboardWedge"))
            return new KeyboardWedgeBarcodeScanner(Math.Max(options.MinimumLength, 1), Math.Max(options.MaxInterCharacterMilliseconds, 1), time, logger);

        return new NullBarcodeScanner(Unknown("barcode scanner", options.Type));
    }

    public static IReceiptPrinter CreateReceiptPrinter(ReceiptPrinterOptions options, ILogger? logger = null, IReceiptImageRenderer? imageRenderer = null)
    {
        if (IsNone(options.Type)) return new NullReceiptPrinter(DeviceStatus.NotConfigured());
        if (LinkOf(options.Type, "EscPos") is not { } link)
            return new NullReceiptPrinter(Unknown("receipt printer", options.Type));

        var (transport, problem) = Transport(link, options, logger);
        return transport is null
            ? new NullReceiptPrinter(problem!)
            : new EscPosReceiptPrinter(transport, options.CharactersPerLine, options.CutPaper, logger, imageRenderer,
                Enum.TryParse<ReceiptImageMode>(options.PrintAsImage, ignoreCase: true, out var mode) ? mode : ReceiptImageMode.Auto, options.DotsPerLine);
    }

    public static ILabelPrinter CreateLabelPrinter(LabelPrinterOptions options, ILogger? logger = null)
    {
        if (IsNone(options.Type)) return new NullLabelPrinter(DeviceStatus.NotConfigured());
        if (LinkOf(options.Type, "Zpl") is not { } link)
            return new NullLabelPrinter(Unknown("label printer", options.Type));

        var (transport, problem) = Transport(link, options, logger);
        return transport is null
            ? new NullLabelPrinter(problem!)
            : new ZplLabelPrinter(transport, options.WidthDots, options.HeightDots, logger);
    }

    public static ICashDrawer CreateCashDrawer(CashDrawerOptions options, ReceiptPrinterOptions receiptPrinter, ILogger? logger = null)
    {
        if (IsNone(options.Type)) return new NullCashDrawer(DeviceStatus.NotConfigured());

        DeviceConnectionOptions connection;
        Link link;

        if (Is(options.Type, "ViaReceiptPrinter"))
        {
            if (LinkOf(receiptPrinter.Type, "EscPos") is not { } printerLink)
                return new NullCashDrawer(DeviceStatus.Unavailable("ViaReceiptPrinter needs an ESC/POS receipt printer to be configured."));

            connection = receiptPrinter;
            link = printerLink;
        }
        else if (LinkOf(options.Type, "EscPos") is { } drawerLink)
        {
            connection = options;
            link = drawerLink;
        }
        else
        {
            return new NullCashDrawer(Unknown("cash drawer", options.Type));
        }

        var (transport, problem) = Transport(link, connection, logger);
        return transport is null
            ? new NullCashDrawer(problem!)
            : new EscPosCashDrawer(transport, options.Pin, options.OnTimeMilliseconds, options.OffTimeMilliseconds, logger);
    }

    public static IScale CreateScale(ScaleOptions options)
        => IsNone(options.Type)
            ? new NullScale(DeviceStatus.NotConfigured())
            : new NullScale(Unknown("scale", options.Type));
}
