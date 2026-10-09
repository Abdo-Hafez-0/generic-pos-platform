using System.Text;
using Client.Hardware.Printing;
using Client.Hardware.Transport;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;

namespace Hardware.Tests;

/// <summary>A transport that records what it was asked to send, or fails on demand.</summary>
public sealed class RecordingTransport : IDeviceTransport
{
    public List<byte[]> Sent { get; } = [];
    public Result? FailWith { get; set; }
    public DeviceStatus Status { get; set; } = DeviceStatus.Ready();
    public string Description => "recording";

    public Task<Result> SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (FailWith is not null) return Task.FromResult(FailWith);
        Sent.Add(data.ToArray());
        return Task.FromResult(Result.Success());
    }

    public Task<DeviceStatus> ProbeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

    public string Text => Encoding.ASCII.GetString(Sent.SelectMany(b => b).ToArray());
}

public sealed class EscPosReceiptTests
{
    private static ReceiptDocument Receipt(Action<List<ReceiptLine>>? lines = null, ReceiptPayment? payment = null, string? store = "Corner Shop")
    {
        var items = new List<ReceiptLine> { new("Cola 330ml", 2m, 1.5m, 3m), new("Bread", 1m, 2.25m, 2.25m) };
        lines?.Invoke(items);
        return new ReceiptDocument(store, ["12 Main Street"], "S-ABCD1234", new DateTimeOffset(2026, 6, 1, 14, 30, 0, TimeSpan.Zero),
            "cashier-1", items, items.Sum(i => i.LineTotal), payment, ["Thank you!"]);
    }

    private static string Render(ReceiptDocument r, int width = 32, bool cutPaper = true)
        => Encoding.ASCII.GetString(EscPosReceiptFormatter.Format(r, width, cutPaper));

    [Fact]
    public void ReceiptStartsByInitialisingThePrinter_AndCutsAtTheEnd()
    {
        var bytes = EscPosReceiptFormatter.Format(Receipt(), 32, cutPaper: true);

        Assert.Equal([0x1B, 0x40], bytes[..2]);
        Assert.Equal([0x1D, 0x56, 0x42, 0x00], bytes[^4..]);
    }

    [Fact]
    public void TheTaxContainedInTheTotal_IsPrintedPerRateAfterTheTotal_AndNothingWithoutTax()
    {
        // FIX-08b: prices include tax
        var taxed = Receipt() with { Taxes = [new ReceiptTax(0.14m, 0.65m), new ReceiptTax(0.05m, 0.11m)] };
        var lines = Render(taxed, width: 32).Split('\n');

        var total = Array.FindIndex(lines, l => l.Contains("TOTAL"));
        Assert.EndsWith("0.65", lines.Single(l => l.Contains("incl. tax 14%")).TrimEnd());
        Assert.EndsWith("0.11", lines.Single(l => l.Contains("incl. tax 5%")).TrimEnd());
        Assert.True(Array.FindIndex(lines, l => l.Contains("incl. tax 14%")) > total);
        Assert.DoesNotContain("incl. tax", Render(Receipt(), width: 32));
    }

    [Fact]
    public void ADiscountedLineShowsItsPriceThenTheDiscount_AndTheReceiptShowsSubtotalAndDiscountBeforeTheTotal()
    {
        // FIX-08c: the line total on the receipt line is what is charged; the price before the discount is printed with the discount under it
        var receipt = Receipt(items => items[0] = new ReceiptLine("Cola 330ml", 2m, 1.5m, 2.5m, 0.5m)) with { Total = 4.75m };
        var lines = Render(receipt, width: 32).Split('\n');

        Assert.EndsWith("3.00", lines.First(l => l.Contains("2 x 1.50")).TrimEnd());
        Assert.EndsWith("-0.50", lines.First(l => l.Contains("discount")).TrimEnd());
        Assert.EndsWith("5.25", lines.First(l => l.StartsWith("Subtotal", StringComparison.Ordinal)).TrimEnd());
        Assert.EndsWith("-0.50", lines.First(l => l.StartsWith("Discount", StringComparison.Ordinal)).TrimEnd());
        Assert.True(Array.FindIndex(lines, l => l.StartsWith("Discount", StringComparison.Ordinal)) < Array.FindIndex(lines, l => l.Contains("TOTAL")));
        Assert.DoesNotContain("Subtotal", Render(Receipt(), width: 32));   // no discount: no subtotal line
    }

