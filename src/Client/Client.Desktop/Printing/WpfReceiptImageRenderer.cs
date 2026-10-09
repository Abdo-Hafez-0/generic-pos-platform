using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Platform.Application.Abstractions.Hardware;

namespace Client.Desktop.Printing;

/// <summary>
/// Draws receipt lines with a Windows font so that any script prints correctly (FIX-13a): WPF shapes Arabic (joined letters) and orders
/// mixed right-to-left / left-to-right text, then the picture is reduced to black and white dots for the printer (ESC/POS GS v 0).
///
/// Layout: a line's <c>Left</c> starts at the left edge, its amount (<c>Right</c>) ends at the right edge; a line written in a right-to-left
/// script with no amount (a product name, the store name in Arabic) is aligned to the right; centred lines stay centred. Each text keeps its
/// own direction. Printing runs on a background thread, so the drawing happens on a short-lived STA thread of its own.
/// </summary>
public sealed class WpfReceiptImageRenderer : IReceiptImageRenderer
{
    /// <summary>A font with Arabic and Latin letters on every Windows installation.</summary>
    public const string FontFamilyName = "Arial";

    public MonochromeImage Render(IReadOnlyList<ReceiptImageLine> lines, int widthDots)
    {
        MonochromeImage? image = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { image = Draw(lines, widthDots); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (failure is not null) throw new InvalidOperationException("The receipt could not be drawn.", failure);
        return image!;
    }

    private static MonochromeImage Draw(IReadOnlyList<ReceiptImageLine> lines, int widthDots)
    {
        var width = Math.Max(8, widthDots / 8 * 8);
        var fontSize = width / 24.0;                     // 24 dots on 576 (80 mm): readable, about 42 characters a line
        var lineHeight = Math.Ceiling(fontSize * 1.3);
        var margin = 4.0;
        var height = (int)Math.Max(1, Math.Ceiling(lines.Count * lineHeight + 2 * margin));

        var visual = new DrawingVisual();
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Aliased);   // crisp edges: the picture becomes pure black and white
        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var y = margin;
            foreach (var line in lines)
            {
                if (line.Rule)
                {
                    dc.DrawRectangle(Brushes.Black, null, new Rect(margin, y + lineHeight / 2 - 1, width - 2 * margin, 2));
                }
                else
                {
                    var right = string.IsNullOrEmpty(line.Right) ? null : Text(line.Right, fontSize, line.Bold, width);
                    var room = width - 2 * margin - (right is null ? 0 : right.WidthIncludingTrailingWhitespace + fontSize);
                    var left = Text(line.Left, fontSize, line.Bold, Math.Max(fontSize, room));

                    double x;
                    if (line.Center) x = (width - left.Width) / 2;
                    else if (right is null && IsRightToLeft(line.Left)) x = width - margin - left.Width;
                    else x = margin;
                    dc.DrawText(left, new Point(x, y));

                    if (right is not null) dc.DrawText(right, new Point(width - margin - right.Width, y));
                }

                y += lineHeight;
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);

        var rows = new byte[width / 8 * height];
        for (var row = 0; row < height; row++)
            for (var column = 0; column < width; column++)
            {
                var i = (row * width + column) * 4;
                var luminance = (pixels[i] * 114 + pixels[i + 1] * 587 + pixels[i + 2] * 299) / 1000;   // B, G, R
                if (luminance < 128) rows[row * (width / 8) + column / 8] |= (byte)(0x80 >> (column % 8));
            }

        return new MonochromeImage(width, height, rows);
    }

    private static FormattedText Text(string text, double fontSize, bool bold, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture,
            IsRightToLeft(text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
            new Typeface(new FontFamily(FontFamilyName), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            fontSize, Brushes.Black, 1.0)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        // the layout box is the text itself: right-to-left text is aligned inside its box, so a box wider than the text would move it
        formatted.MaxTextWidth = Math.Min(maxWidth, Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 1);
        return formatted;
    }

    /// <summary>True when the first letter with a direction is right-to-left (Arabic, Hebrew ...): digits and punctuation do not decide.</summary>
    public static bool IsRightToLeft(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return true;
            if (char.IsLetter(c)) return false;
        }

        return false;
    }
}
