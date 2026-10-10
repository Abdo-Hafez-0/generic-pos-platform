using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Security.BackupEnvelope;

/// <summary>A vendor escrow PUBLIC key: base64 of the DER SubjectPublicKeyInfo of an ECDH P-256 key (the same form as the trusted ES256 keys).</summary>
public sealed record EscrowPublicKey(string KeyId, string PublicKey);

/// <summary>What a backup holds, sealed inside the file (only someone who can open the backup can read it).</summary>
public sealed record EnvelopeMetadata(string ApplicationVersion, IReadOnlyList<string> Migrations, long PlaintextSize, string PlaintextSha256);

/// <summary>The escrow slot (design section 5B): the backup's data key sealed to the vendor's escrow public key.</summary>
public sealed record EscrowSlot(string KeyId, string EphemeralPublicKey, string SealedKey);

/// <summary>The readable, authenticated part of a <c>.gpbak</c> file.</summary>
public sealed record EnvelopeHeader(int Version, string KeyId, DateTimeOffset CreatedAt, string WrappedKey, EscrowSlot Escrow, string NoncePrefix);

public enum EnvelopeProblem
{
    /// <summary>Not a backup file of this application.</summary>
    NotAnEnvelope,

    /// <summary>Made by a newer format version.</summary>
    UnsupportedVersion,

    /// <summary>The key given is not the key this backup was made with (another recovery code).</summary>
    WrongKey,

    /// <summary>Changed, cut short, extended or otherwise damaged.</summary>
    Damaged,
}

public sealed class BackupEnvelopeException(EnvelopeProblem problem, string message, Exception? inner = null) : Exception(message, inner)
{
    public EnvelopeProblem Problem { get; } = problem;
}

/// <summary>
/// The encrypted cloud backup file (design section 4):
///
///   "GPBK" | version (1 byte) | header length (u32 LE) | header (UTF-8 JSON)         - readable, authenticated
///   metadata: length (u32 LE) | AES-256-GCM ciphertext | tag                         - sealed
///   chunks:   flag (1 byte: 0 = more, 1 = final) | length (u32 LE) | ciphertext | tag - Brotli-compressed database, 1 MiB per chunk
///
/// Every backup has its own random 256-bit data key, wrapped under the shop key (header.wrappedKey) and sealed to the vendor's escrow key
/// (header.escrow). Separate keys for the metadata and the content are derived from it. Each chunk's nonce is the header's random prefix
/// plus the chunk number; its associated data is the SHA-256 of everything before the body plus the chunk number and the final flag. So a
/// changed header, a reordered, missing, repeated or added chunk, a file cut short (no final chunk) or bytes after the final chunk are all
/// refused, and nothing is ever written out that was not authenticated. After decryption the database's size and SHA-256 are compared
/// with the sealed metadata.
/// </summary>
public static class BackupEnvelope
{
    public const string Extension = ".gpbak";
    public const int FormatVersion = 1;
    public const int ChunkSize = 1024 * 1024;
    private const int TagSize = 16;
    private const int NonceSize = 12;
    private const int MaxHeaderBytes = 64 * 1024;
    private static readonly byte[] Magic = "GPBK"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ write

