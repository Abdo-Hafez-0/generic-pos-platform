using System.Security.Cryptography;
using System.Text;
using Security.BackupEnvelope;

namespace Backup.Tests;

/// <summary>MISS-04e1: the recovery code, the shop key, and the encrypted cloud backup file (.gpbak) with its escrow slot.</summary>
public sealed class EnvelopeTests
{
    private static readonly DateTimeOffset Made = new(2026, 10, 10, 23, 0, 0, TimeSpan.FromHours(2));

    // ------------------------------------------------------------------ recovery code

    [Fact]
    public void A_recovery_code_is_27_characters_in_groups_of_four_and_reads_back_to_the_same_secret()
    {
        var (secret, text) = RecoveryCode.Generate();

        Assert.Equal(16, secret.Length);
        Assert.Matches("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){5}-[0-9A-HJKMNP-TV-Z]{3}$", text);
        Assert.True(RecoveryCode.TryParse(text, out var back));
        Assert.Equal(secret, back);
        Assert.NotEqual(text, RecoveryCode.Generate().Text);
    }

    [Fact]
    public void Reading_a_code_forgives_case_spaces_dashes_and_letters_that_look_like_digits()
    {
        var secret = Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray();
        var text = RecoveryCode.Format(secret);

        var sloppy = text.Replace("-", " ").ToLowerInvariant().Replace('0', 'o').Replace('1', 'l');
        Assert.True(RecoveryCode.TryParse(sloppy, out var back));
        Assert.Equal(secret, back);
    }

    [Fact]
    public void A_mistyped_or_swapped_character_and_wrong_lengths_are_refused()
    {
        var secret = Enumerable.Range(0, 16).Select(i => (byte)(i * 31 + 7)).ToArray();
        var plain = RecoveryCode.Format(secret).Replace("-", "");
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        // every possible wrong character in every position (including Z typed as 0 and the other way round)
        for (var position = 0; position < plain.Length; position++)
            foreach (var other in alphabet.Where(c => c != plain[position]))
                Assert.False(RecoveryCode.TryParse(plain[..position] + other + plain[(position + 1)..], out _), $"{other} at {position}");

        for (var position = 0; position + 1 < plain.Length; position++)
        {
            if (plain[position] == plain[position + 1]) continue;
            var swapped = plain[..position] + plain[position + 1] + plain[position] + plain[(position + 2)..];
            var difference = Math.Abs(alphabet.IndexOf(plain[position]) - alphabet.IndexOf(plain[position + 1]));
            if (difference != 16) Assert.False(RecoveryCode.TryParse(swapped, out _), $"swap at {position}");
        }

        Assert.False(RecoveryCode.TryParse(plain[..^1], out _));
        Assert.False(RecoveryCode.TryParse(plain + "A", out _));
        Assert.False(RecoveryCode.TryParse("", out _));
        Assert.False(RecoveryCode.TryParse("K7QF-M2XA-U***", out _));
    }

    [Fact]
    public void The_shop_key_is_derived_from_the_code_alone_and_its_id_reveals_nothing_of_it()
    {
        var (secret, _) = RecoveryCode.Generate();

        var a = BackupKey.FromRecoveryCode(secret);
        var b = BackupKey.FromRecoveryCode(secret);
        var other = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);

        Assert.Equal(a.Key, b.Key);
        Assert.Equal(a.KeyId, b.KeyId);
        Assert.Matches("^[0-9a-f]{16}$", a.KeyId);
        Assert.NotEqual(a.Key, other.Key);
        Assert.NotEqual(a.KeyId, other.KeyId);
        Assert.DoesNotContain(Convert.ToHexString(a.Key), a.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the file

    private sealed class Vendor : IDisposable
    {
        public ECDiffieHellman Private { get; } = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        public EscrowPublicKey Public => new("escrow-2026", Convert.ToBase64String(Private.ExportSubjectPublicKeyInfo()));

        /// <summary>What the vendor's offline tool does (MISS-04f): open the escrow slot with the private key.</summary>
        public byte[] OpenSlot(EscrowSlot slot)
        {
            var ephemeralSpki = Convert.FromBase64String(slot.EphemeralPublicKey);
            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralSpki, out _);
            var shared = Private.DeriveRawSecretAgreement(ephemeral.PublicKey);
            var kek = BackupEnvelope.EscrowKek(shared, ephemeralSpki, slot.KeyId);
            return BackupEnvelope.OpenSealed(kek, Convert.FromBase64String(slot.SealedKey), Encoding.UTF8.GetBytes("escrow:" + slot.KeyId));
        }

        public void Dispose() => Private.Dispose();
    }

