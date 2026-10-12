using System.Security.Cryptography;
using System.Text;

namespace Security.BackupEnvelope.Recovery;

/// <summary>
/// The vendor's escrow key pair (design 5B): ECDH P-256, used for nothing else. The private key is created and kept OFFLINE (the machine
/// that runs tools/BackupEscrowRecovery); only <see cref="PublicKey"/> and the key id ship with the desktop (CloudBackup:EscrowKeys).
/// </summary>
public sealed class EscrowPrivateKey : IDisposable
{
    private readonly ECDiffieHellman _key;

    private EscrowPrivateKey(string keyId, ECDiffieHellman key)
    {
        if (string.IsNullOrWhiteSpace(keyId)) throw new ArgumentException("An escrow key needs an id.", nameof(keyId));
        if (key.KeySize != 256) throw new ArgumentException("An escrow key is a P-256 key.", nameof(key));
        KeyId = keyId.Trim();
        _key = key;
    }

    public string KeyId { get; }

    /// <summary>What the desktop is configured with: base64 DER SubjectPublicKeyInfo.</summary>
    public EscrowPublicKey PublicKey => new(KeyId, Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()));

    public static EscrowPrivateKey Generate(string keyId) => new(keyId, ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Reads a PKCS#8 PEM private key written by <see cref="ExportPem"/> or by openssl.</summary>
    public static EscrowPrivateKey FromPem(string keyId, string pem)
    {
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportFromPem(pem);
            return new EscrowPrivateKey(keyId, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    public string ExportPem() => new(PemEncoding.Write("PRIVATE KEY", _key.ExportPkcs8PrivateKey()));

    /// <summary>
    /// Opens an escrow slot: the one backup's data key. Exactly the derivation of <see cref="BackupEnvelope"/> (EscrowKek, OpenSealed).
    /// Throws <see cref="BackupEnvelopeException"/> (WrongKey) when the slot was sealed to another key or was changed.
    /// </summary>
    public byte[] OpenSlot(EscrowSlot slot)
    {
        if (!string.Equals(slot.KeyId, KeyId, StringComparison.Ordinal))
            throw new BackupEnvelopeException(EnvelopeProblem.WrongKey, $"The backup was sealed to escrow key '{slot.KeyId}', not '{KeyId}'.");

        byte[]? shared = null, kek = null;
        try
        {
            var ephemeralPublic = Convert.FromBase64String(slot.EphemeralPublicKey);
            using var ephemeral = ECDiffieHellman.Create();
            ephemeral.ImportSubjectPublicKeyInfo(ephemeralPublic, out _);
            shared = _key.DeriveRawSecretAgreement(ephemeral.PublicKey);
            kek = BackupEnvelope.EscrowKek(shared, ephemeralPublic, slot.KeyId);
            return BackupEnvelope.OpenSealed(kek, Convert.FromBase64String(slot.SealedKey), Encoding.UTF8.GetBytes("escrow:" + slot.KeyId));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new BackupEnvelopeException(EnvelopeProblem.WrongKey, "The escrow slot does not open with this key (changed, or sealed to another key).", ex);
        }
        finally
        {
            if (shared is not null) CryptographicOperations.ZeroMemory(shared);
            if (kek is not null) CryptographicOperations.ZeroMemory(kek);
        }
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Turns an APPROVED recovery into a restore key for that one backup (the data key is never written anywhere).</summary>
public static class RestoreKeyIssuer
{
    public static RestoreKey Issue(ApprovedRecovery approved, IEnumerable<EscrowPrivateKey> keys, DateTimeOffset now)
    {
        var request = approved.Request;
        var key = keys.FirstOrDefault(k => string.Equals(k.KeyId, request.Escrow.KeyId, StringComparison.Ordinal))
                  ?? throw new BackupEnvelopeException(EnvelopeProblem.WrongKey, $"The escrow key '{request.Escrow.KeyId}' this backup was sealed to is not among the keys given.");

        var dataKey = key.OpenSlot(request.Escrow);
        try
        {
            return EscrowRecovery.SealRestoreKey(dataKey, request, now);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }
}
