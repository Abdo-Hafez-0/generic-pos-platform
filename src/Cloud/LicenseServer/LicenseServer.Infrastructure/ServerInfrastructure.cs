using System.Collections.Concurrent;
using LicenseServer.Application;
using Licensing.Contracts;
using Security.Es256.Signing;

namespace LicenseServer.Infrastructure;

/// <summary>
/// ILicenseSigner backed by the shared ES256 signer (Security.Es256.Signing). OWNS the private key; only the PUBLIC key
/// can be exported (so it can be distributed to clients as trusted key material).
/// </summary>
public sealed class EcdsaLicenseSigner : ILicenseSigner, IDisposable
{
    private readonly Es256Signer _signer;

    private EcdsaLicenseSigner(Es256Signer signer) => _signer = signer;

    public string KeyId => _signer.KeyId;

    public string Algorithm => LicenseSigning.Algorithm;

    public byte[] Sign(byte[] data) => _signer.Sign(data);

    /// <summary>Base64 DER SubjectPublicKeyInfo of the public key (safe to share; matches the client TrustedLicenseKey format).</summary>
    public string ExportPublicKey() => _signer.ExportPublicKey();

    /// <summary>Creates a throw-away key (development/testing only). Nothing is persisted or committed.</summary>
    public static EcdsaLicenseSigner GenerateEphemeral(string keyId) => new(Es256Signer.GenerateEphemeral(keyId));

    /// <summary>Loads a PKCS#8 PEM private key from a file OUTSIDE the repository (production key management).</summary>
    public static EcdsaLicenseSigner FromPemFile(string path, string keyId) => new(Es256Signer.FromPemFile(path, keyId));

    public void Dispose() => _signer.Dispose();
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
