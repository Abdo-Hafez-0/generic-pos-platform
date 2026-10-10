using System.Security.Cryptography;
using CloudBackup.Contracts;
using CloudBackup.Domain;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Backup;
using Platform.Core.Results;
using Security.BackupEnvelope;

namespace CloudBackup.Application;

/// <summary>
/// The cloud side of backups (MISS-04e2, design sections 4, 5 and 9):
///   - the recovery code: made on this PC, shown once, two groups typed back before it is used; only the derived key is kept (key ring);
///   - the vendor's backup access key, typed in by the owner;
///   - every new local backup (<see cref="IBackupReplica"/>) is encrypted into the outbox - with the active shop key and the vendor's
///     escrow key - when cloud backup is licensed and set up; otherwise the reason is recorded and nothing else happens;
///   - <see cref="UploadDueAsync"/> sends what is waiting, oldest first, and spaces out retries; a cloud failure never touches the local
///     backup, and the plain copy is never written anywhere by this module.
/// Permissions are checked by the handlers; the background work asks the platform whether the module is licensed (IUnattendedWorkGate).
/// </summary>
public sealed class CloudBackupService(
    IKeyRing keys,
    ICloudAccessStore access,
    ICloudOutbox outbox,
    ICloudStatusStore status,
    IEscrowKeySource escrow,
    IBackupClient client,
    IUnattendedWorkGate license,
    TimeProvider clock,
    ILogger<CloudBackupService> logger,
    IBusinessEventSink? events = null,
    IUploadSignal? signal = null) : IBackupReplica
{
    public const string AuditModule = CloudBackupCapabilities.Module;
    public const int MaxWaiting = 10;
    private static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (Guid Id, byte[] Secret, int[] Groups, DateTimeOffset StartedAt)? _setup;

    // ------------------------------------------------------------------ recovery code

    /// <summary>
    /// Makes a new recovery code and shows it ONCE. It is not used until two of its groups are typed back (<see cref="ConfirmRecoveryCodeAsync"/>).
    /// When a code exists already this makes a new one (after a confirmation the old key still opens older backups on this PC).
    /// </summary>
    public Result<RecoveryCodeSetup> BeginRecoveryCode()
    {
        if (!keys.CanProtect)
            return Error.Failure(CloudBackupErrorCodes.CannotProtectKeys, "This computer cannot protect the backup key, so cloud backup cannot be set up here.");

        var (secret, text) = RecoveryCode.Generate();
        var first = RandomNumberGenerator.GetInt32(1, 7);
        var second = RandomNumberGenerator.GetInt32(1, 7);
        while (second == first) second = RandomNumberGenerator.GetInt32(1, 7);
        int[] groups = [Math.Min(first, second), Math.Max(first, second)];

        if (_setup is { } old) CryptographicOperations.ZeroMemory(old.Secret);
        var id = Guid.NewGuid();
        _setup = (id, secret, groups, clock.GetUtcNow());
        return new RecoveryCodeSetup(id, text, groups);
    }

    /// <summary>Checks the typed groups (1-based numbers given by <see cref="BeginRecoveryCode"/>) and, when they match, starts using the code.</summary>
    public async Task<Result<CloudKeyInfo>> ConfirmRecoveryCodeAsync(Guid setupId, IReadOnlyList<string> typedGroups, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_setup is not { } setup || setup.Id != setupId || clock.GetUtcNow() - setup.StartedAt > SetupLifetime)
                return Error.NotFound(CloudBackupErrorCodes.SetupExpired, "That recovery code is no longer waiting for confirmation. Make a new one.");

            var groups = RecoveryCode.Format(setup.Secret).Split('-');
            var matches = typedGroups.Count == setup.Groups.Length
                          && setup.Groups.Select((g, i) => Normalize(typedGroups[i]) == groups[g - 1]).All(ok => ok);
            if (!matches)
                return Error.Validation(CloudBackupErrorCodes.ConfirmationMismatch, "The groups typed do not match the recovery code. Check what you wrote down.");

            var replacing = (await keys.ListAsync(cancellationToken)).Count > 0;
            var key = BackupKey.FromRecoveryCode(setup.Secret);
            var now = clock.GetUtcNow();
            await keys.AddActiveAsync(key, now, cancellationToken);
            CryptographicOperations.ZeroMemory(setup.Secret);
            _setup = null;

            await events.TryRecordAsync(BusinessEvent.Create(AuditModule, replacing ? "cloud-backup.recovery-code-changed" : "cloud-backup.recovery-code-created",
                "cloud-backup-key", key.KeyId, replacing
                    ? "A new recovery code was made; new cloud backups use it, older ones still need the previous code on another PC."
                    : "The recovery code for cloud backups was made and confirmed."));
            return new CloudKeyInfo(key.KeyId, now, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string Normalize(string? group)
        => new string((group ?? string.Empty).ToUpperInvariant().Where(c => c is not ('-' or ' ')).Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray());

    // ------------------------------------------------------------------ access key

    /// <summary>Stores the vendor's backup access key (empty removes it). Never logged or audited with its value.</summary>
    public async Task<Result> SetAccessAsync(string? value, CancellationToken cancellationToken = default)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed) && (trimmed.Length < 16 || trimmed.Any(char.IsWhiteSpace)))
            return Error.Validation(CloudBackupErrorCodes.InvalidToken, "That does not look like a backup access key. Copy it exactly as the vendor gave it.");
        if (!keys.CanProtect)
            return Error.Failure(CloudBackupErrorCodes.CannotProtectKeys, "This computer cannot protect the access key, so cloud backup cannot be set up here.");

        await access.WriteAsync(string.IsNullOrEmpty(trimmed) ? null : trimmed, cancellationToken);
        await events.TryRecordAsync(BusinessEvent.Create(AuditModule, string.IsNullOrEmpty(trimmed) ? "cloud-backup.access-removed" : "cloud-backup.access-set",
            "cloud-backup-access", null, string.IsNullOrEmpty(trimmed) ? "The backup access key was removed." : "The backup access key was entered."));
        signal?.Raise();
        return Result.Success();
    }

    // ------------------------------------------------------------------ the copy of every new backup

    /// <summary>Why a backup would NOT be copied to the cloud right now, or null when it would.</summary>
    public async Task<CloudSkipReason?> WhyNotReadyAsync(CancellationToken cancellationToken = default)
    {
        if (!license.IsLicensed(CloudBackupCapabilities.Manage)) return CloudSkipReason.NotLicensed;
        if (await keys.GetActiveAsync(cancellationToken) is null) return CloudSkipReason.NoRecoveryCode;
        if (string.IsNullOrEmpty(await access.ReadAsync(cancellationToken))) return CloudSkipReason.NoToken;
        if (escrow.Current is null) return CloudSkipReason.NoEscrowKey;
        return null;
    }

    /// <summary>
    /// Encrypts the fresh local backup into the outbox (written as ".part", renamed when complete). Best effort by contract: never throws,
    /// and the local backup is already safe whatever happens here. At most <see cref="MaxWaiting"/> copies wait; the oldest waiting copy
    /// makes room for a newer one (the server keeps the newest ten anyway).
    /// </summary>
    public async Task ReplicateAsync(string snapshotPath, BackupCopy copy, CancellationToken cancellationToken = default)
    {
        string? part = null;
        try
        {
            if (await WhyNotReadyAsync(cancellationToken) is { } reason)
            {
                await UpdateStatusAsync(s => s with { LastSkip = reason, LastSkipAt = clock.GetUtcNow() });
                logger.LogInformation("Backup {File} was not copied to the cloud: {Reason}.", copy.FileName, reason);
                return;
            }

            var key = (await keys.GetActiveAsync(cancellationToken))!;
            System.IO.Directory.CreateDirectory(outbox.Directory);
            var fileName = copy.BackupId.ToString("N") + BackupEnvelope.Extension;
            var target = Path.Combine(outbox.Directory, fileName);
            part = target + ".part";

            await using (var source = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await BackupEnvelope.WriteAsync(source, output, key, escrow.Current!,
                    new EnvelopeMetadata(copy.ApplicationVersion, copy.Migrations, copy.SizeBytes, copy.Sha256), copy.CreatedAt, cancellationToken);

            File.Move(part, target, overwrite: true);
            part = null;
            var size = new FileInfo(target).Length;
            string sha256;
            await using (var written = File.OpenRead(target))
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, cancellationToken));

            await _gate.WaitAsync(cancellationToken);
            try
            {
                var waiting = (await outbox.ListAsync(cancellationToken)).Where(e => e.BackupId != copy.BackupId).ToList();
                waiting.Add(new OutboxEntry(copy.BackupId, fileName, copy.FileName, copy.CreatedAt, size, sha256, key.KeyId, 0, clock.GetUtcNow()));
                foreach (var dropped in waiting.OrderBy(e => e.CreatedAt).Take(Math.Max(0, waiting.Count - MaxWaiting)).ToList())
                {
                    waiting.Remove(dropped);
                    TryDelete(Path.Combine(outbox.Directory, dropped.FileName));
                    logger.LogWarning("The cloud copy of {File} was dropped without being uploaded: more than {Max} copies were waiting.", dropped.SourceFileName, MaxWaiting);
                }

                await outbox.SaveAsync(waiting, CancellationToken.None);
            }
            finally
            {
                _gate.Release();
            }

            await UpdateStatusAsync(s => s with { LastSkip = null, LastSkipAt = null });
            logger.LogInformation("Backup {File} encrypted for the cloud ({Bytes} bytes); it is uploaded in the background.", copy.FileName, size);
            signal?.Raise();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup {File} could not be prepared for the cloud; the local backup is fine.", copy.FileName);
            await UpdateStatusAsync(s => s with { LastFailure = "The backup could not be prepared for the cloud.", LastFailureAt = clock.GetUtcNow() });
        }
        finally
        {
            if (part is not null) TryDelete(part);
        }
    }

    // ------------------------------------------------------------------ upload

    /// <summary>
    /// Uploads what is due, oldest first. Stops at the first failure (the server is unreachable or refuses; the rest waits for the next
    /// round) and spaces out retries (1, 5, 15 minutes, then hourly). Returns how many copies were uploaded.
    /// </summary>
    public async Task<int> UploadDueAsync(string clientVersion, CancellationToken cancellationToken = default)
    {
        if (!license.IsLicensed(CloudBackupCapabilities.Manage)) return 0;
        var secret = await access.ReadAsync(cancellationToken);
        if (string.IsNullOrEmpty(secret)) return 0;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            var entries = (await outbox.ListAsync(cancellationToken)).ToList();
            var uploaded = 0;
            foreach (var entry in entries.Where(e => e.NextAttemptAt <= now).OrderBy(e => e.CreatedAt).ToList())
            {
                var path = Path.Combine(outbox.Directory, entry.FileName);
                if (!File.Exists(path))
                {
                    entries.Remove(entry);
                    continue;
                }

                Result<CloudBackupItem> sent;
                await using (var content = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
                    sent = await client.UploadAsync(secret, content, entry.Sha256, entry.SourceFileName, clientVersion, cancellationToken);

                if (sent.IsFailure)
                {
                    entries[entries.IndexOf(entry)] = entry.Failed(now, sent.Error.Description);
                    await UpdateStatusAsync(s => s with { LastFailure = sent.Error.Description, LastFailureAt = now });
                    logger.LogWarning("Uploading the cloud copy of {File} failed ({Code}); it is tried again later.", entry.SourceFileName, sent.Error.Code);
                    if (entry.Attempts + 1 == 3)
                        await events.TryRecordAsync(BusinessEvent.Create(AuditModule, "cloud-backup.upload-failing", "backup", entry.BackupId.ToString(),
                            $"The cloud copy of {entry.SourceFileName} could not be uploaded after 3 attempts: {sent.Error.Description}") with { ActorName = "cloud backup" });
                    break;
                }

                entries.Remove(entry);
                TryDelete(path);
                uploaded++;
                await UpdateStatusAsync(s => s with { LastUploadAt = now, LastUploadedFile = entry.SourceFileName, LastFailure = null, LastFailureAt = null });
                await events.TryRecordAsync(BusinessEvent.Create(AuditModule, "cloud-backup.uploaded", "backup", entry.BackupId.ToString(),
                    $"The encrypted copy of {entry.SourceFileName} is in the cloud ({sent.Value.SizeBytes} bytes).") with { ActorName = "cloud backup" });
            }

            await outbox.SaveAsync(entries, CancellationToken.None);
            return uploaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ------------------------------------------------------------------ status

    public async Task<CloudBackupOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => new(await WhyNotReadyAsync(cancellationToken), await keys.ListAsync(cancellationToken), !string.IsNullOrEmpty(await access.ReadAsync(cancellationToken)),
            escrow.Current is not null, (await outbox.ListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList(), await status.ReadAsync(cancellationToken));

    private async Task UpdateStatusAsync(Func<CloudBackupStatus, CloudBackupStatus> change)
    {
        try
        {
            await status.WriteAsync(change(await status.ReadAsync()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The cloud backup status could not be saved.");
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "{Path} could not be removed.", path);
        }
    }
}

/// <summary>Everything the cloud backup screen shows (MISS-04e3): readiness, keys, whether an access key is entered, the waiting copies, the last results.</summary>
public sealed record CloudBackupOverview(
    CloudSkipReason? NotReadyBecause, IReadOnlyList<CloudKeyInfo> Keys, bool HasAccessKey, bool HasEscrowKey, IReadOnlyList<OutboxEntry> Waiting, CloudBackupStatus Status);