    [Fact]
    public void CuttingCanBeTurnedOff()
    {
        var withCut = EscPosReceiptFormatter.Format(Receipt(), 32, cutPaper: true);
        var without = EscPosReceiptFormatter.Format(Receipt(), 32, cutPaper: false);

        Assert.Equal(withCut.Length - 4, without.Length);
        Assert.False(without.AsSpan().IndexOf(new byte[] { 0x1D, 0x56 }) >= 0);
    }

    [Fact]
    public void ContentIsPrintedInOrder_WithAmountsRightAligned()
    {
        var text = Render(Receipt(payment: new ReceiptPayment("Cash", 5.25m, 10m, 4.75m)), width: 32);
        var lines = text.Split('\n');

        Assert.Contains("Corner Shop", text);
        Assert.Contains("12 Main Street", text);
        Assert.Contains("S-ABCD1234", text);
        Assert.Contains("2026-06-01 14:30", text);
        Assert.Contains("Cashier cashier-1", text);
        Assert.Contains("Cola 330ml", text);
        Assert.Contains("  2 x 1.50", lines.First(l => l.Contains("2 x 1.50")));
        Assert.EndsWith("3.00", lines.First(l => l.Contains("2 x 1.50")).TrimEnd());
        Assert.EndsWith("5.25", lines.First(l => l.Contains("TOTAL")).TrimEnd());
        Assert.EndsWith("10.00", lines.First(l => l.Contains("Tendered")).TrimEnd());
        Assert.EndsWith("4.75", lines.First(l => l.Contains("Change")).TrimEnd());
        Assert.Contains("Thank you!", text);
        Assert.True(text.IndexOf("Cola", StringComparison.Ordinal) < text.IndexOf("TOTAL", StringComparison.Ordinal));
    }

    [Fact]
    public void A_split_payment_prints_every_part_in_order_and_the_change_of_the_cash()
    {
        // FIX-10: card 3.00 + cash 2.25 (5.00 handed over, 2.75 back)
        var receipt = Receipt() with
        {
            Payments = [new ReceiptPayment("Card (approval 4711)", 3m), new ReceiptPayment("Cash", 2.25m, 5m, 2.75m)]
        };

        var lines = Render(receipt, width: 32).Split('\n');
        var card = Array.FindIndex(lines, l => l.Contains("Card (approval 4711)"));
        var cash = Array.FindIndex(lines, l => l.Contains("Cash") && !l.Contains("Cashier"));

        Assert.True(card > Array.FindIndex(lines, l => l.Contains("TOTAL")) && cash > card);
        Assert.EndsWith("3.00", lines[card].TrimEnd());
        Assert.EndsWith("2.25", lines[cash].TrimEnd());
        Assert.EndsWith("5.00", lines[cash + 1].TrimEnd());
        Assert.EndsWith("2.75", lines[cash + 2].TrimEnd());
        Assert.Single(lines, l => l.Contains("Change"));
    }

    [Fact]
    public void NoPaymentLines_WhenThePaymentIsUnknown()
    {
        var text = Render(Receipt());

        Assert.DoesNotContain("Tendered", text);
        Assert.DoesNotContain("Change", text);
    }

    [Fact]
    public void NoLineExceedsThePrinterWidth()
    {
        var long1 = new ReceiptLine("An extremely long product description that cannot possibly fit on one printed line", 12.5m, 1234.5m, 15431.25m);
        var text = Render(Receipt(l => l.Add(long1)), width: 32);

        foreach (var line in text.Split('\n').Select(l => l.TrimEnd('\r')))
            Assert.True(line.Length <= 32 + 4, $"Line too long ({line.Length}): '{line}'");   // + ESC/POS control bytes on the same line
        Assert.Contains("12.5 x 1234.50", text);
        Assert.Contains("15431.25", text);
    }

