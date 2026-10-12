using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Security.BackupEnvelope;
using Security.BackupEnvelope.Recovery;
using Tools.BackupEscrowRecovery;

namespace Backup.Tests;

/// <summary>
/// MISS-04f (design 5B): the recovery texts (request, approved recovery, restore key), sealing a backup's data key to the PC that asked,
/// the vendor-only escrow key (Security.BackupEnvelope.Recovery) and the offline tool tools/BackupEscrowRecovery.
/// </summary>
public sealed class EscrowRecoveryTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "genericpos-escrow-" + Guid.NewGuid().ToString("N"));
    private readonly EscrowPrivateKey _escrow = EscrowPrivateKey.Generate("escrow-2026");
    private readonly ECDiffieHellman _pc = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public EscrowRecoveryTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        _escrow.Dispose();
        _pc.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A real .gpbak made with a shop key and the escrow key; returns its header and the database bytes.</summary>
    private async Task<(EnvelopeHeader Header, byte[] File, byte[] Database)> BackupAsync(EscrowPublicKey? escrow = null)
    {
        var database = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("INSERT INTO sal_Sales VALUES (1); ", 4000)));
        var shop = BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret);
        using var output = new MemoryStream();
        var header = await BackupEnvelope.WriteAsync(new MemoryStream(database), output, shop, escrow ?? _escrow.PublicKey,
            new EnvelopeMetadata("1.2.3", ["20261005_Init"], database.Length, Convert.ToHexStringLower(SHA256.HashData(database))), Now);
        return (header, output.ToArray(), database);
    }

    private RecoveryRequest Request(EnvelopeHeader header, Guid? backupId = null)
        => EscrowRecovery.CreateRequest(header, backupId ?? Guid.NewGuid(), "genericpos-20261012-093000.db", Guid.NewGuid().ToString(), _pc.ExportSubjectPublicKeyInfo(), Now);

    private static ApprovedRecovery Approve(RecoveryRequest request) => new(1, request, Guid.NewGuid(), "C-7", Now, "vendor-admin", "callback");

    // ------------------------------------------------------------------ texts

    [Fact]
    public async Task Each_text_reads_back_and_survives_what_mail_and_chat_do_to_it()
    {
        var request = Request((await BackupAsync()).Header);
        var text = EscrowRecovery.WriteRequest(request);

        Assert.StartsWith("GenericPOS - request to recover a cloud backup", text);
        Assert.Contains(request.BackupId.ToString(), text);
        Assert.Contains("-----BEGIN GENERICPOS RECOVERY REQUEST-----", text);

        var mangled = "Hi, please help.\n\n" + string.Join("\n", text.Split("\r\n").Select(l => "> " + l + "  ")) + "\nThanks";
        foreach (var variant in new[] { text, text.Replace("\r\n", "\n"), mangled })
        {
            Assert.True(EscrowRecovery.TryReadRequest(variant, out var back, out _));
            Assert.Equal(request.RequestId, back!.RequestId);
            Assert.Equal(request.Escrow, back.Escrow);
        }

        var approved = Approve(request);
        Assert.True(EscrowRecovery.TryReadApproved(EscrowRecovery.WriteApproved(approved), out var a, out _));
        Assert.Equal(approved.Request.RequestId, a!.Request.RequestId);

        // kinds are never confused
        Assert.False(EscrowRecovery.TryReadApproved(text, out _, out var problem));
        Assert.Equal(RecoveryTextProblem.Missing, problem);
        Assert.False(EscrowRecovery.TryReadRestoreKey(text, out _, out _));
    }

    [Fact]
    public async Task Any_changed_lost_or_added_character_is_damage_and_a_newer_version_is_said_plainly()
    {
        var text = EscrowRecovery.WriteRequest(Request((await BackupAsync()).Header));
        var start = text.IndexOf("-----BEGIN", StringComparison.Ordinal) + "-----BEGIN GENERICPOS RECOVERY REQUEST-----\r\n".Length;
        var end = text.IndexOf("-----END", StringComparison.Ordinal);

        for (var i = start; i < end; i += 7)
        {
            if (char.IsWhiteSpace(text[i])) continue;
            var changed = text[..i] + (text[i] == 'A' ? 'B' : 'A') + text[(i + 1)..];
            Assert.False(EscrowRecovery.TryReadRequest(changed, out _, out var problem), $"changed at {i}");
            Assert.Equal(RecoveryTextProblem.Damaged, problem);
        }

        Assert.False(EscrowRecovery.TryReadRequest(text.Remove(start + 10, 4), out _, out var lost));
        Assert.Equal(RecoveryTextProblem.Damaged, lost);
        Assert.False(EscrowRecovery.TryReadRequest(text[..end], out _, out var cut));
        Assert.Equal(RecoveryTextProblem.Missing, cut);
        Assert.False(EscrowRecovery.TryReadRequest(null, out _, out _));

        var newer = JsonSerializer.SerializeToUtf8Bytes(new { version = 2, requestId = Guid.NewGuid() });
        var block = $"-----BEGIN GENERICPOS RECOVERY REQUEST-----\n{Convert.ToBase64String(newer)}\n#{Convert.ToHexStringLower(SHA256.HashData(newer))[..8]}\n-----END GENERICPOS RECOVERY REQUEST-----";
        Assert.False(EscrowRecovery.TryReadRequest(block, out _, out var version));
        Assert.Equal(RecoveryTextProblem.UnsupportedVersion, version);
    }

    // ------------------------------------------------------------------ the restore key

    [Fact]
    public async Task The_vendor_opens_the_slot_and_seals_the_data_key_to_the_pc_which_then_opens_the_whole_backup()
    {
        var (header, file, database) = await BackupAsync();
        var request = Request(header);

        var restoreKey = RestoreKeyIssuer.Issue(Approve(request), [EscrowPrivateKey.Generate("escrow-2025"), _escrow], Now);
        Assert.Equal(request.BackupId, restoreKey.BackupId);
        Assert.True(EscrowRecovery.TryReadRestoreKey(EscrowRecovery.WriteRestoreKey(restoreKey), out var pasted, out _));

        var dataKey = EscrowRecovery.OpenRestoreKey(pasted!, request, _pc);
        using var plain = new MemoryStream();
        await BackupEnvelope.DecryptAsync(new MemoryStream(file), plain, dataKey);
        Assert.Equal(database, plain.ToArray());
    }

    [Fact]
    public async Task A_restore_key_opens_nothing_with_another_pc_another_request_another_backup_or_a_changed_byte()
    {
        var (header, _, _) = await BackupAsync();
        var request = Request(header);
        var restoreKey = RestoreKeyIssuer.Issue(Approve(request), [_escrow], Now);

        using var otherPc = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(EnvelopeProblem.WrongKey, Assert.Throws<BackupEnvelopeException>(() => EscrowRecovery.OpenRestoreKey(restoreKey, request, otherPc)).Problem);
        Assert.Throws<BackupEnvelopeException>(() => EscrowRecovery.OpenRestoreKey(restoreKey, request with { RequestId = Guid.NewGuid() }, _pc));
        Assert.Throws<BackupEnvelopeException>(() => EscrowRecovery.OpenRestoreKey(restoreKey with { BackupId = Guid.NewGuid() }, request with { BackupId = restoreKey.BackupId == request.BackupId ? Guid.NewGuid() : request.BackupId }, _pc));

        // the binding is inside the sealed key: a restore key relabelled for another backup does not open, even with matching ids
        var relabelled = restoreKey with { BackupId = Guid.NewGuid() };
        Assert.Throws<BackupEnvelopeException>(() => EscrowRecovery.OpenRestoreKey(relabelled, request with { BackupId = relabelled.BackupId }, _pc));

        var sealedBytes = Convert.FromBase64String(restoreKey.SealedKey);
        sealedBytes[^1] ^= 1;
        Assert.Throws<BackupEnvelopeException>(() => EscrowRecovery.OpenRestoreKey(restoreKey with { SealedKey = Convert.ToBase64String(sealedBytes) }, request, _pc));
    }

    [Fact]
    public async Task The_escrow_key_survives_its_pem_and_refuses_slots_sealed_to_another_key()
    {
        var reread = EscrowPrivateKey.FromPem("escrow-2026", _escrow.ExportPem());
        Assert.Equal(_escrow.PublicKey, reread.PublicKey);
        Assert.Contains("BEGIN PRIVATE KEY", _escrow.ExportPem());

        using var other = EscrowPrivateKey.Generate("escrow-2027");
        var (header, _, _) = await BackupAsync(other.PublicKey);
        var request = Request(header);
        Assert.Throws<BackupEnvelopeException>(() => RestoreKeyIssuer.Issue(Approve(request), [_escrow], Now));   // no key with that id

        using var impostor = EscrowPrivateKey.Generate("escrow-2027");                                       // right id, wrong key
        Assert.Throws<BackupEnvelopeException>(() => RestoreKeyIssuer.Issue(Approve(request), [impostor], Now));
        Assert.NotNull(RestoreKeyIssuer.Issue(Approve(request), [other], Now));
    }

    // ------------------------------------------------------------------ the offline tool

    private (int Code, string Output, string Error) Tool(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = EscrowRecoveryTool.Run(args, output, error, new global::Tests.Common.Security.TestClock(Now));
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void New_key_keeps_the_private_key_in_the_folder_prints_the_public_key_and_never_replaces_a_key()
    {
        var keys = Path.Combine(_folder, "keys");

        var made = Tool("new-key", "escrow-2026", keys);
        Assert.Equal(0, made.Code);
        var pem = File.ReadAllText(Path.Combine(keys, "escrow-2026" + EscrowRecoveryTool.KeyFileSuffix));
        var publicKey = EscrowPrivateKey.FromPem("escrow-2026", pem).PublicKey.PublicKey;
        Assert.Contains(publicKey, made.Output);
        Assert.DoesNotContain("PRIVATE KEY-----", made.Output);

        Assert.Equal(1, Tool("new-key", "escrow-2026", keys).Code);
        Assert.Equal(pem, File.ReadAllText(Path.Combine(keys, "escrow-2026" + EscrowRecoveryTool.KeyFileSuffix)));
        Assert.Equal(1, Tool("new-key", "..\\evil", keys).Code);
        Assert.Equal(2, Tool("new-key").Code);
        Assert.Equal(2, Tool().Code);
    }

    [Fact]
    public async Task Recover_turns_only_an_approved_recovery_into_a_restore_key_for_that_pc()
    {
        var keys = Path.Combine(_folder, "keys");
        Directory.CreateDirectory(keys);
        File.WriteAllText(Path.Combine(keys, "escrow-2026" + EscrowRecoveryTool.KeyFileSuffix), _escrow.ExportPem());
        var (header, file, database) = await BackupAsync();
        var request = Request(header);

        var requestFile = Path.Combine(_folder, "request.txt");
        File.WriteAllText(requestFile, EscrowRecovery.WriteRequest(request));
        var refused = Tool("recover", requestFile, keys);
        Assert.Equal(1, refused.Code);
        Assert.Contains("approve it in AdminPortal", refused.Error);
        Assert.Contains("NOT approved", Tool("show", requestFile).Output);

        var approvedFile = Path.Combine(_folder, "approved.txt");
        File.WriteAllText(approvedFile, EscrowRecovery.WriteApproved(Approve(request)));
        Assert.Equal(1, Tool("recover", approvedFile, Path.Combine(_folder, "no-keys")).Code);

        var done = Tool("recover", approvedFile, keys);
        Assert.Equal(0, done.Code);
        Assert.Contains("vendor-admin", done.Output);
        var restoreFile = Path.Combine(_folder, $"restore-key-{request.BackupId:N}.txt");
        Assert.True(EscrowRecovery.TryReadRestoreKey(File.ReadAllText(restoreFile), out var restoreKey, out _));
        Assert.Contains("Restore key", Tool("show", restoreFile).Output);

        using var plain = new MemoryStream();
        await BackupEnvelope.DecryptAsync(new MemoryStream(file), plain, EscrowRecovery.OpenRestoreKey(restoreKey!, request, _pc));
        Assert.Equal(database, plain.ToArray());

        var chosen = Path.Combine(_folder, "out", "key.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);
        Assert.Equal(0, Tool("recover", approvedFile, keys, chosen).Code);
        Assert.True(File.Exists(chosen));

        File.WriteAllText(approvedFile, File.ReadAllText(approvedFile).Replace("A", "B"));
        Assert.Equal(1, Tool("recover", approvedFile, keys).Code);
        Assert.Equal(1, Tool("show", approvedFile).Code);
        Assert.Equal(1, Tool("recover", Path.Combine(_folder, "missing.txt"), keys).Code);
    }
}
