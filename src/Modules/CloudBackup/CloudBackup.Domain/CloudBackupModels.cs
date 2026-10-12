namespace CloudBackup.Domain;

/// <summary>
/// An encrypted copy of a local backup waiting to be uploaded (MISS-04e2). It stays in the outbox until the server has it, so a shop that is
/// offline for days uploads everything once it is back. <see cref="NextAttemptAt"/> spaces out the retries.
/// </summary>
/// <param name="BackupId">The local backup it is a copy of.</param>
/// <param name="FileName">The encrypted file in the outbox (&lt;backup id&gt;.gpbak).</param>
/// <param name="SourceFileName">The local backup's file name (sent as the label, shown in the cloud list).</param>
/// <param name="CreatedAt">When the backup was taken.</param>
/// <param name="SizeBytes">Size of the encrypted file.</param>
/// <param name="Sha256">SHA-256 of the encrypted file (the server refuses an upload that does not match).</param>
/// <param name="KeyId">Which shop key (recovery code) it was made with.</param>
/// <param name="Attempts">Upload attempts so far.</param>
/// <param name="NextAttemptAt">Not before this time.</param>
/// <param name="LastError">The plain reason of the last failed attempt.</param>
public sealed record OutboxEntry(
    Guid BackupId, string FileName, string SourceFileName, DateTimeOffset CreatedAt, long SizeBytes, string Sha256, string KeyId,
    int Attempts, DateTimeOffset NextAttemptAt, string? LastError = null)
{
    /// <summary>Waits between attempts: 1, 5, 15 minutes, then every hour.</summary>
    public static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        _ => TimeSpan.FromHours(1),
    };

    public OutboxEntry Failed(DateTimeOffset now, string reason) => this with
    {
        Attempts = Attempts + 1,
        NextAttemptAt = now + RetryDelay(Attempts + 1),
        LastError = reason,
    };
}

/// <summary>Why a backup was NOT copied to the cloud (nothing is wrong with the local backup).</summary>
public enum CloudSkipReason
{
    NotLicensed = 1,
    NoRecoveryCode = 2,
    NoToken = 3,
    NoEscrowKey = 4,
}

/// <summary>
/// What the cloud side did last (kept next to the database, for the screen and the shell notice in MISS-04e3). <c>VendorRestoreAt</c>
/// (MISS-04f): a restore with the vendor's restore key was confirmed then; until a recovery code is made after it, the PC asks for one.
/// </summary>
public sealed record CloudBackupStatus(
    DateTimeOffset? LastUploadAt = null,
    string? LastUploadedFile = null,
    DateTimeOffset? LastFailureAt = null,
    string? LastFailure = null,
    CloudSkipReason? LastSkip = null,
    DateTimeOffset? LastSkipAt = null,
    DateTimeOffset? VendorRestoreAt = null);

/// <summary>One shop key in the key ring: its public id, when it was made, and whether new backups use it.</summary>
public sealed record CloudKeyInfo(string KeyId, DateTimeOffset CreatedAt, bool Active);

/// <summary>A recovery code being set up: shown once, then two of its groups are typed back before it is used.</summary>
public sealed record RecoveryCodeSetup(Guid SetupId, string Code, IReadOnlyList<int> GroupsToConfirm);

public static class CloudBackupErrorCodes
{
    public const string NotSetUp = "CloudBackup.NotSetUp";
    public const string CannotProtectKeys = "CloudBackup.CannotProtectKeys";
    public const string SetupExpired = "CloudBackup.SetupExpired";
    public const string ConfirmationMismatch = "CloudBackup.ConfirmationMismatch";
    public const string InvalidToken = "CloudBackup.InvalidToken";
    public const string Failed = "CloudBackup.Failed";
    public const string NeedsRecoveryCode = "CloudBackup.NeedsRecoveryCode";
    public const string WrongRecoveryCode = "CloudBackup.WrongRecoveryCode";
    public const string Damaged = "CloudBackup.Damaged";
    public const string NotARestoreKey = "CloudBackup.NotARestoreKey";
    public const string NoRecoveryRequest = "CloudBackup.NoRecoveryRequest";
    public const string WrongRestoreKey = "CloudBackup.WrongRestoreKey";
}

/// <summary>A recovery request made on this PC (MISS-04f): the text to send to the vendor. The one-time private key stays here, protected.</summary>
public sealed record RecoveryRequestMade(Guid BackupId, Guid RequestId, string Text);

/// <summary>A cloud backup downloaded and decrypted for a restore: a plain database file in the module's temporary folder (always discarded afterwards).</summary>
public sealed record DownloadedBackup(string PlainPath, DateTimeOffset CreatedAt, string? Label, string KeyId, Guid BackupId = default);
