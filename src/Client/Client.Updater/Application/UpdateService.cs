using System.Text.Json;
using Client.Updater.Domain;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;
using Security.Es256;
using Updates.Contracts;
using Updates.Package;

namespace Client.Updater.Application;

/// <summary>
/// The client-side update use cases. None of them is ever required for normal operation: discovery and download failures
/// are ordinary results, and an installation in progress never touches the live (active) version until the final
/// atomic pointer switch.
/// </summary>
public interface IUpdateService
{
    /// <summary>Asks the update source for updates; returns only updates whose SIGNED manifest verifies and is installable. Never throws; failure = Update.ServerUnavailable.</summary>
    Task<Result<IReadOnlyList<UpdateInfo>>> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads a package (re-verifying its manifest first). Returns the local package path. The installation is untouched.</summary>
    Task<Result<string>> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken = default);

    /// <summary>Verifies, stages, migrates (if needed) and activates a package. See class remarks of PackageVerifier / UpdateStore.</summary>
    Task<Result<UpdateJournal>> InstallAsync(string packagePath, CancellationToken cancellationToken = default);

    /// <summary>Startup recovery: resolves interrupted/unconfirmed updates using only local state. No network.</summary>
    Task<IReadOnlyList<UpdateJournal>> RecoverAsync(CancellationToken cancellationToken = default);

    /// <summary>The host calls this after the updated version has started healthy: the update becomes final.</summary>
    Task<Result> ConfirmHealthyAsync(string targetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Explicit BINARY rollback to the previous version (a pointer switch; it is not a downgrade install). If the update
    /// migrated the database and the old binaries are not schema-compatible, this is refused unless
    /// <paramref name="restoreData"/> is true, which restores the pre-update database restore point (discarding changes
    /// made since the update). The database is never rolled back implicitly.
    /// </summary>
    Task<Result> RollbackAsync(string targetId, bool restoreData = false, CancellationToken cancellationToken = default);
}

