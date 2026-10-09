using System.Globalization;
using System.Text;
using Client.Hardware.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Printing;

/// <summary>When a receipt is sent as a picture instead of text (FIX-13a).</summary>
public enum ReceiptImageMode
{
    /// <summary>Only when a text cannot be printed with the printer's basic character set (anything outside printable ASCII).</summary>
    Auto,

    /// <summary>Every receipt.</summary>
    Always,

    /// <summary>Never: characters outside ASCII print as '?'.</summary>
    Never
}

/// <summary>
/// Turns a printer-independent receipt into ESC/POS bytes. The receipt is first laid out as lines (<see cref="Layout"/>); those are sent as
/// ASCII text, or - FIX-13a - drawn as a picture by an <see cref="IReceiptImageRenderer"/> and sent with GS v 0 when a text needs more than
/// ASCII (Arabic, accents ...). Without a renderer, characters outside ASCII print as '?'.
/// </summary>
public static class EscPosReceiptFormatter
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;

    /// <summary>Rows per GS v 0 command: printers limit the size of one raster command, so tall receipts go in bands.</summary>
    public const int RasterBandRows = 128;

    public static byte[] Format(ReceiptDocument receipt, int charactersPerLine, bool cutPaper,
        IReceiptImageRenderer? renderer = null, ReceiptImageMode imageMode = ReceiptImageMode.Auto, int dotsPerLine = 576)
    {
        var width = Math.Clamp(charactersPerLine, 16, 80);
        var lines = Layout(receipt, width);

        var asImage = renderer is not null && imageMode switch
        {
            ReceiptImageMode.Always => true,
            ReceiptImageMode.Never => false,
            _ => lines.Any(l => !IsPlainAscii(l.Left) || !IsPlainAscii(l.Right)),
        };

        var output = new List<byte> { Esc, 0x40 };                 // initialise
        if (asImage)
            output.AddRange(Raster(renderer!.Render(lines, Math.Max(8, dotsPerLine / 8 * 8))));
        else
            output.AddRange(Text(lines, width));

        output.AddRange([0x0A, 0x0A]);
        if (cutPaper) output.AddRange([Gs, 0x56, 0x42, 0x00]);    // feed and partial cut
        return [.. output];
    }

    /// <summary>The receipt as lines, wrapped to <paramref name="width"/> characters (the same lines are printed as text or drawn as a picture).</summary>
    public static IReadOnlyList<ReceiptImageLine> Layout(ReceiptDocument receipt, int width)
    {
        var lines = new List<ReceiptImageLine>();
        void Rule() => lines.Add(new ReceiptImageLine("", Rule: true));

        if (!string.IsNullOrWhiteSpace(receipt.StoreName))
            foreach (var l in Wrap(receipt.StoreName!, width)) lines.Add(new ReceiptImageLine(l, Bold: true, Center: true));

        foreach (var header in receipt.HeaderLines ?? [])
            foreach (var l in Wrap(header, width)) lines.Add(new ReceiptImageLine(l, Center: true));

        Rule();
        lines.Add(new ReceiptImageLine("Receipt", receipt.Reference));
        lines.Add(new ReceiptImageLine(receipt.IssuedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(receipt.Cashier)) lines.Add(new ReceiptImageLine("Cashier " + receipt.Cashier));
        Rule();

        foreach (var item in receipt.Lines ?? [])
        {
            foreach (var l in Wrap(item.Description, width)) lines.Add(new ReceiptImageLine(l));
            lines.Add(new ReceiptImageLine($"  {Quantity(item.Quantity)} x {Money(item.UnitPrice)}", Money(item.LineTotal + item.Discount)));
            if (item.Discount > 0m) lines.Add(new ReceiptImageLine("  discount", "-" + Money(item.Discount)));   // FIX-08c
        }

        Rule();
        var discountTotal = (receipt.Lines ?? []).Sum(l => l.Discount);
        if (discountTotal > 0m)
        {
            lines.Add(new ReceiptImageLine("Subtotal", Money(receipt.Total + discountTotal)));
            lines.Add(new ReceiptImageLine("Discount", "-" + Money(discountTotal)));
        }

        lines.Add(new ReceiptImageLine("TOTAL", Money(receipt.Total), Bold: true));

        // FIX-08b: prices include tax - the tax contained in the total, per rate
        foreach (var tax in receipt.Taxes ?? [])
            lines.Add(new ReceiptImageLine($"incl. tax {Quantity(tax.Rate * 100m)}%", Money(tax.Amount)));

        foreach (var payment in receipt.AllPayments)
        {
            lines.Add(new ReceiptImageLine(payment.Method, Money(payment.Amount)));
            if (payment.Tendered is { } tendered) lines.Add(new ReceiptImageLine("Tendered", Money(tendered)));
            if (payment.Change is { } change) lines.Add(new ReceiptImageLine("Change", Money(change)));
        }

        foreach (var footer in receipt.FooterLines ?? [])
            foreach (var l in Wrap(footer, width)) lines.Add(new ReceiptImageLine(l, Center: true));

        return lines;
    }

    /// <summary>ESC p: pulse the cash-drawer connector (pin 0 or 1) for <paramref name="onMs"/>, then rest <paramref name="offMs"/>.</summary>
    public static byte[] DrawerPulse(int pin, int onMs, int offMs)
        => [Esc, 0x70, (byte)(pin == 1 ? 1 : 0), (byte)Math.Clamp(onMs / 2, 1, 255), (byte)Math.Clamp(offMs / 2, 1, 255)];

    public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>True when every character is printable ASCII (what a printer's basic character set prints the same everywhere).</summary>
    public static bool IsPlainAscii(string text) => text.All(c => c is >= ' ' and <= '~');

    private static IEnumerable<byte> Text(IReadOnlyList<ReceiptImageLine> lines, int width)
    {
        var output = new List<byte>();
        var (center, bold) = (false, false);
        foreach (var line in lines)
        {
            if (line.Center != center) { output.AddRange([Esc, 0x61, line.Center ? (byte)1 : (byte)0]); center = line.Center; }   // 1 centre, 0 left
            if (line.Bold != bold) { output.AddRange([Esc, 0x45, line.Bold ? (byte)1 : (byte)0]); bold = line.Bold; }
            var text = line.Rule ? new string('-', width) : Pair(line.Left, line.Right, width);
            output.AddRange(Encoding.ASCII.GetBytes(Clean(text)));
            output.Add(0x0A);
        }

        if (bold) output.AddRange([Esc, 0x45, 0]);
        if (center) output.AddRange([Esc, 0x61, 0]);
        return output;
    }

    /// <summary>GS v 0 (print raster bit image), in bands of <see cref="RasterBandRows"/> rows.</summary>
    private static IEnumerable<byte> Raster(MonochromeImage image)
    {
        var bytesPerRow = image.Width / 8;
        var output = new List<byte>();
        for (var top = 0; top < image.Height; top += RasterBandRows)
        {
            var rows = Math.Min(RasterBandRows, image.Height - top);
            output.AddRange([Gs, 0x76, 0x30, 0x00, (byte)(bytesPerRow % 256), (byte)(bytesPerRow / 256), (byte)(rows % 256), (byte)(rows / 256)]);
            output.AddRange(image.Rows.AsSpan(top * bytesPerRow, rows * bytesPerRow).ToArray());
        }

        return output;
    }

    private static string Clean(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    private static string Pair(string left, string right, int width)
    {
        if (right.Length == 0) return Truncate(left, width);

        var room = width - right.Length - 1;
        return Truncate(left, Math.Max(room, 0)).PadRight(Math.Max(room, 0) + 1) + right;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private static IEnumerable<string> Wrap(string text, int width)
    {
        text = Clean(text).Trim();
        if (text.Length == 0) { yield return ""; yield break; }

        while (text.Length > width)
        {
            var cut = text.LastIndexOf(' ', width - 1);
            if (cut <= 0) cut = width;
            yield return text[..cut].TrimEnd();
            text = text[cut..].TrimStart();
        }

        yield return text;
    }
}

/// <summary>Receipt printer speaking ESC/POS over any <see cref="IDeviceTransport"/>. Never throws for device problems.</summary>
public sealed class EscPosReceiptPrinter(IDeviceTransport transport, int charactersPerLine = 42, bool cutPaper = true, ILogger? logger = null,
    IReceiptImageRenderer? imageRenderer = null, ReceiptImageMode imageMode = ReceiptImageMode.Auto, int dotsPerLine = 576) : IReceiptPrinter
{
    private const string Device = "receipt printer";
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => transport.ProbeAsync(cancellationToken);

    public async Task<Result> PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default)
    {
        if (receipt is null || receipt.Lines is null || receipt.Lines.Count == 0)
            return Result.Failure(HardwareErrors.InvalidData(Device, "the receipt has no lines."));

        byte[] bytes;
        try
        {
            bytes = EscPosReceiptFormatter.Format(receipt, charactersPerLine, cutPaper, imageRenderer, imageMode, dotsPerLine);
        }
        catch (Exception ex) when (imageRenderer is not null && ex is not OperationCanceledException)
        {
            // drawing the picture failed (fonts, graphics): print the text rather than nothing ('?' for what ASCII cannot show)
            _logger.LogWarning(ex, "Receipt {Reference} could not be drawn as a picture; it is printed as text.", receipt.Reference);
            bytes = EscPosReceiptFormatter.Format(receipt, charactersPerLine, cutPaper, null, ReceiptImageMode.Never, dotsPerLine);
        }

        var result = await transport.SendAsync(bytes, cancellationToken);
        if (result.IsFailure)
            _logger.LogWarning("Receipt {Reference} was not printed: {Error}", receipt.Reference, result.Error);

        return result;
    }
}

/// <summary>Cash drawer opened with the standard ESC/POS kick pulse, sent through the printer's (or its own) transport.</summary>
public sealed class EscPosCashDrawer(IDeviceTransport transport, int pin = 0, int onMilliseconds = 100, int offMilliseconds = 500, ILogger? logger = null) : ICashDrawer
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => transport.ProbeAsync(cancellationToken);

    public async Task<Result> OpenAsync(CancellationToken cancellationToken = default)
    {
        var result = await transport.SendAsync(EscPosReceiptFormatter.DrawerPulse(pin, onMilliseconds, offMilliseconds), cancellationToken);
        if (result.IsFailure)
            _logger.LogWarning("The cash drawer was not opened: {Error}", result.Error);

        return result;
    }
}