    [Fact]
    public void NonAsciiCharacters_PrintAsQuestionMarks_NotGarbage()
    {
        var text = Render(Receipt(l => l.Add(new ReceiptLine("Café Müsli", 1, 1, 1))));

        Assert.Contains("Caf? M?sli", text);
    }

    [Fact]
    public void NewlinesInsideText_CannotInjectExtraLines_OrCommands()
    {
        var text = Render(Receipt(l => l.Add(new ReceiptLine("A\r\nB\n\u001b@C", 1, 1, 1))));

        Assert.Contains("A  B", text);
    }

    [Fact]
    public void MoneyAndQuantityFormatting_IsCultureIndependent()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1234.50", EscPosReceiptFormatter.Money(1234.5m));
            Assert.Equal("0.25", EscPosReceiptFormatter.Quantity(0.25m));
            Assert.Equal("3", EscPosReceiptFormatter.Quantity(3m));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void DrawerPulse_IsTheStandardKickCommand()
    {
        Assert.Equal([0x1B, 0x70, 0x00, 50, 250], EscPosReceiptFormatter.DrawerPulse(0, 100, 500));
        Assert.Equal([0x1B, 0x70, 0x01, 1, 255], EscPosReceiptFormatter.DrawerPulse(1, 0, 10_000));
        Assert.Equal(0x00, EscPosReceiptFormatter.DrawerPulse(7, 100, 100)[2]);
    }

    // ---- the printer adapter -----------------------------------------------------------------------------------

    [Fact]
    public async Task ThePrinter_SendsTheFormattedReceiptThroughItsTransport()
    {
        var transport = new RecordingTransport();
        var printer = new EscPosReceiptPrinter(transport, 32, true);

        var result = await printer.PrintAsync(Receipt());

        Assert.True(result.IsSuccess);
        Assert.Equal(EscPosReceiptFormatter.Format(Receipt(), 32, true), Assert.Single(transport.Sent));
    }

    [Fact]
    public async Task ATransportFailure_IsReturned_NeverThrown()
    {
        var transport = new RecordingTransport { FailWith = Result.Failure(HardwareErrors.Unavailable("printer", "cable unplugged")) };

        var result = await new EscPosReceiptPrinter(transport).PrintAsync(Receipt());

        Assert.True(result.IsFailure);
        Assert.Equal(HardwareErrors.UnavailableCode, result.Error.Code);
        Assert.Contains("cable unplugged", result.Error.Description);
    }

    [Fact]
    public async Task AnEmptyReceipt_IsInvalidData_AndSendsNothing()
    {
        var transport = new RecordingTransport();
        var printer = new EscPosReceiptPrinter(transport);

        var empty = Receipt() with { Lines = [] };
        var result = await printer.PrintAsync(empty);

        Assert.Equal(HardwareErrors.InvalidDataCode, result.Error.Code);
        Assert.Empty(transport.Sent);
        Assert.Equal(HardwareErrors.InvalidDataCode, (await printer.PrintAsync(null!)).Error.Code);
    }

    [Fact]
    public async Task StatusComesFromTheTransport()
    {
        var transport = new RecordingTransport { Status = DeviceStatus.Unavailable("offline") };
        var printer = new EscPosReceiptPrinter(transport);

        Assert.Equal(DeviceState.Unavailable, (await printer.GetStatusAsync()).State);
        transport.Status = DeviceStatus.Ready();
        Assert.True((await printer.GetStatusAsync()).IsReady);
    }

