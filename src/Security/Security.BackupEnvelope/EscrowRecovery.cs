using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Security.BackupEnvelope;

/// <summary>
/// The recovery request a PC makes when a cloud backup must be opened without the recovery code (MISS-04f, design 5B, user decisions of
/// 2026-10-12). It names ONE backup and carries what the vendor needs to re-seal that backup's data key to this PC: the backup's escrow
/// slot (from its authenticated header) and the public half of a one-time key pair whose private half never leaves the PC. It holds no
/// business data and is useless without the vendor's escrow private key.
/// </summary>
/// <param name="LicenseId">The license the PC is activated with, when it is (the vendor checks it against the backup's owner).</param>
/// <param name="RequestKey">Base64 DER SubjectPublicKeyInfo of the PC's one-time ECDH P-256 key.</param>
public sealed record RecoveryRequest(
    int Version, Guid RequestId, string? LicenseId, Guid BackupId, DateTimeOffset BackupCreatedAt, string? BackupLabel, string KeyId,
    EscrowSlot Escrow, string RequestKey, DateTimeOffset RequestedAt);

/// <summary>
/// A recovery request the vendor approved in AdminPortal after the written identity check: the slot was compared with the STORED backup's
/// header and the check is in the admin audit log. Only this text is accepted by the offline tool (tools/BackupEscrowRecovery).
/// </summary>
public sealed record ApprovedRecovery(
    int Version, RecoveryRequest Request, Guid LicenseId, string CustomerId, DateTimeOffset ApprovedAt, string ApprovedBy, string CheckMethod);

/// <summary>
/// What the vendor's tool gives back: the one backup's data key sealed to the asking PC's one-time key (ECIES: ephemeral ECDH P-256,
/// HKDF-SHA256, AES-256-GCM). It opens that backup only, and only on the PC that holds the request's private half.
/// </summary>
public sealed record RestoreKey(int Version, Guid RequestId, Guid BackupId, string KeyId, string EphemeralPublicKey, string SealedKey, DateTimeOffset IssuedAt);

/// <summary>Why a pasted text could not be read.</summary>
public enum RecoveryTextProblem
{
    /// <summary>No block of the expected kind (another kind of text, or not copied completely).</summary>
    Missing,

    /// <summary>The block is there but its content does not read (a character changed or lost).</summary>
    Damaged,

    /// <summary>Made by a newer version.</summary>
    UnsupportedVersion,
}

/// <summary>
/// The three texts of a vendor recovery and the crypto between them. Each text is a few readable lines (for people) followed by an armored
/// block (for programs): base64 of canonical JSON between BEGIN/END lines. Only the block counts; the readable lines are ignored on reading.
/// </summary>
public static class EscrowRecovery
{
    public const int FormatVersion = 1;
    public const string RequestKind = "RECOVERY REQUEST";
    public const string ApprovedKind = "APPROVED RECOVERY";
    public const string RestoreKeyKind = "RESTORE KEY";
    private const int MaxBlockBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ the request (on the PC)

    /// <summary>The request for a backup whose header was read from the cloud copy; <paramref name="requestPublicKey"/> is the PC's one-time public key.</summary>
    public static RecoveryRequest CreateRequest(EnvelopeHeader header, Guid backupId, string? backupLabel, string? licenseId, byte[] requestPublicKey, DateTimeOffset now)
    {
        RequirePublicP256(requestPublicKey);
        return new RecoveryRequest(FormatVersion, Guid.NewGuid(), string.IsNullOrWhiteSpace(licenseId) ? null : licenseId.Trim(), backupId, header.CreatedAt,
            string.IsNullOrWhiteSpace(backupLabel) ? null : backupLabel.Trim(), header.KeyId, header.Escrow, Convert.ToBase64String(requestPublicKey), now);
    }

    /// <summary>True when the request names exactly this backup header (same key id, same escrow slot).</summary>
    public static bool Matches(RecoveryRequest request, EnvelopeHeader header)
        => string.Equals(request.KeyId, header.KeyId, StringComparison.Ordinal) && request.Escrow == header.Escrow;

    // ------------------------------------------------------------------ the restore key (vendor seals, PC opens)

