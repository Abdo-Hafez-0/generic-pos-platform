using System.Collections.Concurrent;
using System.Security.Cryptography;
using LicenseServer.Application;
using Licensing.Contracts;

namespace LicenseServer.Infrastructure;

/// <summary>
/// ES256 signer (ECDSA P-256 / SHA-256, IEEE P1363). OWNS the private key; the key is never exposed - only the
/// PUBLIC key can be exported (so it can be distributed to clients as trusted key material).
/// </summary>
public sealed class EcdsaLicenseSigner : ILicenseSigner, IDisposable
{
    private readonly ECDsa _key;

    public EcdsaLicenseSigner(ECDsa key, string keyId)
    {
        _key = key;
        KeyId = keyId;
    }

    public string KeyId { get; }

    public string Algorithm => LicenseSigning.Algorithm;

    public byte[] Sign(byte[] data)
    {
        lock (_key)
        {
            return _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }

    /// <summary>Base64 DER SubjectPublicKeyInfo of the public key (safe to share; matches the client TrustedLicenseKey format).</summary>
    public string ExportPublicKey() => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>Creates a throw-away key (development/testing only). Nothing is persisted or committed.</summary>
    public static EcdsaLicenseSigner GenerateEphemeral(string keyId)
        => new(ECDsa.Create(ECCurve.NamedCurves.nistP256), keyId);

    /// <summary>Loads a PKCS#8 PEM private key from a file OUTSIDE the repository (production key management).</summary>
    public static EcdsaLicenseSigner FromPemFile(string path, string keyId)
    {
        var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(path));
        return new EcdsaLicenseSigner(ecdsa, keyId);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// In-memory license repository: the Stage 6 foundation store. Durable server persistence (and the license
/// administration that fills it) belongs to later work; this keeps the issuance logic fully testable.
/// </summary>
public sealed class InMemoryLicenseRepository : ILicenseRepository
{
    private readonly ConcurrentDictionary<Guid, LicenseRecord> _byId = new();

    public Task<LicenseRecord?> FindByActivationKeyAsync(string activationKey, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.Values.FirstOrDefault(r =>
            string.Equals(r.ActivationKey, activationKey, StringComparison.Ordinal)));

    public Task<LicenseRecord?> FindByIdAsync(Guid licenseId, CancellationToken cancellationToken = default)
        => Task.FromResult(_byId.TryGetValue(licenseId, out var r) ? r : null);

    public Task AddAsync(LicenseRecord record, CancellationToken cancellationToken = default)
    {
        _byId[record.LicenseId] = record;
        return Task.CompletedTask;
    }

    public Task SaveAsync(LicenseRecord record, CancellationToken cancellationToken = default)
    {
        _byId[record.LicenseId] = record;
        return Task.CompletedTask;
    }
}
