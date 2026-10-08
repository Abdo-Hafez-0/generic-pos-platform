using System.Globalization;
using System.Text;
using Client.Hardware.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Client.Hardware.Printing;

/// <summary>Turns a printer-independent receipt into ESC/POS bytes. ASCII only: other characters print as '?'.</summary>
public static class EscPosReceiptFormatter
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;

    public static byte[] Format(ReceiptDocument receipt, int charactersPerLine, bool cutPaper)
    {
        var width = Math.Clamp(charactersPerLine, 16, 80);
        var output = new List<byte>();

        void Command(params byte[] bytes) => output.AddRange(bytes);
        void Text(string text) => output.AddRange(Encoding.ASCII.GetBytes(Clean(text)));
        void Line(string text = "") { Text(text); output.Add(0x0A); }
        void Align(byte mode) => Command(Esc, 0x61, mode);       // 0 left, 1 center, 2 right
        void Bold(bool on) => Command(Esc, 0x45, on ? (byte)1 : (byte)0);

        Command(Esc, 0x40);                                       // initialise

        Align(1);
        if (!string.IsNullOrWhiteSpace(receipt.StoreName))
        {
            Bold(true);
            foreach (var l in Wrap(receipt.StoreName!, width)) Line(l);
            Bold(false);
        }

        foreach (var header in receipt.HeaderLines ?? [])
            foreach (var l in Wrap(header, width)) Line(l);

        Align(0);
        Line(new string('-', width));
        Line(Pair("Receipt", receipt.Reference, width));
        Line(receipt.IssuedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(receipt.Cashier)) Line(Truncate("Cashier " + receipt.Cashier, width));
        Line(new string('-', width));

        foreach (var item in receipt.Lines ?? [])
        {
            foreach (var l in Wrap(item.Description, width)) Line(l);
            Line(Pair($"  {Quantity(item.Quantity)} x {Money(item.UnitPrice)}", Money(item.LineTotal + item.Discount), width));
            if (item.Discount > 0m) Line(Pair("  discount", "-" + Money(item.Discount), width));   // FIX-08c
        }

        Line(new string('-', width));
        var discountTotal = (receipt.Lines ?? []).Sum(l => l.Discount);
        if (discountTotal > 0m)
        {
            Line(Pair("Subtotal", Money(receipt.Total + discountTotal), width));
            Line(Pair("Discount", "-" + Money(discountTotal), width));
        }

        Bold(true);
        Line(Pair("TOTAL", Money(receipt.Total), width));
        Bold(false);

        // FIX-08b: prices include tax - the tax contained in the total, per rate
        foreach (var tax in receipt.Taxes ?? [])
            Line(Pair($"incl. tax {Quantity(tax.Rate * 100m)}%", Money(tax.Amount), width));

        if (receipt.Payment is { } payment)
        {
            Line(Pair(payment.Method, Money(payment.Amount), width));
            if (payment.Tendered is { } tendered) Line(Pair("Tendered", Money(tendered), width));
            if (payment.Change is { } change) Line(Pair("Change", Money(change), width));
        }

        Align(1);
        foreach (var footer in receipt.FooterLines ?? [])
            foreach (var l in Wrap(footer, width)) Line(l);

        Line();
        Line();
        if (cutPaper) Command(Gs, 0x56, 0x42, 0x00);              // feed and partial cut
        return [.. output];
    }

    /// <summary>ESC p: pulse the cash-drawer connector (pin 0 or 1) for <paramref name="onMs"/>, then rest <paramref name="offMs"/>.</summary>
    public static byte[] DrawerPulse(int pin, int onMs, int offMs)
        => [Esc, 0x70, (byte)(pin == 1 ? 1 : 0), (byte)Math.Clamp(onMs / 2, 1, 255), (byte)Math.Clamp(offMs / 2, 1, 255)];

    public static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    public static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

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
public sealed class EscPosReceiptPrinter(IDeviceTransport transport, int charactersPerLine = 42, bool cutPaper = true, ILogger? logger = null) : IReceiptPrinter
{
    private const string Device = "receipt printer";
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public Task<DeviceStatus> GetStatusAsync(CancellationToken cancellationToken = default) => transport.ProbeAsync(cancellationToken);

    public async Task<Result> PrintAsync(ReceiptDocument receipt, CancellationToken cancellationToken = default)
    {
        if (receipt is null || receipt.Lines is null || receipt.Lines.Count == 0)
            return Result.Failure(HardwareErrors.InvalidData(Device, "the receipt has no lines."));

        var bytes = EscPosReceiptFormatter.Format(receipt, charactersPerLine, cutPaper);
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
