namespace Client.Backup.Domain;

/// <summary>How a backup came to be made.</summary>
public enum BackupOrigin
{
    Manual = 0,
    Scheduled = 1
}

/// <summary>
/// One backup in the history. The history lives next to the database (not in it), so restoring a backup can never erase the
/// record of backups. <see cref="Sha256"/> is the fingerprint of the file as written: Verify and Restore compare against it.
/// </summary>
/// <param name="Id">Identity of the backup.</param>
/// <param name="FileName">The file name, e.g. genericpos-20261010-230000.db.</param>
/// <param name="Destination">Where it went: "local" (a folder, USB drive or network share).</param>
/// <param name="Location">The full path of the file at its destination.</param>
/// <param name="CreatedAt">When the copy was taken.</param>
/// <param name="SizeBytes">Size of the file.</param>
/// <param name="Sha256">SHA-256 of the file, lowercase hex.</param>
/// <param name="ApplicationVersion">Version of the application that made it.</param>
/// <param name="Migrations">The database migrations applied in the copy (in order), so a restore can refuse data from a newer version.</param>
/// <param name="Origin">Manual or scheduled.</param>
/// <param name="LastVerifiedAt">When it was last checked, if ever.</param>
/// <param name="LastVerifyPassed">The result of that check.</param>
public sealed record BackupRecord(
    Guid Id,
    string FileName,
    string Destination,
    string Location,
    DateTimeOffset CreatedAt,
    long SizeBytes,
    string Sha256,
    string ApplicationVersion,
    IReadOnlyList<string> Migrations,
    BackupOrigin Origin,
    DateTimeOffset? LastVerifiedAt = null,
    bool? LastVerifyPassed = null);

/// <summary>
/// The backup settings a person with <c>backup.configure</c> changes. <see cref="LocalFolder"/> null = no local destination yet
/// (nothing is backed up until one is chosen). <see cref="KeepLocal"/> = how many local backups are kept (oldest removed first).
/// </summary>
public sealed record BackupSettings(string? LocalFolder, int KeepLocal)
{
    public const int DefaultKeepLocal = 14;
    public const int MinKeepLocal = 1;
    public const int MaxKeepLocal = 365;
}

/// <summary>A copy of the database taken by <c>IDatabaseSnapshotter</c>, already checked, in the backup staging folder.</summary>
public sealed record DatabaseSnapshot(string Path, long SizeBytes, string Sha256, IReadOnlyList<string> Migrations);

public static class BackupDestinations
{
    public const string Local = "local";

    /// <summary>The shop data as it was just before a restore (MISS-04b): kept next to the database, never removed automatically.</summary>
    public const string BeforeRestore = "before-restore";
}

/// <summary>
/// A backup checked and copied into place, waiting for the person's confirmation (MISS-04b). Nothing has changed yet.
/// </summary>
/// <param name="Id">Identity of this preparation (passed back to confirm or cancel).</param>
/// <param name="FileName">The backup's file name.</param>
/// <param name="BackupCreatedAt">When the backup was made (for a loose file: when the file was last written).</param>
/// <param name="ApplicationVersion">The version that made it ("unknown" for a loose file).</param>
/// <param name="FromHistory">Whether it came from the backup history (and its fingerprint was compared).</param>
public sealed record RestorePreparation(Guid Id, string FileName, DateTimeOffset BackupCreatedAt, string ApplicationVersion, bool FromHistory);

/// <summary>
/// A confirmed restore, waiting for the next start (kept as <c>restore-pending.json</c> next to the database). The startup step puts the
/// staged file in place of the database before anything opens it, and only if the staged file still has <see cref="Sha256"/>.
/// </summary>
public sealed record PendingRestore(
    Guid Id,
    string StagedFileName,
    string Sha256,
    string SourceFileName,
    DateTimeOffset BackupCreatedAt,
    DateTimeOffset RequestedAt,
    Guid? RequestedById,
    string? RequestedByName);

/// <summary>What happened to the last restore (kept as <c>restore-outcome.json</c>; reported to the audit log once, after the start).</summary>
public sealed record RestoreOutcome(
    Guid Id,
    bool Succeeded,
    string Message,
    string SourceFileName,
    DateTimeOffset BackupCreatedAt,
    string? BeforeRestoreFileName,
    DateTimeOffset CompletedAt,
    Guid? RequestedById,
    string? RequestedByName,
    bool Reported = false);

public static class BackupErrorCodes
{
    public const string NotConfigured = "Backup.NotConfigured";
    public const string DestinationUnavailable = "Backup.DestinationUnavailable";
    public const string CheckFailed = "Backup.CheckFailed";
    public const string NoDatabase = "Backup.NoDatabase";
    public const string Busy = "Backup.Busy";
    public const string NotFound = "Backup.NotFound";
    public const string Damaged = "Backup.Damaged";
    public const string InvalidSettings = "Backup.InvalidSettings";
    public const string Failed = "Backup.Failed";
    public const string NewerVersion = "Backup.NewerVersion";
    public const string NotABackup = "Backup.NotABackup";
    public const string RestorePending = "Backup.RestorePending";
    public const string NothingPrepared = "Backup.NothingPrepared";
}