    /// <summary>About 3.5 MiB: a third compressible, the rest random, so the compressed stream spans at least three 1 MiB chunks.</summary>
    private static byte[] Database()
    {
        var data = new byte[3_700_000];
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("INSERT INTO sal_Sales VALUES ('2.50'); ", 34_000))).AsSpan(0, 1_300_000).CopyTo(data);
        new Random(42).NextBytes(data.AsSpan(1_300_000));
        return data;
    }

    private static EnvelopeMetadata MetadataOf(byte[] database)
        => new("1.2.3", ["20261005120224_InitialSalesSchema"], database.Length, Convert.ToHexStringLower(SHA256.HashData(database)));

    private static async Task<byte[]> SealAsync(byte[] database, BackupKey key, EscrowPublicKey escrow, EnvelopeMetadata? metadata = null)
    {
        using var output = new MemoryStream();
        await BackupEnvelope.WriteAsync(new MemoryStream(database), output, key, escrow, metadata ?? MetadataOf(database), Made);
        return output.ToArray();
    }

    private static async Task<(byte[] Database, EnvelopeMetadata Metadata)> OpenAsync(byte[] file, byte[] dataKey)
    {
        using var output = new MemoryStream();
        var metadata = await BackupEnvelope.DecryptAsync(new MemoryStream(file), output, dataKey);
        return (output.ToArray(), metadata);
    }

    private static byte[] DataKey(byte[] file, BackupKey key) => BackupEnvelope.UnwrapDataKey(BackupEnvelope.ReadHeader(new MemoryStream(file)), key);

    [Fact]
    public async Task A_backup_opens_with_the_shop_key_and_gives_back_the_same_database_and_metadata()
    {
        using var vendor = new Vendor();
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        var database = Database();

        var file = await SealAsync(database, key, vendor.Public);

        Assert.True(file.Length < database.Length, "the readable half is compressed");
        Assert.True(file.Length > 2 * BackupEnvelope.ChunkSize / 2, "several chunks");
        var header = BackupEnvelope.ReadHeader(new MemoryStream(file));
        Assert.Equal(key.KeyId, header.KeyId);
        Assert.Equal(Made, header.CreatedAt);
        Assert.Equal("escrow-2026", header.Escrow.KeyId);

        var (opened, metadata) = await OpenAsync(file, DataKey(file, key));
        Assert.Equal(database, opened);
        Assert.Equal("1.2.3", metadata.ApplicationVersion);
        Assert.Equal(["20261005120224_InitialSalesSchema"], metadata.Migrations);

        // nothing readable of the database is in the file
        Assert.DoesNotContain("INSERT INTO sal_Sales", Encoding.ASCII.GetString(file));
    }

    [Fact]
    public async Task The_vendor_opens_the_escrow_slot_with_the_private_key_and_only_that_backup()
    {
        using var vendor = new Vendor();
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        var first = await SealAsync(Database(), key, vendor.Public);
        var second = await SealAsync(Database(), key, vendor.Public);

        var slot = BackupEnvelope.ReadHeader(new MemoryStream(first)).Escrow;
        var dataKey = vendor.OpenSlot(slot);

        Assert.Equal(Database(), (await OpenAsync(first, dataKey)).Database);
        Assert.Equal(DataKey(first, key), dataKey);
        Assert.NotEqual(DataKey(second, key), dataKey);   // a key per backup: escrow opens one backup, never the shop key
        await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync(second, dataKey));

        using var stranger = new Vendor();
        Assert.ThrowsAny<CryptographicException>(() => stranger.OpenSlot(slot));
    }

    [Fact]
    public async Task Another_recovery_code_cannot_open_a_backup()
    {
        using var vendor = new Vendor();
        var file = await SealAsync(Database(), BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret), vendor.Public);
        var other = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);

        var refused = Assert.Throws<BackupEnvelopeException>(() => DataKey(file, other));
        Assert.Equal(EnvelopeProblem.WrongKey, refused.Problem);

        // and a forged key id with the wrong key still fails at the authenticated wrap
        var forged = new BackupKey(BackupEnvelope.ReadHeader(new MemoryStream(file)).KeyId, other.Key);
        Assert.Equal(EnvelopeProblem.Damaged, Assert.Throws<BackupEnvelopeException>(() => DataKey(file, forged)).Problem);
    }

    [Fact]
    public async Task Any_changed_byte_anywhere_in_the_file_is_refused()
    {
        using var vendor = new Vendor();
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        var file = await SealAsync(Database(), key, vendor.Public);
        var dataKey = DataKey(file, key);

        foreach (var position in new[] { 0, 4, 7, 20, 120, 300, 700, 2000, file.Length / 3, file.Length / 2, file.Length - 40, file.Length - 1 })
        {
            var changed = (byte[])file.Clone();
            changed[position] ^= 0x01;
            await Assert.ThrowsAnyAsync<BackupEnvelopeException>(async () =>
            {
                var header = BackupEnvelope.ReadHeader(new MemoryStream(changed));
                await OpenAsync(changed, BackupEnvelope.UnwrapDataKey(header, key));
            });
        }

        Assert.Equal(Database(), (await OpenAsync(file, dataKey)).Database);   // the original still opens
    }

    [Fact]
    public async Task A_file_cut_short_or_extended_is_refused()
    {
        using var vendor = new Vendor();
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        var file = await SealAsync(Database(), key, vendor.Public);
        var dataKey = DataKey(file, key);

        foreach (var cut in new[] { file.Length - 1, file.Length - 17, file.Length - 22, file.Length / 2, 600 })
            Assert.Equal(EnvelopeProblem.Damaged, (await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync(file[..cut], dataKey))).Problem);

        Assert.Equal(EnvelopeProblem.Damaged, (await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync([.. file, 0], dataKey))).Problem);
    }

    [Fact]
    public async Task Chunks_cannot_be_reordered_or_dropped()
    {
        using var vendor = new Vendor();
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        var file = await SealAsync(Database(), key, vendor.Public);
        var dataKey = DataKey(file, key);

        // walk the layout: header, metadata, then chunks
        var offset = 9 + BitConverter.ToInt32(file, 5);
        offset += 4 + BitConverter.ToInt32(file, offset) + 16;
        var chunks = new List<(int Start, int Length)>();
        while (offset < file.Length)
        {
            var length = 1 + 4 + BitConverter.ToInt32(file, offset + 1) + 16;
            chunks.Add((offset, length));
            offset += length;
        }

        Assert.True(chunks.Count >= 3, $"expected several chunks, found {chunks.Count}");
        var body = chunks[0].Start;
        byte[] Rebuild(IEnumerable<int> order) => [.. file[..body], .. order.SelectMany(i => file.AsSpan(chunks[i].Start, chunks[i].Length).ToArray())];

        var swapped = Enumerable.Range(0, chunks.Count).ToArray();
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
        await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync(Rebuild(swapped), dataKey));
        await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync(Rebuild(Enumerable.Range(1, chunks.Count - 1)), dataKey));
        await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync(Rebuild(Enumerable.Range(0, chunks.Count - 1)), dataKey));
        await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync(Rebuild([0, .. Enumerable.Range(0, chunks.Count)]), dataKey));
    }

    [Fact]
    public async Task A_database_that_does_not_match_its_sealed_fingerprint_is_refused_after_decryption()
    {
        using var vendor = new Vendor();
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        var database = Database();
        var file = await SealAsync(database, key, vendor.Public, MetadataOf(database) with { PlaintextSha256 = new string('0', 64) });

        var refused = await Assert.ThrowsAsync<BackupEnvelopeException>(() => OpenAsync(file, DataKey(file, key)));
        Assert.Equal(EnvelopeProblem.Damaged, refused.Problem);
    }

    [Fact]
    public async Task Other_files_and_newer_formats_are_told_apart_and_an_empty_database_round_trips()
    {
        Assert.Equal(EnvelopeProblem.NotAnEnvelope, Assert.Throws<BackupEnvelopeException>(() => BackupEnvelope.ReadHeader(new MemoryStream("SQLite format 3\0"u8.ToArray()))).Problem);
        Assert.Equal(EnvelopeProblem.NotAnEnvelope, Assert.Throws<BackupEnvelopeException>(() => BackupEnvelope.ReadHeader(new MemoryStream([]))).Problem);

        using var vendor = new Vendor();
        var key = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        var file = await SealAsync([], key, vendor.Public);
        Assert.Empty((await OpenAsync(file, DataKey(file, key))).Database);

        file[4] = 2;
        Assert.Equal(EnvelopeProblem.UnsupportedVersion, Assert.Throws<BackupEnvelopeException>(() => BackupEnvelope.ReadHeader(new MemoryStream(file))).Problem);
    }

    [Fact]
    public async Task Only_a_P256_escrow_key_is_accepted()
    {
        using var p384 = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var wrong = new EscrowPublicKey("bad", Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()));

        await Assert.ThrowsAsync<ArgumentException>(() => SealAsync(Database(), BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret), wrong));
    }
}
