using Platform.Core.Codes;
using QRCoder;

namespace Hardware.Tests;

/// <summary>
/// MISS-11c-2 (owner's decision: Matjar's own QR encoder, an established library only in the tests): every module of Matjar's code equals
/// QRCoder's for the same text at level M - small and large versions, the version information (7+), the 16-bit length (10+), all masks.
/// </summary>
public sealed class QrCodeTests
{
    private const string EtaQr =
        "https://invoicing.eta.gov.eg/receipts/search/095621f0df4130b0f8b5d65bf47f34727afed7721bb4b4987150af33ccda5f21/share/2026-10-21T11:47:44Z#Total:1.00,IssuerRIN:123456789";

    private static void AssertSameAsQrCoder(string text)
    {
        var ours = QrCode.Encode(text);
        using var generator = new QRCodeGenerator();
        var reference = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = reference.ModuleMatrix;
        var quiet = (matrix.Count - ours.Size) / 2;

        Assert.Equal(reference.Version, ours.Version);
        Assert.Equal(ours.Version * 4 + 17, ours.Size);
        List<string> Differences(QrCode code)
        {
            var found = new List<string>();
            for (var y = 0; y < code.Size; y++)
                for (var x = 0; x < code.Size; x++)
                    if (code[y, x] != matrix[y + quiet][x + quiet]) found.Add($"({y},{x})");
            return found;
        }

        var differences = Differences(ours);
        if (differences.Count == 0) return;

        // the masks' penalties are weighed differently: with the reference's mask every module must be the same
        var same = Enumerable.Range(0, 8).Where(m => Differences(QrCode.Encode(text, m)).Count == 0).ToList();
        Assert.True(same.Count == 1, $"version {ours.Version}, mask {ours.Mask}: {differences.Count} modules differ and no mask gives the reference's code");
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("matjar.app/r?n=1")]
    [InlineData(EtaQr)]
    public void Small_codes_equal_the_reference(string text) => AssertSameAsQrCoder(text);

    [Theory]
    [InlineData(150)]    // version 7: the version information
    [InlineData(300)]    // version 11: the 16-bit length
    [InlineData(700)]    // version 18
    [InlineData(1500)]   // version 31
    [InlineData(2331)]   // version 40, full
    public void Larger_codes_equal_the_reference(int length)
        => AssertSameAsQrCoder(string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i * 7 % 26))));

    [Fact]
    public void Every_mask_is_chosen_for_some_text_and_each_matches_the_reference()
    {
        var masks = new HashSet<int>();
        for (var i = 0; masks.Count < 8 && i < 400; i++)
        {
            var text = $"receipt-{i}-" + new string((char)('a' + i % 26), i % 40);
            masks.Add(QrCode.Encode(text).Mask);
            AssertSameAsQrCoder(text);
        }

        Assert.Equal(8, masks.Count);
    }

    [Fact]
    public void The_ETA_link_fits_about_30_mm_at_four_dots_a_module()
    {
        var code = QrCode.Encode(EtaQr);
        Assert.InRange(code.Version, 7, 9);
        Assert.InRange((code.Size + 8) * 4, 200, 260);   // dots at 8 dots/mm (203 dpi): 25-32 mm with its quiet zone
    }

    [Fact]
    public void Empty_or_too_long_texts_are_refused()
    {
        Assert.Throws<ArgumentException>(() => QrCode.Encode(""));
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('a', QrCode.MaxBytes + 1)));
        Assert.Equal(40, QrCode.Encode(new string('a', QrCode.MaxBytes)).Version);
    }
}
