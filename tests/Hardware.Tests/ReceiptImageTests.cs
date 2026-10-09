using System.Text;
using Client.Hardware.Printing;
using Platform.Application.Abstractions.Hardware;

namespace Hardware.Tests;

/// <summary>
/// FIX-13a: a receipt whose texts need more than ASCII (Arabic, accents ...) is drawn by an <see cref="IReceiptImageRenderer"/> and sent as a
/// raster picture (GS v 0); ASCII receipts stay text. The real renderer (WPF fonts) is tested in UI.Tests.
/// </summary>
public sealed class ReceiptImageTests
{
    private sealed class FakeRenderer(int height = 40, bool fail = false) : IReceiptImageRenderer
    {
        public List<(IReadOnlyList<ReceiptImageLine> Lines, int Width)> Calls { get; } = [];

        public MonochromeImage Render(IReadOnlyList<ReceiptImageLine> lines, int widthDots)
        {
            if (fail) throw new InvalidOperationException("no fonts");
            Calls.Add((lines, widthDots));
            var rows = new byte[widthDots / 8 * height];
            Array.Fill(rows, (byte)0xAA);
            return new MonochromeImage(widthDots, height, rows);
        }
    }

    private static ReceiptDocument Receipt(string product = "Water 1.5L", string? store = "Corner Shop")
        => new(store, [], "S-ABCD1234", new DateTimeOffset(2026, 10, 9, 14, 30, 0, TimeSpan.Zero), "cashier-1",
            [new ReceiptLine(product, 2m, 1.5m, 3m)], 3m, new ReceiptPayment("Cash", 3m), ["Thank you!"]);

    private static int Find(byte[] haystack, byte[] needle, int from = 0)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    private static List<(int BytesPerRow, int Rows)> RasterCommands(byte[] bytes)
    {
        var found = new List<(int, int)>();
        for (var i = Find(bytes, [0x1D, 0x76, 0x30, 0x00]); i >= 0; i = Find(bytes, [0x1D, 0x76, 0x30, 0x00], i + 1))
        {
            var (x, y) = (bytes[i + 4] + bytes[i + 5] * 256, bytes[i + 6] + bytes[i + 7] * 256);
            found.Add((x, y));
            i += 7 + x * y;
        }

        return found;
    }

    [Fact]
    public void An_Arabic_product_name_makes_the_receipt_a_picture_of_every_line()
    {
        var renderer = new FakeRenderer();
        var bytes = EscPosReceiptFormatter.Format(Receipt("مياه معدنية ١٫٥ لتر"), 42, cutPaper: true, renderer);

        var call = Assert.Single(renderer.Calls);
        Assert.Equal(576, call.Width);
        Assert.Contains(call.Lines, l => l.Left == "مياه معدنية ١٫٥ لتر");
        Assert.Contains(call.Lines, l => l is { Left: "TOTAL", Right: "3.00", Bold: true });
        Assert.Contains(call.Lines, l => l is { Left: "Corner Shop", Center: true });
        Assert.Equal([(72, 40)], RasterCommands(bytes));
        Assert.Equal(-1, Find(bytes, Encoding.ASCII.GetBytes("TOTAL")));   // nothing of it went as text
        Assert.Equal([0x1B, 0x40], bytes[..2]);
        Assert.Equal([0x1D, 0x56, 0x42, 0x00], bytes[^4..]);
    }

    [Fact]
    public void An_ASCII_receipt_stays_text_even_with_a_renderer()
    {
        var renderer = new FakeRenderer();
        var text = Encoding.ASCII.GetString(EscPosReceiptFormatter.Format(Receipt(), 42, cutPaper: false, renderer));

        Assert.Empty(renderer.Calls);
        Assert.Contains("TOTAL", text);
        Assert.Contains("Water 1.5L", text);
    }

    [Theory]
    [InlineData(ReceiptImageMode.Always, "Water 1.5L", true)]
    [InlineData(ReceiptImageMode.Never, "Café crème", false)]
    public void The_mode_can_force_a_picture_or_text(ReceiptImageMode mode, string product, bool picture)
    {
        var renderer = new FakeRenderer();
        var bytes = EscPosReceiptFormatter.Format(Receipt(product), 42, cutPaper: false, renderer, mode);

        Assert.Equal(picture, renderer.Calls.Count == 1);
        if (!picture) Assert.Contains("Caf? cr?me", Encoding.ASCII.GetString(bytes));   // the earlier behaviour, on request
    }

    [Fact]
    public void Without_a_renderer_non_ASCII_still_prints_as_question_marks()
        => Assert.Contains("????", Encoding.ASCII.GetString(EscPosReceiptFormatter.Format(Receipt("مياه"), 42, cutPaper: false)));

    [Fact]
    public void A_tall_picture_is_sent_in_bands_and_a_58mm_width_is_honoured()
    {
        var renderer = new FakeRenderer(height: 300);
        var bytes = EscPosReceiptFormatter.Format(Receipt("مياه"), 32, cutPaper: false, renderer, dotsPerLine: 384);

        Assert.Equal([(48, 128), (48, 128), (48, 44)], RasterCommands(bytes));
        Assert.Equal(384, renderer.Calls[0].Width);
    }

    [Fact]
    public async Task A_renderer_that_fails_does_not_lose_the_receipt_it_is_printed_as_text()
    {
        var transport = new RecordingTransport();
        var printer = new EscPosReceiptPrinter(transport, imageRenderer: new FakeRenderer(fail: true));

        var result = await printer.PrintAsync(Receipt("مياه"));

        Assert.True(result.IsSuccess);
        var text = Encoding.ASCII.GetString(Assert.Single(transport.Sent));
        Assert.Contains("TOTAL", text);
        Assert.Empty(RasterCommands(transport.Sent[0]));
    }

    [Fact]
    public async Task The_factory_reads_the_picture_settings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"receipt-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, []);
        try
        {
            var renderer = new FakeRenderer();
            var printer = Client.Hardware.HardwareFactory.CreateReceiptPrinter(
                new Client.Hardware.Configuration.ReceiptPrinterOptions { Type = "EscPosFile", Path = path, PrintAsImage = "always", DotsPerLine = 384 },
                imageRenderer: renderer);

            Assert.True((await printer.PrintAsync(Receipt())).IsSuccess);
            Assert.Equal(384, Assert.Single(renderer.Calls).Width);
            Assert.Equal([(48, 40)], RasterCommands(File.ReadAllBytes(path)));
        }
        finally { File.Delete(path); }
    }
}
