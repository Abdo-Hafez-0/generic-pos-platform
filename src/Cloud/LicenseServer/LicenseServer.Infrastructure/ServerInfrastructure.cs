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
/// In-memory license repository: the Stage 6 foundation store, kept as the Development-only default and for tests. The durable
/// store (Stage 9) is Cloud.Infrastructure's EfLicenseRepository, filled by the administration host.
/// </summary>
public sealed class InMemoryLicenseRepository : ILicenseRepository, ILicenseQuery
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

    public Task<LicensePage> ListAsync(LicenseFilter filter, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var all = Filter(filter).OrderBy(r => r.ValidFrom).ThenBy(r => r.LicenseId).ToList();
        var items = all.Skip((Math.Max(page, 1) - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new LicensePage(items, all.Count));
    }

    public Task<int> CountAsync(LicenseFilter filter, CancellationToken cancellationToken = default)
        => Task.FromResult(Filter(filter).Count());

    private IEnumerable<LicenseRecord> Filter(LicenseFilter f)
        => _byId.Values.Where(r => (f.CustomerId is null || r.CustomerId == f.CustomerId)
                                && (f.Status is null || r.Status == f.Status)
                                && (f.Bound is null || (r.InstallationId is not null) == f.Bound));
}
