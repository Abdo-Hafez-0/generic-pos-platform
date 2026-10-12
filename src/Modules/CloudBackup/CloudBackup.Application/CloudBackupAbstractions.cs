using CloudBackup.Domain;
using Platform.Application.Abstractions.Authorization;
using Security.BackupEnvelope;

namespace CloudBackup.Application;

/// <summary>
/// The cloud backup capabilities. Setting up and running cloud backup needs the license module <c>cloud-backup</c>; restoring your own
/// data from the cloud never does (rule 10 - the server, too, lets a lapsed license read and restore its backups).
/// </summary>
public static class CloudBackupCapabilities
{
    public const string Module = "cloud-backup";

    /// <summary>Set up the recovery code (and make a new one), enter the vendor's backup access key, see the cloud status.</summary>
    public const string Manage = "backup.cloud.manage";

    /// <summary>Restore from a cloud backup (MISS-04e3).</summary>
    public const string Restore = "backup.cloud.restore";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
        new(Manage, Module, "Manage cloud backup", "Set up the recovery code and the vendor's backup access key, and see what was copied to the cloud.", LicenseRequirement.Module, IsSensitive: true),
        new(Restore, Module, "Restore from the cloud", "Replace the shop's data with a backup kept in the cloud.", LicenseRequirement.None, IsSensitive: true),
    ];
}

public sealed class CloudBackupCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => CloudBackupCapabilities.All;
}

/// <summary>
/// The shop keys this PC holds, protected for this Windows user (design 5A): the active one encrypts new backups, older ones still open
/// older backups. The recovery code itself is never stored.
/// </summary>
public interface IKeyRing
{
    /// <summary>False when this computer offers no way to protect the keys (cloud backup cannot be set up here).</summary>
    bool CanProtect { get; }

    Task<IReadOnlyList<CloudKeyInfo>> ListAsync(CancellationToken cancellationToken = default);

    Task<BackupKey?> GetActiveAsync(CancellationToken cancellationToken = default);

    Task<BackupKey?> FindAsync(string keyId, CancellationToken cancellationToken = default);

    /// <summary>Adds a key and makes it the active one; earlier keys stay for reading older backups.</summary>
    Task AddActiveAsync(BackupKey key, DateTimeOffset createdAt, CancellationToken cancellationToken = default);
}

/// <summary>The per-license backup access key the vendor issued (typed in once by the owner, design decision 6), kept protected.</summary>
public interface ICloudAccessStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(string? access, CancellationToken cancellationToken = default);
}

/// <summary>The encrypted copies waiting for upload.</summary>
public interface ICloudOutbox
{
    string Directory { get; }

    Task<IReadOnlyList<OutboxEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(IReadOnlyList<OutboxEntry> entries, CancellationToken cancellationToken = default);
}

public interface ICloudStatusStore
{
    Task<CloudBackupStatus> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(CloudBackupStatus status, CancellationToken cancellationToken = default);
}

/// <summary>The vendor's escrow public key new backups are sealed to (configuration; moves into the signed binary with PKG-05).</summary>
public interface IEscrowKeySource
{
    EscrowPublicKey? Current { get; }
}

/// <summary>Where a cloud backup is downloaded and decrypted for a restore (emptied at every start: a plain copy never lingers).</summary>
public sealed record CloudDownloadArea(string Directory);

/// <summary>
/// A recovery request this PC made (MISS-04f) with the PKCS#8 private half of its one-time key pair. <paramref name="UsedAt"/>: a restore
/// with its restore key was confirmed (the request is removed at the next start, when that restore has run).
/// </summary>
public sealed record StoredRecoveryRequest(RecoveryRequest Request, byte[] PrivateKey, DateTimeOffset? UsedAt = null);

/// <summary>The recovery requests this PC made, one per backup, kept protected for this Windows user (ISecretProtector) until used.</summary>
public interface IRecoveryRequestStore
{
    Task<IReadOnlyList<StoredRecoveryRequest>> ListAsync(CancellationToken cancellationToken = default);

    Task<StoredRecoveryRequest?> FindAsync(Guid backupId, CancellationToken cancellationToken = default);

    /// <summary>Stores it, replacing an earlier request for the same backup (whose restore key then no longer opens anything).</summary>
    Task SaveAsync(StoredRecoveryRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid backupId, CancellationToken cancellationToken = default);
}

/// <summary>What the shell needs to ask for a new recovery code after a restore with the vendor's restore key (MISS-04f).</summary>
public interface ICloudBackupNotice
{
    /// <summary>Raised (on any thread) when the answer may have changed.</summary>
    event EventHandler? Changed;

    Task<bool> NeedsNewRecoveryCodeAsync(CancellationToken cancellationToken = default);
}

/// <summary>Tells the background uploader there is something new to send (so it does not wait for its next round).</summary>
public interface IUploadSignal
{
    void Raise();
}
