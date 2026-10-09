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

    /// <summary>Creates a transport for a connection, or explains why the configuration is unusable.</summary>
    internal static (IDeviceTransport? Transport, DeviceStatus? Problem) Transport(bool tcp, DeviceConnectionOptions options, ILogger? logger)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(options.TimeoutMilliseconds, 100, 60_000));

        if (tcp)
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
        if (!Is(options.Type, "EscPosTcp") && !Is(options.Type, "EscPosFile"))
            return new NullReceiptPrinter(Unknown("receipt printer", options.Type));

        var (transport, problem) = Transport(Is(options.Type, "EscPosTcp"), options, logger);
        return transport is null
            ? new NullReceiptPrinter(problem!)
            : new EscPosReceiptPrinter(transport, options.CharactersPerLine, options.CutPaper, logger, imageRenderer,
                Enum.TryParse<ReceiptImageMode>(options.PrintAsImage, ignoreCase: true, out var mode) ? mode : ReceiptImageMode.Auto, options.DotsPerLine);
    }

    public static ILabelPrinter CreateLabelPrinter(LabelPrinterOptions options, ILogger? logger = null)
    {
        if (IsNone(options.Type)) return new NullLabelPrinter(DeviceStatus.NotConfigured());
        if (!Is(options.Type, "ZplTcp") && !Is(options.Type, "ZplFile"))
            return new NullLabelPrinter(Unknown("label printer", options.Type));

        var (transport, problem) = Transport(Is(options.Type, "ZplTcp"), options, logger);
        return transport is null
            ? new NullLabelPrinter(problem!)
            : new ZplLabelPrinter(transport, options.WidthDots, options.HeightDots, logger);
    }

    public static ICashDrawer CreateCashDrawer(CashDrawerOptions options, ReceiptPrinterOptions receiptPrinter, ILogger? logger = null)
    {
        if (IsNone(options.Type)) return new NullCashDrawer(DeviceStatus.NotConfigured());

        DeviceConnectionOptions connection;
        bool tcp;

        if (Is(options.Type, "ViaReceiptPrinter"))
        {
            if (!Is(receiptPrinter.Type, "EscPosTcp") && !Is(receiptPrinter.Type, "EscPosFile"))
                return new NullCashDrawer(DeviceStatus.Unavailable("ViaReceiptPrinter needs an ESC/POS receipt printer to be configured."));

            connection = receiptPrinter;
            tcp = Is(receiptPrinter.Type, "EscPosTcp");
        }
        else if (Is(options.Type, "EscPosTcp") || Is(options.Type, "EscPosFile"))
        {
            connection = options;
            tcp = Is(options.Type, "EscPosTcp");
        }
        else
        {
            return new NullCashDrawer(Unknown("cash drawer", options.Type));
        }

        var (transport, problem) = Transport(tcp, connection, logger);
        return transport is null
            ? new NullCashDrawer(problem!)
            : new EscPosCashDrawer(transport, options.Pin, options.OnTimeMilliseconds, options.OffTimeMilliseconds, logger);
    }

    public static IScale CreateScale(ScaleOptions options)
        => IsNone(options.Type)
            ? new NullScale(DeviceStatus.NotConfigured())
            : new NullScale(Unknown("scale", options.Type));
}
