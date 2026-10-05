using System.Globalization;
using System.Text;
using Client.Hardware.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Printing;

/// <summary>Turns a printer-independent label into ZPL II (understood by Zebra-compatible label printers).</summary>
public static class ZplLabelFormatter
{
    public const int MaxCopies = 100;
    public const int MaxNameLength = 30;

    /// <summary>Null when the label is printable; otherwise why not.</summary>
    public static string? Validate(LabelDocument label)
    {
        if (label is null) return "no label was given.";
        if (string.IsNullOrWhiteSpace(label.Code) || label.Code.Any(c => c < 0x20 || c > 0x7E))
            return "the label code must be non-empty printable ASCII (it is printed as a Code 128 barcode).";
        if (label.Copies is < 1 or > MaxCopies) return $"copies must be between 1 and {MaxCopies}.";
        return null;
    }

    public static byte[] Format(LabelDocument label, int widthDots, int heightDots)
    {
        var zpl = new StringBuilder();
        zpl.Append("^XA^CI28");                                                           // start label, UTF-8
        zpl.Append(CultureInfo.InvariantCulture, $"^PW{widthDots}^LL{heightDots}");
        zpl.Append(CultureInfo.InvariantCulture, $"^FO20,15^A0N,28,28^FD{Field(Truncate(label.ProductName, MaxNameLength))}^FS");
        zpl.Append(CultureInfo.InvariantCulture, $"^FO20,55^BY2^BCN,70,Y,N,N^FD{Field(label.Code)}^FS");
        if (label.Price is { } price)
            zpl.Append(CultureInfo.InvariantCulture, $"^FO20,{Math.Max(heightDots - 55, 150)}^A0N,34,34^FD{price.ToString("0.00", CultureInfo.InvariantCulture)}^FS");
        zpl.Append(CultureInfo.InvariantCulture, $"^PQ{label.Copies}^XZ");                 // quantity, end label
        return Encoding.UTF8.GetBytes(zpl.ToString());
    }

    // ZPL command characters inside field data would be read as commands.
    private static string Field(string value) => value.Replace('^', ' ').Replace('~', ' ').Replace('\r', ' ').Replace('\n', ' ');

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

/// <summary>Label printer speaking ZPL over any <see cref="IDeviceTransport"/>. Never throws for device problems.</summary>
public sealed class ZplLabelPrinter(IDeviceTransport transport, int widthDots = 406, int heightDots = 203, ILogger? logger = null) : ILabelPrinter
{
    private const string Device = "label printer";
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => transport.ProbeAsync(cancellationToken);

    public async Task<Result> PrintAsync(LabelDocument label, CancellationToken cancellationToken = default)
    {
        var invalid = ZplLabelFormatter.Validate(label);
        if (invalid is not null)
            return Result.Failure(HardwareErrors.InvalidData(Device, invalid));

        var result = await transport.SendAsync(ZplLabelFormatter.Format(label, widthDots, heightDots), cancellationToken);
        if (result.IsFailure)
            _logger.LogWarning("Label for {Code} was not printed: {Error}", label.Code, result.Error);

        return result;
    }
}