public sealed class UpdateService(
    IUpdateStore store,
    PackageVerifier verifier,
    IUpdateClient client,
    IInstalledStateProvider installedState,
    IMigrationCoordinator migrations,
    IDataSafeguard safeguard,
    UpdaterOptions options,
    TimeProvider timeProvider,
    ILogger<UpdateService> logger,
    ISecurityEventSink? events = null) : IUpdateService
{
    /// <summary>Records what happened to an update: IDs, version and result only - never package content.</summary>
    private void Audit(string action, SecurityEventOutcome outcome, string? subjectId, string summary)
        => _ = events.TryRecordAsync(SecurityEvent.Create(action, outcome, subjectType: "update", subjectId: subjectId, summary: summary, occurredAt: timeProvider.GetUtcNow()));

    // ------------------------------------------------------------------ discovery

    public async Task<Result<IReadOnlyList<UpdateInfo>>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var installed = new List<InstalledTarget> { new(PackageManifest.CoreTargetId, installedState.HostVersion.ToString()) };
        installed.AddRange(installedState.GetInstalledModules().Select(m => new InstalledTarget(m.Id.Value, m.Version.ToString())));
        var request = new UpdateCheckRequest(installedState.HostVersion.ToString(), options.TargetFramework, installed);

        UpdateCheckResponse response;
        try
        {
            response = await client.CheckAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Update discovery failed; continuing without update information.");
            return Result.Failure<IReadOnlyList<UpdateInfo>>(Error.Failure(
                UpdateErrorCodes.ServerUnavailable, "The update source could not be reached: " + ex.Message));
        }

        if (!response.IsSuccess)
            return Result.Failure<IReadOnlyList<UpdateInfo>>(Error.Failure(
                response.ErrorCode ?? UpdateErrorCodes.ServerUnavailable,
                response.ErrorMessage ?? "The update source returned an error."));

        var eligible = new List<UpdateInfo>();
        foreach (var info in response.Updates)
        {
            var verified = verifier.VerifyManifest(info.SignedManifest);
            if (verified.IsFailure)
                continue; // already logged by the verifier: unsigned/untrusted/incompatible/unlicensed updates are not offered

            var m = verified.Value.Manifest;
            if (m.PackageId != info.PackageId || m.TargetId != info.TargetId || m.Version != info.Version)
            {
                logger.LogWarning("Discovered update {PackageId} rejected: its metadata does not match its signed manifest.", info.PackageId);
                continue;
            }

            logger.LogInformation("Update discovered: {Target} {Version} ({Type})", m.TargetId, m.Version, m.PackageType);
            eligible.Add(info);
        }

        return Result.Success<IReadOnlyList<UpdateInfo>>(eligible);
    }

    // ------------------------------------------------------------------ download

    public async Task<Result<string>> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken = default)
    {
        var manifest = verifier.VerifyManifest(update.SignedManifest);
        if (manifest.IsFailure)
            return Result.Failure<string>(manifest.Error);

        store.EnsureDirectories();
        var finalPath = store.DownloadPath(update.PackageId);
        var partPath = finalPath + ".part";

        try
        {
            Result transfer;
            await using (var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
                transfer = await client.DownloadAsync(update.PackageId, file, cancellationToken);

            if (transfer.IsFailure)
            {
                DeleteQuietly(partPath);
                logger.LogWarning("Package download failed [{Code}]: {Message}", transfer.Error.Code, transfer.Error.Description);
                return Result.Failure<string>(Error.Failure(UpdateErrorCodes.DownloadFailed, transfer.Error.Description));
            }

            string actual;
            await using (var read = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                actual = Sha256Hex.Compute(read);

            if (!string.IsNullOrEmpty(update.DownloadSha256)
                && !string.Equals(actual, update.DownloadSha256, StringComparison.OrdinalIgnoreCase))
            {
                DeleteQuietly(partPath);
                logger.LogWarning("Downloaded package {PackageId} rejected: file hash mismatch.", update.PackageId);
                Audit("security.update.rejected", SecurityEventOutcome.Denied, update.PackageId.ToString(), "downloaded file does not match its advertised hash");
                return Result.Failure<string>(Error.Validation(UpdateErrorCodes.HashMismatch, "The downloaded package does not match its advertised hash."));
            }

            File.Move(partPath, finalPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteQuietly(partPath);
            logger.LogWarning(ex, "Package download failed.");
            return Result.Failure<string>(Error.Failure(UpdateErrorCodes.DownloadFailed, "The package could not be downloaded: " + ex.Message));
        }

        var journal = store.LoadJournal(update.PackageId) ?? NewJournal(manifest.Value.Manifest);
        journal.History.Add(new UpdateStateEntry(UpdateState.Discovered, timeProvider.GetUtcNow(), null));
        Transition(journal, UpdateState.Downloaded, "Package downloaded.");
        logger.LogInformation("Package downloaded: {PackageId}", update.PackageId);
        return Result.Success(finalPath);
    }

    // ------------------------------------------------------------------ install

    public async Task<Result<UpdateJournal>> InstallAsync(string packagePath, CancellationToken cancellationToken = default)
    {
        // Steps 1-14: verification. Mutates nothing.
        var verified = verifier.VerifyPackage(packagePath);
        if (verified.IsFailure)
        {
            RecordVerificationFailure(packagePath, verified.Error);
            return Result.Failure<UpdateJournal>(verified.Error);
        }

        var package = verified.Value;
        var m = package.Manifest;

        var unresolved = store.ListJournals().FirstOrDefault(j =>
            j.TargetId == m.TargetId && j.State is UpdateState.Activated or UpdateState.RecoveryRequired);
        if (unresolved is not null)
            return Result.Failure<UpdateJournal>(Error.Conflict(UpdateErrorCodes.RecoveryRequired,
                $"A previous update of '{m.TargetId}' is {unresolved.State}. Confirm, roll back or resolve it first."));

        store.EnsureDirectories();
        var journal = store.LoadJournal(m.PackageId) ?? NewJournal(m);
        journal.PreviousVersion = store.ReadActive(m.TargetId)?.Version;
        journal.MigrationRequired = m.Migration is not null;
        journal.OldBinaryCompatible = m.Migration?.OldBinaryCompatibleWithNewSchema ?? true;
        Transition(journal, UpdateState.Verified, "Package verified.");

        // Stage: extract to a private area; the live installation is untouched.
        var staging = store.StagingPath(m.PackageId);
        var stage = Stage(package, staging);
        if (stage.IsFailure)
        {
            DirectoryCleanup.DeleteQuietly(staging);
            Transition(journal, UpdateState.Failed, stage.Error.Description);
            return Result.Failure<UpdateJournal>(stage.Error);
        }

        Transition(journal, UpdateState.Staged, "Payload staged.");

        // Migrate (module-owned), protected by a restore point.
        if (m.Migration is { } migration)
        {
            Transition(journal, UpdateState.MigrationPending, $"Schema {migration.FromSchemaVersion} -> {migration.ToSchemaVersion}.");

            var restore = await safeguard.CreateRestorePointAsync(m.PackageId, cancellationToken);
            if (restore.IsFailure)
            {
                DirectoryCleanup.DeleteQuietly(staging);
                Transition(journal, UpdateState.Failed, "Restore point could not be created; update aborted before any change.");
                return Result.Failure<UpdateJournal>(Error.Failure(UpdateErrorCodes.InstallationFailed,
                    "The database restore point could not be created, so the update was aborted: " + restore.Error.Description));
            }

            journal.RestorePointPath = restore.Value;
            Transition(journal, UpdateState.Migrating, "Migration started.");
            logger.LogInformation("Migration started for {Target} (schema {From} -> {To})", m.TargetId, migration.FromSchemaVersion, migration.ToSchemaVersion);

            MigrationOutcome outcome;
            try
            {
                outcome = await migrations.MigrateAsync(
                    new MigrationRequest(new Platform.Core.Modules.ModuleId(m.TargetId), migration.FromSchemaVersion, migration.ToSchemaVersion, staging),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = new MigrationOutcome(false, true, false, ex.Message);
            }

            if (!outcome.Succeeded)
            {
                DirectoryCleanup.DeleteQuietly(staging);
                var message = "Migration failed: " + (outcome.Message ?? "unknown error");
                if (outcome.DatabaseModified)
                {
                    Transition(journal, UpdateState.RecoveryRequired, message + " The database may have been modified; the restore point was kept.");
                    logger.LogError("Migration failed after modifying data for {Target}; recovery required.", m.TargetId);
                }
                else
                {
                    Transition(journal, UpdateState.MigrationFailed, message);
                }

                return Result.Failure<UpdateJournal>(Error.Failure(UpdateErrorCodes.MigrationFailed, message));
            }

            logger.LogInformation("Migration completed for {Target} (deferred: {Deferred})", m.TargetId, outcome.Deferred);
        }

        // Deploy side by side, then activate with one atomic pointer write.
        Transition(journal, UpdateState.ReadyToActivate, "Ready to activate.");
        var versionDir = store.VersionDir(m.TargetId, m.Version);
        var deployed = false;
        try
        {
            if (Directory.Exists(versionDir))
                throw new IOException($"The version directory '{versionDir}' already exists; refusing to overwrite it.");

            Directory.CreateDirectory(store.TargetDir(m.TargetId));
            Directory.Move(staging, versionDir);
            deployed = true;

            logger.LogInformation("Activation started: {Target} {Version}", m.TargetId, m.Version);
            store.WriteActive(m.TargetId, new ActivePointer { Version = m.Version, PreviousVersion = journal.PreviousVersion });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (deployed) DirectoryCleanup.DeleteQuietly(versionDir);
            DirectoryCleanup.DeleteQuietly(staging);

            var message = "Activation failed; the previous version remains active: " + ex.Message;
            if (journal.BinaryRollbackSafe)
            {
                Transition(journal, UpdateState.ActivationFailed, message);
            }
            else
            {
                Transition(journal, UpdateState.RecoveryRequired, message + " The database was migrated and the old binaries may not be schema-compatible.");
            }

            logger.LogError(ex, "Activation failed for {Target} {Version}", m.TargetId, m.Version);
            return Result.Failure<UpdateJournal>(Error.Failure(UpdateErrorCodes.InstallationFailed, message));
        }

        Transition(journal, UpdateState.Activated, "Activated; awaiting confirmation after a healthy start.");
        logger.LogInformation("Activation completed: {Target} {Version}", m.TargetId, m.Version);
        Audit("security.update.accepted", SecurityEventOutcome.Success, m.PackageId.ToString(),
            $"{m.TargetId} {m.Version} verified (signature by key '{m.KeyId}') and activated");
        return Result.Success(journal);
    }

    private Result Stage(VerifiedPackage package, string staging)
    {
        try
        {
            DirectoryCleanup.DeleteQuietly(staging);
            Directory.CreateDirectory(staging);

            using var opened = OpenAgain(package.PackagePath!);
            var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

            foreach (var file in package.Manifest.Files)
            {
                var target = Path.GetFullPath(Path.Combine(staging, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(stagingRoot, StringComparison.Ordinal))
                    return Error.Validation(UpdateErrorCodes.InvalidPackage, $"Path '{file.Path}' escapes the staging area.");

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);

                using (var source = opened.OpenPayload(file.Path))
                using (var dest = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(dest);

                // Re-verify what was actually written to disk.
                using var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (check.Length != file.Length || !string.Equals(Sha256Hex.Compute(check), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return Error.Validation(UpdateErrorCodes.HashMismatch, $"Staged file '{file.Path}' does not match its signed hash.");
            }

            var infoDir = Path.Combine(staging, ".package");
            Directory.CreateDirectory(infoDir);
            File.WriteAllText(Path.Combine(infoDir, "manifest.json"), PackageManifestSerializer.SerializeEnvelope(package.Envelope));
            return Result.Success();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Error.Failure(UpdateErrorCodes.InstallationFailed, "Staging failed: " + ex.Message);
        }
    }

    private static PackageContents OpenAgain(string path)
    {
        var opened = PackageReader.Open(path);
        return opened.Contents ?? throw new IOException(opened.ErrorMessage ?? "The package cannot be re-opened.");
    }

    // ------------------------------------------------------------------ recovery

    public Task<IReadOnlyList<UpdateJournal>> RecoverAsync(CancellationToken cancellationToken = default)
    {
        var changed = new List<UpdateJournal>();
        if (!Directory.Exists(store.Root))
            return Task.FromResult<IReadOnlyList<UpdateJournal>>(changed);

        foreach (var journal in store.ListJournals())
        {
            switch (journal.State)
            {
                case UpdateState.Activated:
                    journal.StartupAttempts++;
                    if (journal.StartupAttempts <= options.MaxStartupAttempts)
                    {
                        store.SaveJournal(journal);
                        break;
                    }

                    logger.LogWarning("Recovery detected: {Target} {Version} was activated but never confirmed healthy.", journal.TargetId, journal.Version);
                    if (journal.BinaryRollbackSafe)
                    {
                        var rolledBack = RollbackCore(journal, "Automatic rollback: the new version never confirmed a healthy start.");
                        if (rolledBack.IsFailure)
                            Transition(journal, UpdateState.RecoveryRequired, "Automatic rollback failed: " + rolledBack.Error.Description);
                    }
                    else
                    {
                        Transition(journal, UpdateState.RecoveryRequired,
                            "The new version never confirmed a healthy start, but the database was migrated and the old binaries may not be compatible. Explicit decision required.");
                    }

                    changed.Add(journal);
                    break;

                case UpdateState.ReadyToActivate when !journal.BinaryRollbackSafe:
                    // The migration completed but activation never happened: the database may already be on the new schema
                    // while the (still active) old binaries are not schema-compatible. Needs an explicit decision.
                    logger.LogError("Recovery detected: update {PackageId} migrated the database but was never activated.", journal.PackageId);
                    Transition(journal, UpdateState.RecoveryRequired,
                        "Interrupted after migration and before activation; the database may be on the new schema and the old binaries are not compatible. The restore point was kept.");
                    changed.Add(journal);
                    break;

                case UpdateState.Staged:
                case UpdateState.MigrationPending:
                case UpdateState.ReadyToActivate:
                    logger.LogWarning("Recovery detected: update {PackageId} was interrupted in state {State}; known-good version kept.", journal.PackageId, journal.State);
                    Transition(journal, UpdateState.Failed, $"Interrupted in state {journal.State} before activation; the known-good installation was kept.");
                    changed.Add(journal);
                    break;

                case UpdateState.Migrating:
                    logger.LogError("Recovery detected: update {PackageId} was interrupted during migration.", journal.PackageId);
                    Transition(journal, UpdateState.RecoveryRequired, "Interrupted during migration; the database may have been modified. The restore point was kept.");
                    changed.Add(journal);
                    break;
            }
        }

        // Nothing in staging can ever be activated after a restart.
        if (Directory.Exists(store.StagingDir))
            foreach (var dir in Directory.GetDirectories(store.StagingDir))
                DirectoryCleanup.DeleteQuietly(dir);

        return Task.FromResult<IReadOnlyList<UpdateJournal>>(changed);
    }

    public Task<Result> ConfirmHealthyAsync(string targetId, CancellationToken cancellationToken = default)
    {
        var journal = LatestJournal(targetId, UpdateState.Activated);
        if (journal is null)
            return Task.FromResult(Result.Failure(Error.NotFound(UpdateErrorCodes.NotFound, $"No unconfirmed update for '{targetId}'.")));

        Transition(journal, UpdateState.Confirmed, "Confirmed healthy.");
        PruneOldVersions(targetId);
        if (journal.BinaryRollbackSafe && journal.RestorePointPath is not null)
            DirectoryCleanup.DeleteQuietly(Path.GetDirectoryName(journal.RestorePointPath)!);

        DeleteQuietly(store.DownloadPath(journal.PackageId));
        logger.LogInformation("Update confirmed: {Target} {Version}", targetId, journal.Version);
        return Task.FromResult(Result.Success());
    }

    public async Task<Result> RollbackAsync(string targetId, bool restoreData = false, CancellationToken cancellationToken = default)
    {
        // Prefer the update that is actually active; otherwise the most recent one.
        var activeVersion = store.ReadActive(targetId)?.Version;
        var journal = store.ListJournals()
            .Where(j => j.TargetId == targetId && j.State is UpdateState.Activated or UpdateState.Confirmed or UpdateState.RecoveryRequired)
            .OrderByDescending(j => j.Version == activeVersion)
            .ThenByDescending(j => j.History.LastOrDefault()?.At)
            .ThenByDescending(j => j.History.Count)
            .FirstOrDefault();

        if (journal is null)
            return Error.NotFound(UpdateErrorCodes.NotFound, $"There is no activated update of '{targetId}' to roll back.");

        if (!journal.BinaryRollbackSafe)
        {
            if (!restoreData)
                return Error.Failure(UpdateErrorCodes.RollbackFailed,
                    "Rolling back the binaries alone is unsafe: the update migrated the database and the previous binaries are not schema-compatible. " +
                    "Restore the pre-update data explicitly (restoreData: true) or keep the new version.");

            if (journal.RestorePointPath is null)
                return Error.Failure(UpdateErrorCodes.RollbackFailed, "No database restore point exists for this update.");

            var restored = await safeguard.RestoreAsync(journal.RestorePointPath, cancellationToken);
            if (restored.IsFailure)
                return Error.Failure(UpdateErrorCodes.RollbackFailed, "The database could not be restored: " + restored.Error.Description);
        }

        return RollbackCore(journal, restoreData ? "Rolled back with explicit data restore." : "Rolled back.");
    }

    private Result RollbackCore(UpdateJournal journal, string message)
    {
        logger.LogWarning("Rollback started: {Target} {Version}", journal.TargetId, journal.Version);
        try
        {
            var active = store.ReadActive(journal.TargetId);
            if (active is not null && active.Version != journal.Version)
                return Error.Conflict(UpdateErrorCodes.RollbackFailed, $"Version {active.Version} is active, not {journal.Version}.");

            if (journal.PreviousVersion is not null && Directory.Exists(store.VersionDir(journal.TargetId, journal.PreviousVersion)))
                store.WriteActive(journal.TargetId, new ActivePointer { Version = journal.PreviousVersion });
            else
                store.ClearActive(journal.TargetId);   // back to the built-in baseline
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Rollback failed for {Target}", journal.TargetId);
            return Error.Failure(UpdateErrorCodes.RollbackFailed, "The active pointer could not be restored: " + ex.Message);
        }

        // The rolled-back version is removed so the same package can be retried later; the restored version is untouched.
        if (journal.State is UpdateState.Activated or UpdateState.Confirmed)
            DirectoryCleanup.DeleteQuietly(store.VersionDir(journal.TargetId, journal.Version));

        Transition(journal, UpdateState.RolledBack, message);
        logger.LogWarning("Rollback completed: {Target} is back to {Previous}", journal.TargetId, journal.PreviousVersion ?? "the built-in version");
        return Result.Success();
    }

    // ------------------------------------------------------------------ helpers

    private UpdateJournal NewJournal(PackageManifest m) => new()
    {
        PackageId = m.PackageId,
        PackageType = m.PackageType,
        TargetId = m.TargetId,
        Version = m.Version,
        State = UpdateState.Discovered,
        History = [new UpdateStateEntry(UpdateState.Discovered, timeProvider.GetUtcNow(), null)]
    };

    private void Transition(UpdateJournal journal, UpdateState state, string? message)
    {
        journal.State = state;
        journal.History.Add(new UpdateStateEntry(state, timeProvider.GetUtcNow(), message));

        // The states that are security-relevant to whoever reads the audit trail: a rollback happened, or an update ended badly.
        if (state is UpdateState.RolledBack)
            Audit("security.update.rolledback", SecurityEventOutcome.Success, journal.PackageId.ToString(), $"{journal.TargetId} {journal.Version}: {message}");
        else if (state is UpdateState.Failed or UpdateState.MigrationFailed or UpdateState.ActivationFailed or UpdateState.RecoveryRequired)
            Audit("security.update.failed", SecurityEventOutcome.Failure, journal.PackageId.ToString(), $"{journal.TargetId} {journal.Version}: {state}");

        try
        {
            store.SaveJournal(journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Could not persist the update journal for {PackageId}", journal.PackageId);
        }
    }

    private UpdateJournal? LatestJournal(string targetId, UpdateState state)
        => store.ListJournals()
            .Where(j => j.TargetId == targetId && j.State == state)
            .OrderByDescending(j => j.History.LastOrDefault()?.At)
            .FirstOrDefault();

    private void RecordVerificationFailure(string packagePath, Error error)
    {
        // The package ID is only known if the file follows the download naming scheme.
        if (!Guid.TryParse(Path.GetFileNameWithoutExtension(packagePath), out var id)) return;

        var journal = store.LoadJournal(id) ?? new UpdateJournal { PackageId = id, State = UpdateState.Discovered };
        Transition(journal, UpdateState.VerificationFailed, $"[{error.Code}] {error.Description}");
    }

    /// <summary>Removes old deployed versions but never the active version or its recorded previous version (the known-good fallback).</summary>
    private void PruneOldVersions(string targetId)
    {
        var active = store.ReadActive(targetId);
        if (active is null) return;

        var keep = new HashSet<string>(StringComparer.Ordinal) { active.Version };
        if (active.PreviousVersion is not null) keep.Add(active.PreviousVersion);

        var dir = store.TargetDir(targetId);
        if (!Directory.Exists(dir)) return;

        foreach (var versionDir in Directory.GetDirectories(dir))
            if (!keep.Contains(Path.GetFileName(versionDir)!))
                DirectoryCleanup.DeleteQuietly(versionDir);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
