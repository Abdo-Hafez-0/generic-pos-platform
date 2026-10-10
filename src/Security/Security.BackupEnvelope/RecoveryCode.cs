using System.Security.Cryptography;
using System.Text;

namespace Security.BackupEnvelope;

/// <summary>
/// The shop's recovery code (design section 5A): 128 random bits, written as 26 Crockford base-32 characters plus one check character,
/// in groups of four - for example <c>K7QF-M2XA-9C1D-...-ZP3</c>. Reading is forgiving: case, spaces and dashes do not matter, and the
/// letters people confuse with digits are read as those digits (O = 0, I and L = 1). The check character catches a mistyped or swapped
/// character (and almost every swapped pair) before anyone is told "wrong code". The code is shown once and never stored: only the key derived from it is kept.
/// </summary>
public static class RecoveryCode
{
    public const int SecretBytes = 16;
    private const int DataChars = 26;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>A new random code (the secret behind it) and its written form.</summary>
    public static (byte[] Secret, string Text) Generate()
    {
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        return (secret, Format(secret));
    }

    public static string Format(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != SecretBytes) throw new ArgumentException($"A recovery code holds {SecretBytes} bytes.", nameof(secret));

        var values = ToBase32(secret);
        var chars = values.Select(v => Alphabet[v]).Append(Alphabet[Check(values)]).ToArray();
        var text = new StringBuilder();
        for (var i = 0; i < chars.Length; i++)
        {
            if (i > 0 && i % 4 == 0) text.Append('-');
            text.Append(chars[i]);
        }

        return text.ToString();
    }

    /// <summary>False when the text is not a recovery code or a character was mistyped (the check character does not match).</summary>
    public static bool TryParse(string? text, out byte[] secret)
    {
        secret = [];
        if (string.IsNullOrWhiteSpace(text)) return false;

        var values = new List<int>(DataChars + 1);
        foreach (var raw in text.ToUpperInvariant())
        {
            if (raw is '-' or ' ' or '\t') continue;
            var c = raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw };
            var value = Alphabet.IndexOf(c);
            if (value < 0) return false;
            values.Add(value);
        }

        if (values.Count != DataChars + 1) return false;
        var data = values.Take(DataChars).ToArray();
        if (Check(data) != values[DataChars]) return false;

        var bytes = FromBase32(data);
        if (bytes is null) return false;
        secret = bytes;
        return true;
    }

    /// <summary>
    /// Weighted sum modulo 32 with odd weights: ANY single wrong character changes it (an odd weight times a difference of 1..31 is never a
    /// multiple of 32). Two swapped neighbours change it unless the two characters are exactly 16 apart in the alphabet.
    /// </summary>
    private static int Check(IReadOnlyList<int> values)
    {
        var sum = 0;
        for (var i = 0; i < values.Count; i++) sum += (2 * i + 1) * values[i];
        return sum % 32;
    }

    private static int[] ToBase32(ReadOnlySpan<byte> bytes)
    {
        var values = new int[DataChars];
        int buffer = 0, bits = 0, index = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                values[index++] = (buffer >> (bits - 5)) & 31;
                bits -= 5;
            }
        }

        if (bits > 0) values[index] = (buffer << (5 - bits)) & 31;   // the last 2 bits padded with zeros
        return values;
    }

    private static byte[]? FromBase32(IReadOnlyList<int> values)
    {
        var bytes = new byte[SecretBytes];
        int buffer = 0, bits = 0, index = 0;
        for (var i = 0; i < values.Count; i++)
        {
            buffer = (buffer << 5) | values[i];
            bits += 5;
            if (bits >= 8)
            {
                if (index == SecretBytes) return null;
                bytes[index++] = (byte)((buffer >> (bits - 8)) & 0xFF);
                bits -= 8;
            }
        }

        // 26 characters carry 130 bits: the 2 padding bits must be zero, or it is not a code this application wrote
        return index == SecretBytes && (buffer & ((1 << bits) - 1)) == 0 ? bytes : null;
    }
}

/// <summary>The shop key derived from a recovery code, and its public id (which code a backup needs, without revealing it).</summary>
public sealed record BackupKey(string KeyId, byte[] Key)
{
    private static readonly byte[] Salt = Encoding.ASCII.GetBytes("GenericPOS backup v1");

    public static BackupKey FromRecoveryCode(ReadOnlySpan<byte> secret)
    {
        if (secret.Length != RecoveryCode.SecretBytes) throw new ArgumentException("Not a recovery code secret.", nameof(secret));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret.ToArray(), 32, Salt, Encoding.ASCII.GetBytes("shop key"));
        var id = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret.ToArray(), 8, Salt, Encoding.ASCII.GetBytes("shop key id"));
        return new BackupKey(Convert.ToHexStringLower(id), key);
    }

    public override string ToString() => $"BackupKey {{ KeyId = {KeyId} }}";   // never the key itself
}
