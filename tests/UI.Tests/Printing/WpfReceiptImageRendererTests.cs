using Client.Desktop.Printing;
using Platform.Application.Abstractions.Hardware;

namespace UI.Tests.Printing;

/// <summary>FIX-13a: the desktop's receipt picture - real WPF fonts, Arabic shaped and placed right to left, reduced to printer dots.</summary>
public sealed class WpfReceiptImageRendererTests
{
    private static readonly WpfReceiptImageRenderer Renderer = new();

    /// <summary>The leftmost and rightmost black dot of a row band (null when the band is blank).</summary>
    private static (int Left, int Right)? Ink(MonochromeImage image, int fromRow, int toRow)
    {
        var bytesPerRow = image.Width / 8;
        int? left = null, right = null;
        for (var row = fromRow; row < Math.Min(toRow, image.Height); row++)
            for (var column = 0; column < image.Width; column++)
                if ((image.Rows[row * bytesPerRow + column / 8] & (0x80 >> (column % 8))) != 0)
                {
                    left = Math.Min(left ?? column, column);
                    right = Math.Max(right ?? column, column);
                }

        return left is null ? null : (left.Value, right!.Value);
    }

    private static int LineHeight(int width) => (int)Math.Ceiling(width / 24.0 * 1.3);

    [Fact]
    public void The_picture_has_the_paper_width_and_a_row_of_text_per_line()
    {
        var image = Renderer.Render([new ReceiptImageLine("TOTAL", "7.50", Bold: true), new ReceiptImageLine("Cash", "7.50")], 576);

        Assert.Equal(576, image.Width);
        Assert.Equal(576 / 8 * image.Height, image.Rows.Length);
        Assert.InRange(image.Height, 2 * LineHeight(576), 2 * LineHeight(576) + 10);
        Assert.NotNull(Ink(image, 0, image.Height));
    }

    [Fact]
    public void An_Arabic_line_without_an_amount_is_drawn_on_the_right_and_an_English_one_on_the_left()
    {
        var h = LineHeight(576);
        var image = Renderer.Render([new ReceiptImageLine("مياه معدنية"), new ReceiptImageLine("Mineral water")], 576);

        var arabic = Ink(image, 0, 4 + h)!.Value;
        var english = Ink(image, 4 + h, 4 + 2 * h)!.Value;

        Assert.True(arabic.Left > 576 / 2, $"the Arabic text starts at {arabic.Left}");
        Assert.True(arabic.Right > 576 - 20, $"the Arabic text ends at {arabic.Right}");
        Assert.True(english.Left < 20 && english.Right < 576 / 2, $"the English text spans {english}");
    }

    [Fact]
    public void An_amount_ends_at_the_right_edge_and_a_centred_line_is_centred()
    {
        var h = LineHeight(576);
        var image = Renderer.Render([new ReceiptImageLine("مياه معدنية", "3.00"), new ReceiptImageLine("شكراً لزيارتكم", Center: true)], 576);

        var priced = Ink(image, 0, 4 + h)!.Value;
        var centred = Ink(image, 4 + h, 4 + 2 * h)!.Value;

        Assert.True(priced.Left < 20 && priced.Right > 576 - 20, $"name on the left and amount on the right: {priced}");
        Assert.InRange((centred.Left + centred.Right) / 2, 576 / 2 - 12, 576 / 2 + 12);
    }

    [Fact]
    public void A_separator_is_a_solid_line_across_the_paper()
    {
        var image = Renderer.Render([new ReceiptImageLine("", Rule: true)], 384);

        var rule = Ink(image, 0, image.Height)!.Value;
        Assert.Equal(384, image.Width);
        Assert.True(rule.Left <= 4 && rule.Right >= 384 - 5, $"the rule spans {rule}");
    }

    [Fact]
    public void A_QR_line_draws_the_code_in_whole_dots_centred_under_the_text_above_it()
    {
        // MISS-11c-2 (owner's choice: large and centred): the picture of an Arabic receipt carries the electronic receipt's QR code
        const string qr = "https://invoicing.eta.gov.eg/receipts/search/095621f0df4130b0f8b5d65bf47f34727afed7721bb4b4987150af33ccda5f21/share/2026-10-21T11:47:44Z#Total:1.00,IssuerRIN:123456789";
        var image = Renderer.Render([new ReceiptImageLine("TOTAL", "1.00", Bold: true), new ReceiptImageLine("", Center: true, QrPayload: qr),
            new ReceiptImageLine("إيصال إلكتروني", Bold: true, Center: true)], 576);

        var code = Platform.Core.Codes.QrCode.Encode(qr);
        var dot = ReceiptImageLine.QrModuleDots(code.Size, 576);
        var top = 4 + LineHeight(576);
        var left = (576 - (code.Size + 8) * dot) / 2 + 4 * dot;
        Assert.Equal(4 + 2 * LineHeight(576) + (code.Size + 8) * dot + 4, image.Height, tolerance: 1);
        for (var y = 0; y < code.Size; y++)
            for (var x = 0; x < code.Size; x++)
            {
                var (row, column) = (top + (y + 4) * dot + dot / 2, left + x * dot + dot / 2);
                Assert.True(code[y, x] == ((image.Rows[row * 72 + column / 8] & (0x80 >> (column % 8))) != 0), $"module ({y},{x})");
            }

        Assert.NotNull(Ink(image, top + (code.Size + 8) * dot, image.Height));   // the title under the code
    }

    [Theory]
    [InlineData("مياه", true)]
    [InlineData("123 مياه", true)]
    [InlineData("Water مياه", false)]
    [InlineData("7.50", false)]
    public void The_direction_is_decided_by_the_first_letter(string text, bool rightToLeft)
        => Assert.Equal(rightToLeft, WpfReceiptImageRenderer.IsRightToLeft(text));
}
