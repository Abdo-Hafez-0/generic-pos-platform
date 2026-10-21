using Client.Hardware.Printing;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Codes;

namespace Hardware.Tests;

/// <summary>
/// MISS-11c-2 (owner's choice: large and centred under the totals): the electronic receipt's QR code on the printed receipt - laid out after
/// the payments with its title and caption, drawn as whole-dot modules about 30 mm wide, sent as a raster picture on a text receipt and
/// handed to the picture renderer on an Arabic one.
/// </summary>
public sealed class ReceiptQrTests
{
    private const string Qr = "https://invoicing.eta.gov.eg/receipts/search/095621f0df4130b0f8b5d65bf47f34727afed7721bb4b4987150af33ccda5f21/share/2026-10-21T11:47:44Z#Total:3.00,IssuerRIN:123456789";

    private static ReceiptDocument Receipt(string title = "E-receipt")
        => new("Corner Shop", [], "S-ABCD1234", new DateTimeOffset(2026, 10, 21, 14, 30, 0, TimeSpan.Zero), "cashier-1",
            [new ReceiptLine("Water 1.5L", 2m, 1.5m, 3m)], 3m, new ReceiptPayment("Cash", 3m), ["Thank you!"],
            Fiscal: new ReceiptFiscal(Qr, title, "UUID 095621f0...ccda5f21"));

    private static bool Dot(MonochromeImage image, int row, int column)
        => (image.Rows[row * (image.Width / 8) + column / 8] & (0x80 >> (column % 8))) != 0;

    /// <summary>Reads the code back from the picture: the centre dot of every module.</summary>
    private static void AssertDrawsTheCode(MonochromeImage image, int firstRow = 0)
    {
        var code = QrCode.Encode(Qr);
        var dot = ReceiptImageLine.QrModuleDots(code.Size, image.Width);
        var left = (image.Width - (code.Size + 8) * dot) / 2 + 4 * dot;
        for (var y = 0; y < code.Size; y++)
            for (var x = 0; x < code.Size; x++)
                Assert.True(code[y, x] == Dot(image, firstRow + (y + 4) * dot + dot / 2, left + x * dot + dot / 2), $"module ({y},{x})");
    }

    [Fact]
    public void The_QR_its_title_and_caption_come_after_the_payments_and_before_the_footer()
    {
        var lines = EscPosReceiptFormatter.Layout(Receipt("إيصال إلكتروني"), 42);

        var qr = lines.ToList().FindIndex(l => l.QrPayload is not null);
        Assert.True(qr > lines.ToList().FindIndex(l => l.Left == "Cash"));
        Assert.True(lines[qr - 1].Rule);
        Assert.Equal(Qr, lines[qr].QrPayload);
        Assert.Equal(("إيصال إلكتروني", true, true), (lines[qr + 1].Left, lines[qr + 1].Bold, lines[qr + 1].Center));
        Assert.Equal(("UUID 095621f0...ccda5f21", true), (lines[qr + 2].Left, lines[qr + 2].Center));
        Assert.Equal("Thank you!", lines[qr + 3].Left);

        Assert.DoesNotContain(EscPosReceiptFormatter.Layout(Receipt() with { Fiscal = null }, 42), l => l.QrPayload is not null);
    }

    [Theory]
    [InlineData(576)]   // 80 mm paper
    [InlineData(384)]   // 58 mm paper
    public void The_code_is_drawn_in_whole_dots_about_30_mm_wide_and_centred(int width)
    {
        var image = QrRaster.Draw(Qr, width);
        var code = QrCode.Encode(Qr);
        var dot = ReceiptImageLine.QrModuleDots(code.Size, width);

        Assert.Equal(width, image.Width);
        Assert.Equal((code.Size + 8) * dot, image.Height);
        Assert.InRange(image.Height, 216, 264);   // 27-33 mm at 8 dots/mm, the quiet zone included (about 30 mm)
        AssertDrawsTheCode(image);
        for (var row = 0; row < 4 * dot; row++)                        // the quiet zone stays white
            Assert.All(Enumerable.Range(0, width), column => Assert.False(Dot(image, row, column)));
    }

    [Fact]
    public void A_text_receipt_sends_the_code_as_a_raster_picture_between_its_lines()
    {
        var bytes = EscPosReceiptFormatter.Format(Receipt(), 42, cutPaper: false, renderer: null);

        var at = -1;
        for (var i = 0; i + 4 <= bytes.Length && at < 0; i++)
            if (bytes[i] == 0x1D && bytes[i + 1] == 0x76 && bytes[i + 2] == 0x30 && bytes[i + 3] == 0x00) at = i;
        Assert.True(at > 0, "no raster picture");
        var (bytesPerRow, rows) = (bytes[at + 4] + bytes[at + 5] * 256, bytes[at + 6] + bytes[at + 7] * 256);
        Assert.Equal(576 / 8, bytesPerRow);

        // the picture's bands joined back together draw the code
        var picture = new List<byte>();
        var total = QrRaster.Draw(Qr, 576).Height;
        for (int i = at, left = total; left > 0; )
        {
            var band = bytes[i + 6] + bytes[i + 7] * 256;
            picture.AddRange(bytes.AsSpan(i + 8, bytesPerRow * band).ToArray());
            i += 8 + bytesPerRow * band;
            left -= band;
        }

        AssertDrawsTheCode(new MonochromeImage(576, total, [.. picture]));
        var text = System.Text.Encoding.ASCII.GetString(bytes);
        Assert.Contains("E-receipt", text);
        Assert.Contains("UUID 095621f0...ccda5f21", text);
    }

    [Fact]
    public void An_Arabic_title_makes_the_receipt_a_picture_and_the_renderer_gets_the_code()
    {
        var renderer = new RecordingRenderer();
        EscPosReceiptFormatter.Format(Receipt("إيصال إلكتروني"), 42, cutPaper: false, renderer);

        var lines = Assert.Single(renderer.Calls);
        Assert.Contains(lines, l => l.QrPayload == Qr);
    }

    private sealed class RecordingRenderer : IReceiptImageRenderer
    {
        public List<IReadOnlyList<ReceiptImageLine>> Calls { get; } = [];

        public MonochromeImage Render(IReadOnlyList<ReceiptImageLine> lines, int widthDots)
        {
            Calls.Add(lines);
            return new MonochromeImage(widthDots, 8, new byte[widthDots / 8 * 8]);
        }
    }
}