    /// <summary>Encrypts a database into <paramref name="output"/>. Returns the header written.</summary>
    public static async Task<EnvelopeHeader> WriteAsync(Stream plaintext, Stream output, BackupKey shopKey, EscrowPublicKey escrow, EnvelopeMetadata metadata,
        DateTimeOffset createdAt, CancellationToken cancellationToken = default)
    {
        var dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var header = new EnvelopeHeader(FormatVersion, shopKey.KeyId, createdAt, WrapKey(dataKey, shopKey),
                SealToEscrow(dataKey, escrow), Convert.ToBase64String(RandomNumberGenerator.GetBytes(NonceSize - 5)));
            var prefix = HeaderBytes(header);
            await output.WriteAsync(prefix, cancellationToken);
            var bound = SHA256.HashData(prefix);

            var (metaKey, contentKey) = DeriveKeys(dataKey);
            var meta = JsonSerializer.SerializeToUtf8Bytes(metadata, Json);
            var sealedMeta = Seal(metaKey, Nonce(header, uint.MaxValue, final: true), meta, Aad(bound, uint.MaxValue, final: true));
            await WriteUInt32Async(output, (uint)(sealedMeta.Length - TagSize), cancellationToken);
            await output.WriteAsync(sealedMeta, cancellationToken);

            await using var compressed = new ChunkSealingStream(output, contentKey, header, bound);
            await using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                await plaintext.CopyToAsync(brotli, cancellationToken);
            await compressed.CompleteAsync(cancellationToken);
            return header;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    // ------------------------------------------------------------------ read

    /// <summary>Reads and checks the readable part (which key it needs, when it was made, the escrow slot). The stream is left after the header.</summary>
    public static EnvelopeHeader ReadHeader(Stream input)
    {
        Span<byte> start = stackalloc byte[9];
        if (!TryReadExactly(input, start)) throw NotAnEnvelope();
        if (!start[..4].SequenceEqual(Magic)) throw NotAnEnvelope();
        if (start[4] != FormatVersion)
            throw new BackupEnvelopeException(EnvelopeProblem.UnsupportedVersion, $"The backup was made with format {start[4]}, which this version cannot read.");

        var length = BinaryPrimitives.ReadUInt32LittleEndian(start[5..]);
        if (length is 0 or > MaxHeaderBytes) throw NotAnEnvelope();
        var json = new byte[length];
        if (!TryReadExactly(input, json)) throw Damaged();

        try
        {
            var header = JsonSerializer.Deserialize<EnvelopeHeader>(json, Json);
            if (header is null || header.Version != FormatVersion || string.IsNullOrEmpty(header.KeyId) || header.Escrow is null) throw NotAnEnvelope();
            if (!HeaderBytes(header).AsSpan(9).SequenceEqual(json)) throw Damaged();   // only the canonical form is accepted
            return header;
        }
        catch (JsonException ex)
        {
            throw new BackupEnvelopeException(EnvelopeProblem.NotAnEnvelope, "The file is not a backup of this application.", ex);
        }
    }

    /// <summary>The backup's data key, unwrapped with the shop key. WrongKey when it is another shop key (another recovery code).</summary>
    public static byte[] UnwrapDataKey(EnvelopeHeader header, BackupKey shopKey)
    {
        if (!string.Equals(header.KeyId, shopKey.KeyId, StringComparison.Ordinal))
            throw new BackupEnvelopeException(EnvelopeProblem.WrongKey, "The backup was made with another recovery code.");

        try
        {
            return Open(shopKey.Key, Convert.FromBase64String(header.WrappedKey), Encoding.UTF8.GetBytes("wrap:" + header.KeyId));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new BackupEnvelopeException(EnvelopeProblem.Damaged, "The backup's key slot is damaged.", ex);
        }
    }

    /// <summary>
    /// Decrypts a whole backup (header included) into <paramref name="plaintext"/> with its data key (from <see cref="UnwrapDataKey"/> or,
    /// for the vendor, from the escrow slot). Checks every chunk, the end of the file, and the database's size and SHA-256 against the
    /// sealed metadata. On any failure the output must be discarded (the caller writes to a temporary file).
    /// </summary>
    public static async Task<EnvelopeMetadata> DecryptAsync(Stream input, Stream plaintext, byte[] dataKey, CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(input);
        var bound = SHA256.HashData(HeaderBytes(header));
        var (metaKey, contentKey) = DeriveKeys(dataKey);

        EnvelopeMetadata metadata;
        try
        {
            var metaLength = await ReadUInt32Async(input, cancellationToken);
            if (metaLength > MaxHeaderBytes) throw Damaged();
            var sealedMeta = new byte[metaLength + TagSize];
            if (!await TryReadExactlyAsync(input, sealedMeta, cancellationToken)) throw Damaged();
            metadata = JsonSerializer.Deserialize<EnvelopeMetadata>(Open(metaKey, Nonce(header, uint.MaxValue, true), sealedMeta, Aad(bound, uint.MaxValue, true)), Json)
                       ?? throw Damaged();
        }
        catch (CryptographicException ex)
        {
            throw new BackupEnvelopeException(EnvelopeProblem.WrongKey, "The backup cannot be opened with this key.", ex);
        }

        await using var opened = new ChunkOpeningStream(input, contentKey, header, bound);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        try
        {
            await using var brotli = new BrotliStream(opened, CompressionMode.Decompress);
            var buffer = new byte[81920];
            int read;
            while ((read = await brotli.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await plaintext.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                size += read;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new BackupEnvelopeException(EnvelopeProblem.Damaged, "The backup's content is damaged.", ex);
        }

        await opened.EnsureCompleteAsync(cancellationToken);
        if (size != metadata.PlaintextSize || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), metadata.PlaintextSha256, StringComparison.OrdinalIgnoreCase))
            throw new BackupEnvelopeException(EnvelopeProblem.Damaged, "The restored database does not match the backup's fingerprint.");
        return metadata;
    }

    // ------------------------------------------------------------------ keys

    private static string WrapKey(byte[] dataKey, BackupKey shopKey)
        => Convert.ToBase64String(Seal(shopKey.Key, RandomNumberGenerator.GetBytes(NonceSize), dataKey, Encoding.UTF8.GetBytes("wrap:" + shopKey.KeyId), includeNonce: true));

    /// <summary>ECIES: an ephemeral P-256 key agrees a secret with the escrow key; HKDF turns it into the key that seals the data key.</summary>
    private static EscrowSlot SealToEscrow(byte[] dataKey, EscrowPublicKey escrow)
    {
        using var vendor = ECDiffieHellman.Create();
        vendor.ImportSubjectPublicKeyInfo(Convert.FromBase64String(escrow.PublicKey), out _);
        if (vendor.KeySize != 256) throw new ArgumentException("The escrow key must be a P-256 key.", nameof(escrow));

        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var shared = ephemeral.DeriveRawSecretAgreement(vendor.PublicKey);
        try
        {
            var kek = EscrowKek(shared, ephemeralPublic, escrow.KeyId);
            var sealedKey = Seal(kek, RandomNumberGenerator.GetBytes(NonceSize), dataKey, Encoding.UTF8.GetBytes("escrow:" + escrow.KeyId), includeNonce: true);
            CryptographicOperations.ZeroMemory(kek);
            return new EscrowSlot(escrow.KeyId, Convert.ToBase64String(ephemeralPublic), Convert.ToBase64String(sealedKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    /// <summary>The key-encryption key of an escrow slot (public so the vendor-side opener derives exactly the same one).</summary>
    public static byte[] EscrowKek(byte[] sharedSecret, byte[] ephemeralPublicKey, string escrowKeyId)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, ephemeralPublicKey, Encoding.UTF8.GetBytes("GenericPOS escrow v1:" + escrowKeyId));

    /// <summary>Opens a value sealed with <c>nonce | ciphertext | tag</c> (the escrow slot's and the wrapped key's layout).</summary>
    public static byte[] OpenSealed(byte[] key, byte[] nonceCiphertextTag, byte[] associatedData) => Open(key, nonceCiphertextTag, associatedData);

    private static (byte[] Meta, byte[] Content) DeriveKeys(byte[] dataKey)
        => (HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, info: "metadata"u8.ToArray()),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, info: "content"u8.ToArray()));

    // ------------------------------------------------------------------ primitives

    internal static byte[] HeaderBytes(EnvelopeHeader header)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(header, Json);
        var bytes = new byte[9 + json.Length];
        Magic.CopyTo(bytes, 0);
        bytes[4] = FormatVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5), (uint)json.Length);
        json.CopyTo(bytes, 9);
        return bytes;
    }

    internal static byte[] Nonce(EnvelopeHeader header, uint counter, bool final)
    {
        var nonce = new byte[NonceSize];
        Convert.FromBase64String(header.NoncePrefix).CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NonceSize - 5), counter);
        nonce[NonceSize - 1] = final ? (byte)1 : (byte)0;
        return nonce;
    }

    internal static byte[] Aad(byte[] boundHeader, uint counter, bool final)
    {
        var aad = new byte[boundHeader.Length + 5];
        boundHeader.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(boundHeader.Length), counter);
        aad[^1] = final ? (byte)1 : (byte)0;
        return aad;
    }

    /// <summary>ciphertext | tag (or nonce | ciphertext | tag).</summary>
    internal static byte[] Seal(byte[] key, byte[] nonce, ReadOnlySpan<byte> plaintext, byte[] aad, bool includeNonce = false)
    {
        var offset = includeNonce ? NonceSize : 0;
        var output = new byte[offset + plaintext.Length + TagSize];
        if (includeNonce) nonce.CopyTo(output, 0);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(offset, plaintext.Length), output.AsSpan(offset + plaintext.Length, TagSize), aad);
        return output;
    }

    internal static byte[] Open(byte[] key, byte[] nonceCiphertextTag, byte[] aad)
        => Open(key, nonceCiphertextTag.AsSpan(0, NonceSize).ToArray(), nonceCiphertextTag.AsSpan(NonceSize).ToArray(), aad);

    internal static byte[] Open(byte[] key, byte[] nonce, byte[] ciphertextTag, byte[] aad)
    {
        if (ciphertextTag.Length < TagSize) throw new CryptographicException("Too short.");
        var length = ciphertextTag.Length - TagSize;
        var plaintext = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(nonce, ciphertextTag.AsSpan(0, length), ciphertextTag.AsSpan(length), plaintext, aad);
        return plaintext;
    }

    internal static async Task WriteUInt32Async(Stream output, uint value, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        await output.WriteAsync(bytes, cancellationToken);
    }

    internal static async Task<uint> ReadUInt32Async(Stream input, CancellationToken cancellationToken)
    {
        var bytes = new byte[4];
        if (!await TryReadExactlyAsync(input, bytes, cancellationToken)) throw Damaged();
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    internal static bool TryReadExactly(Stream input, Span<byte> buffer)
    {
        try
        {
            input.ReadExactly(buffer);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    internal static async Task<bool> TryReadExactlyAsync(Stream input, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try
        {
            await input.ReadExactlyAsync(buffer, cancellationToken);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    internal static BackupEnvelopeException NotAnEnvelope() => new(EnvelopeProblem.NotAnEnvelope, "The file is not a backup of this application.");

    internal static BackupEnvelopeException Damaged() => new(EnvelopeProblem.Damaged, "The backup file is damaged or incomplete.");

    internal const int Tag = TagSize;
}

/// <summary>Write side of the chunk stream: buffers 1 MiB, seals each full chunk; <see cref="CompleteAsync"/> seals the final (possibly empty) one.</summary>
internal sealed class ChunkSealingStream(Stream output, byte[] key, EnvelopeHeader header, byte[] bound) : Stream
{
    private readonly byte[] _buffer = new byte[BackupEnvelope.ChunkSize];
    private int _filled;
    private uint _counter;
    private bool _completed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        while (!source.IsEmpty)
        {
            var take = Math.Min(source.Length, _buffer.Length - _filled);
            source[..take].CopyTo(_buffer.AsMemory(_filled));
            _filled += take;
            source = source[take..];
            if (_filled == _buffer.Length) await SealAsync(final: false, cancellationToken);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed) return;
        await SealAsync(final: true, cancellationToken);
        _completed = true;
        await output.FlushAsync(cancellationToken);
    }

    private async Task SealAsync(bool final, CancellationToken cancellationToken)
    {
        var sealedChunk = BackupEnvelope.Seal(key, BackupEnvelope.Nonce(header, _counter, final), _buffer.AsSpan(0, _filled), BackupEnvelope.Aad(bound, _counter, final));
        await output.WriteAsync(new[] { final ? (byte)1 : (byte)0 }, cancellationToken);
        await BackupEnvelope.WriteUInt32Async(output, (uint)_filled, cancellationToken);
        await output.WriteAsync(sealedChunk, cancellationToken);
        _counter = checked(_counter + 1);
        _filled = 0;
    }

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Read side: opens chunk after chunk (each authenticated before any byte is handed on) until the final one, then expects the end.</summary>
internal sealed class ChunkOpeningStream(Stream input, byte[] key, EnvelopeHeader header, byte[] bound) : Stream
{
    private byte[] _chunk = [];
    private int _position;
    private uint _counter;
    private bool _final;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        while (_position == _chunk.Length)
        {
            if (_final) return 0;
            await NextChunkAsync(cancellationToken);
        }

        var take = Math.Min(destination.Length, _chunk.Length - _position);
        _chunk.AsMemory(_position, take).CopyTo(destination);
        _position += take;
        return take;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private async Task NextChunkAsync(CancellationToken cancellationToken)
    {
        var flag = new byte[1];
        if (!await BackupEnvelope.TryReadExactlyAsync(input, flag, cancellationToken) || flag[0] > 1) throw BackupEnvelope.Damaged();   // cut short
        var final = flag[0] == 1;
        var length = await BackupEnvelope.ReadUInt32Async(input, cancellationToken);
        if (length > BackupEnvelope.ChunkSize) throw BackupEnvelope.Damaged();

        var sealedChunk = new byte[length + BackupEnvelope.Tag];
        if (!await BackupEnvelope.TryReadExactlyAsync(input, sealedChunk, cancellationToken)) throw BackupEnvelope.Damaged();
        try
        {
            _chunk = BackupEnvelope.Open(key, BackupEnvelope.Nonce(header, _counter, final), sealedChunk, BackupEnvelope.Aad(bound, _counter, final));
        }
        catch (CryptographicException ex)
        {
            throw new BackupEnvelopeException(EnvelopeProblem.Damaged, "The backup file is damaged or was changed.", ex);
        }

        _position = 0;
        _counter = checked(_counter + 1);
        _final = final;
    }

    /// <summary>
    /// Reads (and authenticates) whatever chunks the decompressor did not need - normally just the empty final one - then requires the end
    /// of the file: a file cut short has no final chunk, an extended one has bytes after it.
    /// </summary>
    public async Task EnsureCompleteAsync(CancellationToken cancellationToken)
    {
        while (!_final) await NextChunkAsync(cancellationToken);
        var extra = new byte[1];
        if (await input.ReadAsync(extra, cancellationToken) != 0) throw BackupEnvelope.Damaged();
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
