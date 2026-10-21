using System.Text;

namespace Platform.Core.Codes;

/// <summary>
/// MISS-11c-2 (owner's decision: Matjar's own encoder, no package): a QR code (ISO/IEC 18004, model 2) for a text - byte mode (the text's
/// UTF-8 bytes), error correction level M, the smallest version (1-40) that holds it, the mask with the lowest penalty. Pure: the receipt
/// printer and the screen draw the modules. The tests check it module for module against an established library.
/// </summary>
public sealed class QrCode
{
    private readonly bool[,] _modules;

    private QrCode(int version, int mask, bool[,] modules) => (Version, Mask, _modules) = (version, mask, modules);

    /// <summary>1 to 40: the code is 17 + 4 x version modules square.</summary>
    public int Version { get; }

    /// <summary>The data mask chosen (0-7).</summary>
    public int Mask { get; }

    /// <summary>Modules per side (without the quiet zone of 4 modules the code needs around it).</summary>
    public int Size => _modules.GetLength(0);

    /// <summary>True = dark module, at row <paramref name="y"/>, column <paramref name="x"/>.</summary>
    public bool this[int y, int x] => _modules[y, x];

    /// <summary>The largest text (in UTF-8 bytes) level M holds: version 40.</summary>
    public const int MaxBytes = 2331;

