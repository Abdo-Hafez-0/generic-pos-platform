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
    IUploadSignal? signal = null,
    CloudDownloadArea? downloads = null,
    IRecoveryRequestStore? requests = null) : IBackupReplica, ICloudBackupNotice
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
            Changed?.Invoke(this, EventArgs.Empty);   // a restore with the vendor's key asked for this code (MISS-04f)
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

    // ------------------------------------------------------------------ restore from the cloud (MISS-04e3)

    /// <summary>The backups the cloud keeps for this shop, newest first. Works in every license state (rule 10).</summary>
    public async Task<Result<IReadOnlyList<CloudBackupItem>>> ListCloudAsync(CancellationToken cancellationToken = default)
    {
        var secret = await access.ReadAsync(cancellationToken);
        if (string.IsNullOrEmpty(secret))
            return Error.Validation(CloudBackupErrorCodes.NotSetUp, "Enter the backup access key first; the vendor gives it with the license.");

        var listed = await client.ListAsync(secret, cancellationToken);
        return listed.IsFailure ? listed : Result.Success<IReadOnlyList<CloudBackupItem>>(listed.Value.OrderByDescending(b => b.CreatedAt).ToList());
    }

    /// <summary>
    /// Downloads a cloud backup and decrypts it into a plain database file for the restore (the caller hands the file to the local restore
    /// and then discards it). The key comes from this PC's key ring; on another PC, or for a backup made with an older code, the recovery
    /// code is typed. The file's time is set to when the backup was made, so the restore question names the right moment.
    /// </summary>
    public async Task<Result<DownloadedBackup>> DownloadForRestoreAsync(Guid backupId, string? recoveryCode, string? label = null, CancellationToken cancellationToken = default)
    {
        BackupKey? typed = null;
        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            if (!RecoveryCode.TryParse(recoveryCode, out var codeSecret))
                return Error.Validation(CloudBackupErrorCodes.WrongRecoveryCode, "That is not a recovery code, or a character is mistyped. Check it against what was written down.");
            typed = BackupKey.FromRecoveryCode(codeSecret);
        }

        return await DownloadAndOpenAsync(backupId, label, async (header, ct) =>
        {
            var key = typed ?? await keys.FindAsync(header.KeyId, ct);
            if (key is null)
                return Error.Validation(CloudBackupErrorCodes.NeedsRecoveryCode, "Type the recovery code this backup was made with.");
            if (key.KeyId != header.KeyId)
                return Error.Validation(CloudBackupErrorCodes.WrongRecoveryCode, "This backup was made with another recovery code.");
            return BackupEnvelope.UnwrapDataKey(header, key);
        }, cancellationToken);
    }

    /// <summary>
    /// Downloads a cloud backup and decrypts it with the data key <paramref name="dataKeyFor"/> gives for its header, into a plain database
    /// file named after the backup. Nothing is left behind on any refusal; the encrypted download is always removed.
    /// </summary>
    private async Task<Result<DownloadedBackup>> DownloadAndOpenAsync(Guid backupId, string? label,
        Func<EnvelopeHeader, CancellationToken, Task<Result<byte[]>>> dataKeyFor, CancellationToken cancellationToken)
    {
        if (downloads is null) return Error.Failure(CloudBackupErrorCodes.Failed, "Restoring from the cloud is not available here.");
        var secret = await access.ReadAsync(cancellationToken);
        if (string.IsNullOrEmpty(secret))
            return Error.Validation(CloudBackupErrorCodes.NotSetUp, "Enter the backup access key first; the vendor gives it with the license.");

        System.IO.Directory.CreateDirectory(downloads.Directory);
        var encrypted = Path.Combine(downloads.Directory, backupId.ToString("N") + BackupEnvelope.Extension);
        // named after the backup (its label), so the restore question and the outcome name the backup the person chose
        var plain = Path.Combine(downloads.Directory, PlainName(label, backupId));
        TryDelete(plain);
        var keep = false;
        byte[]? dataKey = null;
        try
        {
            var downloaded = await client.DownloadAsync(secret, backupId, cancellationToken);
            if (downloaded.IsFailure) return Result.Failure<DownloadedBackup>(downloaded.Error);
            await using (var source = downloaded.Value)
            await using (var target = new FileStream(encrypted, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(target, cancellationToken);

            EnvelopeHeader header;
            await using (var input = File.OpenRead(encrypted))
                header = BackupEnvelope.ReadHeader(input);

            var opened = await dataKeyFor(header, cancellationToken);
            if (opened.IsFailure) return Result.Failure<DownloadedBackup>(opened.Error);
            dataKey = opened.Value;

            await using (var input = File.OpenRead(encrypted))
            await using (var output = new FileStream(plain, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await BackupEnvelope.DecryptAsync(input, output, dataKey, cancellationToken);

            File.SetLastWriteTimeUtc(plain, header.CreatedAt.UtcDateTime);
            keep = true;
            logger.LogInformation("Cloud backup {Id} downloaded and opened for a restore.", backupId);
            return new DownloadedBackup(plain, header.CreatedAt, label, header.KeyId, backupId);
        }
        catch (BackupEnvelopeException ex)
        {
            logger.LogWarning(ex, "Cloud backup {Id} could not be opened ({Problem}).", backupId, ex.Problem);
            return ex.Problem == EnvelopeProblem.WrongKey
                ? Error.Validation(CloudBackupErrorCodes.WrongRecoveryCode, "This backup was made with another recovery code.")
                : Error.Failure(CloudBackupErrorCodes.Damaged, "The cloud backup is damaged or is not a backup of this application, so it cannot be restored.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Cloud backup {Id} could not be stored for the restore.", backupId);
            return Error.Failure(CloudBackupErrorCodes.Failed, "The cloud backup could not be prepared on this computer. Your data was not changed.");
        }
        finally
        {
            if (dataKey is not null) CryptographicOperations.ZeroMemory(dataKey);
            TryDelete(encrypted);
            if (!keep) TryDelete(plain);
        }
    }

    // ------------------------------------------------------------------ recovery through the vendor (MISS-04f, design 5B)

    /// <summary>
    /// Makes the recovery request for a cloud backup that cannot be opened here (no recovery code): reads the backup's authenticated header
    /// (key id, escrow slot), makes a one-time key pair whose private half is kept protected on this PC, and returns the text the owner
    /// sends to the vendor. It holds no business data. A new request for the same backup replaces the earlier one.
    /// </summary>
    public async Task<Result<RecoveryRequestMade>> CreateRecoveryRequestAsync(Guid backupId, string? label, string? licenseId, CancellationToken cancellationToken = default)
    {
        if (requests is null || !keys.CanProtect)
            return Error.Failure(CloudBackupErrorCodes.CannotProtectKeys, "This computer cannot protect the key a recovery needs, so the vendor cannot recover a backup for it.");
        var secret = await access.ReadAsync(cancellationToken);
        if (string.IsNullOrEmpty(secret))
            return Error.Validation(CloudBackupErrorCodes.NotSetUp, "Enter the backup access key first; the vendor gives it with the license.");

        EnvelopeHeader header;
        try
        {
            var downloaded = await client.DownloadAsync(secret, backupId, cancellationToken);
            if (downloaded.IsFailure) return Result.Failure<RecoveryRequestMade>(downloaded.Error);
            await using (var source = downloaded.Value)
                header = BackupEnvelope.ReadHeader(source);   // the header only; the rest is never read
        }
        catch (BackupEnvelopeException ex)
        {
            logger.LogWarning(ex, "Cloud backup {Id} has no readable header ({Problem}).", backupId, ex.Problem);
            return Error.Failure(CloudBackupErrorCodes.Damaged, "The cloud backup is damaged or is not a backup of this application, so it cannot be recovered.");
        }

        using var oneTime = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var request = EscrowRecovery.CreateRequest(header, backupId, label, licenseId, oneTime.ExportSubjectPublicKeyInfo(), clock.GetUtcNow());
        var privateKey = oneTime.ExportPkcs8PrivateKey();
        try
        {
            await requests.SaveAsync(new StoredRecoveryRequest(request, privateKey), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        await events.TryRecordAsync(BusinessEvent.Create(AuditModule, "cloud-backup.recovery-requested", "backup", backupId.ToString(),
            $"A recovery request for the cloud backup {label ?? backupId.ToString("N")} was made for the vendor (request {request.RequestId:N}, shop key {header.KeyId})."));
        logger.LogInformation("Recovery request {Request} made for cloud backup {Id}.", request.RequestId, backupId);
        return new RecoveryRequestMade(backupId, request.RequestId, EscrowRecovery.WriteRequest(request));
    }

    /// <summary>
    /// Opens the backup named in the vendor's restore key: only with the request this PC made for it (its one-time private key), and only if
    /// the downloaded backup's header is the one the request named. The plain copy goes to the LOCAL restore, as with a recovery code.
    /// </summary>
    public async Task<Result<DownloadedBackup>> DownloadWithRestoreKeyAsync(string? restoreKeyText, CancellationToken cancellationToken = default)
    {
        if (!EscrowRecovery.TryReadRestoreKey(restoreKeyText, out var restoreKey, out var problem))
            return Error.Validation(CloudBackupErrorCodes.NotARestoreKey, problem == RecoveryTextProblem.Damaged
                ? "The restore key is damaged (a character changed or lost). Paste the whole text exactly as the vendor sent it."
                : "No restore key was found in the text. Paste the whole text the vendor sent, including the BEGIN and END lines.");

        var stored = requests is null ? null : await requests.FindAsync(restoreKey!.BackupId, cancellationToken);
        if (stored is null || stored.UsedAt is not null || stored.Request.RequestId != restoreKey!.RequestId)
            return Error.Validation(CloudBackupErrorCodes.NoRecoveryRequest,
                "This restore key belongs to a request this computer did not make, a newer request replaced it, or it was used already. Ask the vendor again from this computer.");

        return await DownloadAndOpenAsync(restoreKey.BackupId, stored.Request.BackupLabel, (header, ct) =>
        {
            if (!EscrowRecovery.Matches(stored.Request, header))
                return Task.FromResult<Result<byte[]>>(Error.Validation(CloudBackupErrorCodes.WrongRestoreKey, "The cloud backup is not the one the recovery request was made for."));
            try
            {
                using var oneTime = ECDiffieHellman.Create();
                oneTime.ImportPkcs8PrivateKey(stored.PrivateKey, out _);
                return Task.FromResult<Result<byte[]>>(EscrowRecovery.OpenRestoreKey(restoreKey, stored.Request, oneTime));
            }
            catch (BackupEnvelopeException)
            {
                return Task.FromResult<Result<byte[]>>(Error.Validation(CloudBackupErrorCodes.WrongRestoreKey, "The restore key does not open this backup on this computer. Ask the vendor again from this computer."));
            }
        }, cancellationToken);
    }

    /// <summary>
    /// The restore with the vendor's restore key was confirmed (it runs at the next start): the request can no longer be used, it is removed
    /// at the next start, and the PC asks for a new recovery code. Undone by <see cref="VendorRestoreCancelledAsync"/>.
    /// </summary>
    public async Task VendorRestoreConfirmedAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        if (requests is not null && await requests.FindAsync(backupId, cancellationToken) is { } stored)
            await requests.SaveAsync(stored with { UsedAt = now }, cancellationToken);
        await UpdateStatusAsync(s => s with { VendorRestoreAt = now });
        await events.TryRecordAsync(BusinessEvent.Create(AuditModule, "cloud-backup.restore-key-used", "backup", backupId.ToString(),
            "A restore with the vendor's restore key was confirmed; a new recovery code is needed afterwards."));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The confirmed restore with the vendor's restore key was called off before the restart: the request can be used again.</summary>
    public async Task VendorRestoreCancelledAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        if (requests is not null && await requests.FindAsync(backupId, cancellationToken) is { UsedAt: not null } stored)
            await requests.SaveAsync(stored with { UsedAt = null }, cancellationToken);
        await UpdateStatusAsync(s => s with { VendorRestoreAt = null });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>At start: requests whose restore was confirmed before the restart have done their work and are removed (one time only).</summary>
    public async Task SweepUsedRequestsAsync(CancellationToken cancellationToken = default)
    {
        if (requests is null) return;
        foreach (var used in (await requests.ListAsync(cancellationToken)).Where(r => r.UsedAt is not null))
            await requests.DeleteAsync(used.Request.BackupId, cancellationToken);
    }

    public event EventHandler? Changed;

    /// <summary>True after a restore with the vendor's restore key until a recovery code is made on this PC (design 5B step 4).</summary>
    public async Task<bool> NeedsNewRecoveryCodeAsync(CancellationToken cancellationToken = default)
    {
        if ((await status.ReadAsync(cancellationToken)).VendorRestoreAt is not { } restoredAt) return false;
        return !(await keys.ListAsync(cancellationToken)).Any(k => k.Active && k.CreatedAt >= restoredAt);
    }

    /// <summary>A safe file name from the cloud backup's label ("genericpos-20261011-200108.db"); the id when there is none.</summary>
    private static string PlainName(string? label, Guid backupId)
    {
        var name = Path.GetFileName(label ?? string.Empty);
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim();
        if (name.Length is 0 or > 100 || name.StartsWith('.')) name = backupId.ToString("N");
        return name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ? name : name + ".db";
    }

    /// <summary>Removes a downloaded plain copy (only files in the download folder).</summary>
    public void DiscardDownload(string plainPath)
    {
        if (downloads is null) return;
        var full = Path.GetFullPath(plainPath);
        if (full.StartsWith(Path.GetFullPath(downloads.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            TryDelete(full);
    }

    /// <summary>At start: whatever a crash left in the download folder (plain copies included) is removed.</summary>
    public void SweepDownloads()
    {
        if (downloads is null || !System.IO.Directory.Exists(downloads.Directory)) return;
        foreach (var file in System.IO.Directory.EnumerateFiles(downloads.Directory)) TryDelete(file);
    }

    // ------------------------------------------------------------------ status

    public async Task<CloudBackupOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => new(await WhyNotReadyAsync(cancellationToken), await keys.ListAsync(cancellationToken), !string.IsNullOrEmpty(await access.ReadAsync(cancellationToken)),
            escrow.Current is not null, (await outbox.ListAsync(cancellationToken)).OrderBy(e => e.CreatedAt).ToList(), await status.ReadAsync(cancellationToken),
            await NeedsNewRecoveryCodeAsync(cancellationToken));

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
    CloudSkipReason? NotReadyBecause, IReadOnlyList<CloudKeyInfo> Keys, bool HasAccessKey, bool HasEscrowKey, IReadOnlyList<OutboxEntry> Waiting, CloudBackupStatus Status,
    bool NeedsNewRecoveryCode = false);