    [Fact]
    public async Task TheDrawer_SendsTheKickPulse_AndReportsFailures()
    {
        var transport = new RecordingTransport();
        var drawer = new EscPosCashDrawer(transport, pin: 1, onMilliseconds: 120, offMilliseconds: 240);

        Assert.True((await drawer.OpenAsync()).IsSuccess);
        Assert.Equal([0x1B, 0x70, 0x01, 60, 120], Assert.Single(transport.Sent));

        transport.FailWith = Result.Failure(HardwareErrors.Timeout("drawer"));
        Assert.Equal(HardwareErrors.TimeoutCode, (await drawer.OpenAsync()).Error.Code);
    }
}

public sealed class ZplLabelTests
{
    private static string Render(LabelDocument l, int w = 406, int h = 203) => Encoding.UTF8.GetString(ZplLabelFormatter.Format(l, w, h));

    [Fact]
    public void ALabel_HasNameBarcodePriceAndCopies()
    {
        var zpl = Render(new LabelDocument("Orange Juice", "SKU-9", 3.5m, 4));

        Assert.StartsWith("^XA", zpl);
        Assert.EndsWith("^XZ", zpl);
        Assert.Contains("^PW406", zpl);
        Assert.Contains("^LL203", zpl);
        Assert.Contains("^FDOrange Juice^FS", zpl);
        Assert.Contains("^BCN,70,Y,N,N^FDSKU-9^FS", zpl);
        Assert.Contains("^FD3.50^FS", zpl);
        Assert.Contains("^PQ4", zpl);
    }

    [Fact]
    public void ThePriceIsOptional()
        => Assert.DoesNotContain("^A0N,34,34", Render(new LabelDocument("Thing", "X1", null)));

    [Fact]
    public void LongNamesAreTruncated_AndPrinterCommandCharactersAreNeutralised()
    {
        var zpl = Render(new LabelDocument(new string('N', 80) + "^XZ~JA", "ABC^D~E", 1m));

        Assert.Contains("^FD" + new string('N', ZplLabelFormatter.MaxNameLength) + "^FS", zpl);
        Assert.Contains("^FDABC D E^FS", zpl);
        Assert.Equal(1, zpl.Split("^XZ").Length - 1);       // only the real end-of-label command
        Assert.DoesNotContain("~JA", zpl);
    }

    [Theory]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("café", 1)]
    [InlineData("A\tB", 1)]
    [InlineData("OK", 0)]
    [InlineData("OK", 101)]
    public void InvalidLabels_AreRejectedBeforeAnythingIsSent(string code, int copies)
        => Assert.NotNull(ZplLabelFormatter.Validate(new LabelDocument("x", code, 1m, copies)));

    [Fact]
    public void AValidLabel_PassesValidation()
    {
        Assert.Null(ZplLabelFormatter.Validate(new LabelDocument("x", "SKU-9", 1m, 1)));
        Assert.Null(ZplLabelFormatter.Validate(new LabelDocument("x", "SKU-9", null, ZplLabelFormatter.MaxCopies)));
    }

    [Fact]
    public async Task ThePrinter_SendsValidLabels_AndRefusesInvalidOnes()
    {
        var transport = new RecordingTransport();
        var printer = new ZplLabelPrinter(transport);

        Assert.True((await printer.PrintAsync(new LabelDocument("Juice", "SKU-9", 3.5m))).IsSuccess);
        var invalid = await printer.PrintAsync(new LabelDocument("Juice", "", 3.5m));
        var nothing = await printer.PrintAsync(null!);

        Assert.Single(transport.Sent);
        Assert.Equal(HardwareErrors.InvalidDataCode, invalid.Error.Code);
        Assert.Equal(HardwareErrors.InvalidDataCode, nothing.Error.Code);
    }

    [Fact]
    public async Task ATransportFailure_IsReturned()
    {
        var transport = new RecordingTransport { FailWith = Result.Failure(HardwareErrors.Unavailable("label printer")) };

        var result = await new ZplLabelPrinter(transport).PrintAsync(new LabelDocument("Juice", "SKU-9", 3.5m));

        Assert.Equal(HardwareErrors.UnavailableCode, result.Error.Code);
    }
}