    // Level M: error-correction codewords per block and the number of blocks, by version (index 0 unused) - ISO/IEC 18004 table 9.
    private static readonly int[] EccPerBlock =
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28];
    private static readonly int[] Blocks =
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49];

    private const int FormatLevelM = 0;   // the two error-correction bits of the format information: L 01, M 00, Q 11, H 10

    /// <summary>The QR code of <paramref name="text"/>; an empty or too long text is refused.</summary>
    public static QrCode Encode(string text) => Encode(text, null);

    /// <summary>With <paramref name="forcedMask"/>: that mask instead of the one with the lowest penalty (the tests compare with libraries
    /// that weigh the penalty differently - any mask gives a valid code).</summary>
    internal static QrCode Encode(string text, int? forcedMask)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var data = Encoding.UTF8.GetBytes(text);
        if (data.Length > MaxBytes) throw new ArgumentException($"A QR code at level M holds at most {MaxBytes} bytes.", nameof(text));

        var version = 1;
        while (DataBits(version, data.Length) > DataCodewords(version) * 8) version++;

        var codewords = AddErrorCorrection(Bits(version, data), version);
        var size = version * 4 + 17;
        var modules = new bool[size, size];
        var isFunction = new bool[size, size];
        DrawFunctionPatterns(modules, isFunction, version);
        DrawCodewords(modules, isFunction, codewords);

        // the mask with the lowest penalty (ties: the lower mask number)
        var (best, bestPenalty) = (0, int.MaxValue);
        for (var mask = 0; mask < 8; mask++)
        {
            ApplyMask(modules, isFunction, mask);
            DrawFormatBits(modules, mask);
            var penalty = Penalty(modules);
            if (penalty < bestPenalty) (best, bestPenalty) = (mask, penalty);
            ApplyMask(modules, isFunction, mask);   // undo (a mask is its own inverse)
        }

        if (forcedMask is { } forced) best = forced;
        ApplyMask(modules, isFunction, best);
        DrawFormatBits(modules, best);
        return new QrCode(version, best, modules);
    }

    // ---- the data

    private static int CountBits(int version) => version <= 9 ? 8 : 16;

    private static int DataBits(int version, int length) => 4 + CountBits(version) + length * 8;

    /// <summary>Modules that carry data (codewords and remainder bits) in a version: all but the function patterns.</summary>
    private static int RawDataModules(int version)
    {
        var result = (16 * version + 128) * version + 64;
        if (version >= 2)
        {
            var alignments = version / 7 + 2;
            result -= (25 * alignments - 10) * alignments - 55;
            if (version >= 7) result -= 36;
        }

        return result;
    }

    private static int DataCodewords(int version) => RawDataModules(version) / 8 - EccPerBlock[version] * Blocks[version];

    /// <summary>Mode (byte), length, the bytes, the terminator and the pad codewords 0xEC 0x11.</summary>
    private static byte[] Bits(int version, byte[] data)
    {
        var capacity = DataCodewords(version) * 8;
        var bits = new List<bool>(capacity);
        void Append(int value, int count) { for (var i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }

        Append(0b0100, 4);
        Append(data.Length, CountBits(version));
        foreach (var b in data) Append(b, 8);
        Append(0, Math.Min(4, capacity - bits.Count));
        Append(0, (8 - bits.Count % 8) % 8);
        for (var pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(pad, 8);

        var bytes = new byte[bits.Count / 8];
        for (var i = 0; i < bits.Count; i++) if (bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        return bytes;
    }

    /// <summary>Splits the data into the version's blocks, adds each block's Reed-Solomon codewords and interleaves them.</summary>
    private static byte[] AddErrorCorrection(byte[] data, int version)
    {
        var blocks = Blocks[version];
        var ecc = EccPerBlock[version];
        var raw = RawDataModules(version) / 8;
        var shortBlocks = blocks - raw % blocks;
        var shortLength = raw / blocks;                       // a short block's codewords, data and error correction together
        var divisor = ReedSolomonDivisor(ecc);

        var all = new List<byte[]>();
        for (int i = 0, k = 0; i < blocks; i++)
        {
            var dataLength = shortLength - ecc + (i < shortBlocks ? 0 : 1);
            var block = data[k..(k + dataLength)];
            k += dataLength;
            var full = new byte[shortLength + 1];
            block.CopyTo(full, 0);
            ReedSolomonRemainder(block, divisor).CopyTo(full, shortLength + 1 - ecc);
            all.Add(full);
        }

        var result = new List<byte>(raw);
        for (var i = 0; i < shortLength + 1; i++)
            for (var j = 0; j < all.Count; j++)
                if (i != shortLength - ecc || j >= shortBlocks)   // short blocks have no codeword in the long blocks' last data place
                    result.Add(all[j][i]);
        return [.. result];
    }

    private static byte[] ReedSolomonDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        var root = 1;
        for (var i = 0; i < degree; i++)
        {
            for (var j = 0; j < result.Length; j++)
            {
                result[j] = Multiply(result[j], root);
                if (j + 1 < result.Length) result[j] ^= result[j + 1];
            }

            root = Multiply(root, 0x02);
        }

        return result;
    }

    private static byte[] ReedSolomonRemainder(byte[] data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (var b in data)
        {
            var factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (var i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
        }

        return result;
    }

    /// <summary>Multiplication in GF(2^8) modulo x^8 + x^4 + x^3 + x^2 + 1.</summary>
    private static byte Multiply(int x, int y)
    {
        var z = 0;
        for (var i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }

        return (byte)z;
    }

    // ---- the drawing

    private static void Set(bool[,] modules, bool[,] isFunction, int x, int y, bool dark) { modules[y, x] = dark; isFunction[y, x] = true; }

    private static void DrawFunctionPatterns(bool[,] modules, bool[,] isFunction, int version)
    {
        var size = modules.GetLength(0);
        for (var i = 0; i < size; i++)   // timing patterns
        {
            Set(modules, isFunction, 6, i, i % 2 == 0);
            Set(modules, isFunction, i, 6, i % 2 == 0);
        }

        DrawFinder(modules, isFunction, 3, 3);
        DrawFinder(modules, isFunction, size - 4, 3);
        DrawFinder(modules, isFunction, 3, size - 4);

        var positions = AlignmentPositions(version);
        var n = positions.Length;
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                if (!(i == 0 && j == 0 || i == 0 && j == n - 1 || i == n - 1 && j == 0))   // not over the finders
                    for (var dy = -2; dy <= 2; dy++)
                        for (var dx = -2; dx <= 2; dx++)
                            Set(modules, isFunction, positions[i] + dx, positions[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);

        DrawFormatBits(modules, 0, isFunction);   // reserves the places; the real bits come with the mask
        if (version >= 7)
        {
            var remainder = version;
            for (var i = 0; i < 12; i++) remainder = (remainder << 1) ^ ((remainder >> 11) * 0x1F25);
            var bits = version << 12 | remainder;
            for (var i = 0; i < 18; i++)
            {
                var dark = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3, b = i / 3;
                Set(modules, isFunction, a, b, dark);
                Set(modules, isFunction, b, a, dark);
            }
        }
    }

    private static void DrawFinder(bool[,] modules, bool[,] isFunction, int x, int y)
    {
        var size = modules.GetLength(0);
        for (var dy = -4; dy <= 4; dy++)
            for (var dx = -4; dx <= 4; dx++)
            {
                int xx = x + dx, yy = y + dy;
                if (xx < 0 || xx >= size || yy < 0 || yy >= size) continue;
                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy));
                Set(modules, isFunction, xx, yy, distance != 2 && distance != 4);
            }
    }

    private static int[] AlignmentPositions(int version)
    {
        if (version == 1) return [];
        var count = version / 7 + 2;
        var step = version == 32 ? 26 : (version * 4 + count * 2 + 1) / (count * 2 - 2) * 2;
        var result = new int[count];
        result[0] = 6;
        for (int i = count - 1, position = version * 4 + 10; i >= 1; i--, position -= step) result[i] = position;
        return result;
    }

    /// <summary>The 15 format bits (level M and the mask, BCH-protected, XOR 0x5412) in both copies.</summary>
    private static void DrawFormatBits(bool[,] modules, int mask, bool[,]? isFunction = null)
    {
        var size = modules.GetLength(0);
        var data = FormatLevelM << 3 | mask;
        var remainder = data;
        for (var i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
        var bits = (data << 10 | remainder) ^ 0x5412;
        void Put(int x, int y, bool dark)
        {
            modules[y, x] = dark;
            if (isFunction is not null) isFunction[y, x] = true;
        }

        bool Bit(int i) => ((bits >> i) & 1) != 0;

        for (var i = 0; i <= 5; i++) Put(8, i, Bit(i));
        Put(8, 7, Bit(6));
        Put(8, 8, Bit(7));
        Put(7, 8, Bit(8));
        for (var i = 9; i < 15; i++) Put(14 - i, 8, Bit(i));

        for (var i = 0; i < 8; i++) Put(size - 1 - i, 8, Bit(i));
        for (var i = 8; i < 15; i++) Put(8, size - 15 + i, Bit(i));
        Put(8, size - 8, true);   // the dark module
    }

    /// <summary>The codewords in the zigzag order: two-module columns from the right, up and down in turn, skipping the timing column.</summary>
    private static void DrawCodewords(bool[,] modules, bool[,] isFunction, byte[] codewords)
    {
        var size = modules.GetLength(0);
        var i = 0;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (var vertical = 0; vertical < size; vertical++)
                for (var j = 0; j < 2; j++)
                {
                    var x = right - j;
                    var upward = ((right + 1) & 2) == 0;
                    var y = upward ? size - 1 - vertical : vertical;
                    if (isFunction[y, x] || i >= codewords.Length * 8) continue;
                    modules[y, x] = ((codewords[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                    i++;
                }
        }
    }

    private static void ApplyMask(bool[,] modules, bool[,] isFunction, int mask)
    {
        var size = modules.GetLength(0);
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                if (isFunction[y, x]) continue;
                var invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) modules[y, x] = !modules[y, x];
            }
    }

    // ---- the penalty of a masked code (ISO/IEC 18004 7.8.3)

    private static int Penalty(bool[,] modules)
    {
        var size = modules.GetLength(0);
        var result = 0;

        // N1: five or more modules of one colour in a row or a column; N3: the finder-like 1:1:3:1:1 with four light modules on one side
        for (var horizontal = 0; horizontal < 2; horizontal++)
            for (var a = 0; a < size; a++)
            {
                var run = 0;
                var previous = false;
                for (var b = 0; b < size; b++)
                {
                    var dark = horizontal == 0 ? modules[a, b] : modules[b, a];
                    if (b > 0 && dark == previous) run++;
                    else
                    {
                        if (run >= 5) result += 3 + run - 5;
                        run = 1;
                    }

                    previous = dark;
                }

                if (run >= 5) result += 3 + run - 5;

                for (var b = 0; b + 10 < size + 4; b++)
                {
                    bool At(int i) { var k = b + i - 4; return k >= 0 && k < size && (horizontal == 0 ? modules[a, k] : modules[k, a]); }
                    // the pattern dark-light-dark x3-light-dark with four light modules before or after (beyond the edge counts as light)
                    if (!(At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10))) continue;
                    var lightBefore = !At(0) && !At(1) && !At(2) && !At(3);
                    var lightAfter = !At(11) && !At(12) && !At(13) && !At(14);
                    if (lightBefore) result += 40;
                    if (lightAfter) result += 40;
                }
            }

        // N2: 2x2 blocks of one colour
        for (var y = 0; y < size - 1; y++)
            for (var x = 0; x < size - 1; x++)
            {
                var dark = modules[y, x];
                if (dark == modules[y, x + 1] && dark == modules[y + 1, x] && dark == modules[y + 1, x + 1]) result += 3;
            }

        // N4: how far the share of dark modules is from half, in steps of 5%
        var darkCount = 0;
        foreach (var dark in modules) if (dark) darkCount++;
        var total = size * size;
        var k = (Math.Abs(darkCount * 20 - total * 10) + total - 1) / total - 1;
        result += k * 10;
        return result;
    }
}