    /// <summary>Seals one backup's data key to the PC that asked (public key only; the vendor's tool calls it after opening the escrow slot).</summary>
    public static RestoreKey SealRestoreKey(byte[] dataKey, RecoveryRequest request, DateTimeOffset issuedAt)
    {
        if (dataKey.Length != 32) throw new ArgumentException("A backup data key is 256 bits.", nameof(dataKey));
        var requestKey = Convert.FromBase64String(request.RequestKey);
        RequirePublicP256(requestKey);

        using var pc = ECDiffieHellman.Create();
        pc.ImportSubjectPublicKeyInfo(requestKey, out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralPublic = ephemeral.ExportSubjectPublicKeyInfo();
        var shared = ephemeral.DeriveRawSecretAgreement(pc.PublicKey);
        var kek = RestoreKek(shared, ephemeralPublic, request.RequestId, request.BackupId, request.KeyId);
        try
        {
            var sealedKey = BackupEnvelope.Seal(kek, RandomNumberGenerator.GetBytes(12), dataKey, Binding(request.RequestId, request.BackupId, request.KeyId), includeNonce: true);
            return new RestoreKey(FormatVersion, request.RequestId, request.BackupId, request.KeyId, Convert.ToBase64String(ephemeralPublic), Convert.ToBase64String(sealedKey), issuedAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>
    /// Opens a restore key with the PC's one-time private key (held by the caller, never by this library). WrongKey when the restore key was
    /// made for another request, another backup, or does not open with this key.
    /// </summary>
    public static byte[] OpenRestoreKey(RestoreKey restoreKey, RecoveryRequest request, ECDiffieHellman requestPrivateKey)
    {
        if (restoreKey.RequestId != request.RequestId || restoreKey.BackupId != request.BackupId || !string.Equals(restoreKey.KeyId, request.KeyId, StringComparison.Ordinal))
            throw new BackupEnvelopeException(EnvelopeProblem.WrongKey, "The restore key was made for another request or another backup.");

        byte[]? shared = null, kek = null;
        try
        {
            var ephemeralPublic = Convert.FromBase64String(restoreKey.EphemeralPublicKey);
            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
            shared = requestPrivateKey.DeriveRawSecretAgreement(ephemeral.PublicKey);
            kek = RestoreKek(shared, ephemeralPublic, restoreKey.RequestId, restoreKey.BackupId, restoreKey.KeyId);
            return BackupEnvelope.Open(kek, Convert.FromBase64String(restoreKey.SealedKey), Binding(restoreKey.RequestId, restoreKey.BackupId, restoreKey.KeyId));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new BackupEnvelopeException(EnvelopeProblem.WrongKey, "The restore key does not open on this computer.", ex);
        }
        finally
        {
            if (shared is not null) CryptographicOperations.ZeroMemory(shared);
            if (kek is not null) CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static byte[] RestoreKek(byte[] shared, byte[] ephemeralPublic, Guid requestId, Guid backupId, string keyId)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, ephemeralPublic, Encoding.UTF8.GetBytes($"GenericPOS restore v1:{requestId:N}:{backupId:N}:{keyId}"));

    private static byte[] Binding(Guid requestId, Guid backupId, string keyId) => Encoding.UTF8.GetBytes($"restore:{requestId:N}:{backupId:N}:{keyId}");

    private static void RequirePublicP256(byte[] spki)
    {
        using var key = ECDiffieHellman.Create();
        key.ImportSubjectPublicKeyInfo(spki, out _);
        if (key.KeySize != 256) throw new ArgumentException("The request key must be a P-256 key.");
    }

    // ------------------------------------------------------------------ texts

    public static string WriteRequest(RecoveryRequest request) => Write(RequestKind, request,
    [
        "GenericPOS - request to recover a cloud backup",
        $"Backup:    {request.BackupCreatedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC{(request.BackupLabel is null ? "" : "  (" + Readable(request.BackupLabel) + ")")}",
        $"Backup id: {request.BackupId}",
        $"Key id:    {request.KeyId}",
        $"License:   {request.LicenseId ?? "(this computer is not activated)"}",
        "Send this whole text to your vendor. It contains no business data.",
    ]);

    public static string WriteApproved(ApprovedRecovery approved) => Write(ApprovedKind, approved,
    [
        "GenericPOS - approved recovery (for the offline escrow tool only)",
        $"Backup id:   {approved.Request.BackupId}",
        $"License:     {approved.LicenseId}  customer {approved.CustomerId}",
        $"Approved by: {approved.ApprovedBy} at {approved.ApprovedAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC ({approved.CheckMethod})",
    ]);

    public static string WriteRestoreKey(RestoreKey key) => Write(RestoreKeyKind, key,
    [
        "GenericPOS - restore key for one cloud backup",
        $"Backup id: {key.BackupId}",
        "Paste this whole text on the Cloud backup screen of the computer that asked for it. It works there only, for this backup only.",
    ]);

    public static bool TryReadRequest(string? text, out RecoveryRequest? request, out RecoveryTextProblem problem)
        => TryRead(text, RequestKind, r => r.Version, r => r.Escrow is not null && !string.IsNullOrEmpty(r.KeyId) && !string.IsNullOrEmpty(r.RequestKey), out request, out problem);

    public static bool TryReadApproved(string? text, out ApprovedRecovery? approved, out RecoveryTextProblem problem)
        => TryRead(text, ApprovedKind, a => a.Version, a => a.Request is { Escrow: not null } r && !string.IsNullOrEmpty(r.KeyId) && !string.IsNullOrEmpty(r.RequestKey) && r.Version == FormatVersion,
            out approved, out problem);

    public static bool TryReadRestoreKey(string? text, out RestoreKey? key, out RecoveryTextProblem problem)
        => TryRead(text, RestoreKeyKind, k => k.Version, k => !string.IsNullOrEmpty(k.KeyId) && !string.IsNullOrEmpty(k.SealedKey) && !string.IsNullOrEmpty(k.EphemeralPublicKey),
            out key, out problem);

    /// <summary>A label in a readable line: one line, and never something that looks like a block marker.</summary>
    private static string Readable(string value) => new string(value.Where(c => !char.IsControl(c)).ToArray()).Replace("-----", "-");

    /// <summary>The block's last line: '#' and the first 8 hex digits of the SHA-256 of the content, so any change is reported as damage.</summary>
    private const char CheckMark = '#';

    private static string Check(byte[] json) => Convert.ToHexStringLower(SHA256.HashData(json))[..8];

    private static string Begin(string kind) => $"-----BEGIN GENERICPOS {kind}-----";

    private static string End(string kind) => $"-----END GENERICPOS {kind}-----";

    private static string Write<T>(string kind, T value, IEnumerable<string> summary)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        var body = Convert.ToBase64String(json);
        var text = new StringBuilder();
        foreach (var line in summary) text.Append(line).Append("\r\n");
        text.Append("\r\n").Append(Begin(kind)).Append("\r\n");
        for (var i = 0; i < body.Length; i += 64) text.Append(body, i, Math.Min(64, body.Length - i)).Append("\r\n");
        text.Append(CheckMark).Append(Check(json)).Append("\r\n");
        text.Append(End(kind)).Append("\r\n");
        return text.ToString();
    }

    /// <summary>Finds the block of this kind anywhere in the text (mail clients add spaces, quote marks or line breaks; all whitespace in the block is ignored).</summary>
    private static bool TryRead<T>(string? text, string kind, Func<T, int> version, Func<T, bool> complete, out T? value, out RecoveryTextProblem problem) where T : class
    {
        value = null;
        problem = RecoveryTextProblem.Missing;
        if (string.IsNullOrEmpty(text)) return false;
        var begin = text.IndexOf(Begin(kind), StringComparison.Ordinal);
        if (begin < 0) return false;
        begin += Begin(kind).Length;
        var end = text.IndexOf(End(kind), begin, StringComparison.Ordinal);
        if (end < 0) return false;

        problem = RecoveryTextProblem.Damaged;
        var block = new string(text[begin..end].Where(c => !char.IsWhiteSpace(c) && c != '>').ToArray());
        var mark = block.LastIndexOf(CheckMark);
        if (mark <= 0 || block.Length > MaxBlockBytes * 4 / 3 + 16) return false;
        try
        {
            var json = Convert.FromBase64String(block[..mark]);
            if (!string.Equals(block[(mark + 1)..], Check(json), StringComparison.OrdinalIgnoreCase)) return false;   // a character changed, lost or added
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (document.RootElement.TryGetProperty("version", out var v) && v.TryGetInt32(out var number) && number > FormatVersion)
            {
                problem = RecoveryTextProblem.UnsupportedVersion;
                return false;
            }

            var read = document.RootElement.Deserialize<T>(Json);
            if (read is null || version(read) != FormatVersion || !complete(read)) return false;
            value = read;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }
}
