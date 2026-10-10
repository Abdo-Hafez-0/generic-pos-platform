namespace Platform.Application.Abstractions.Backup;

/// <summary>A fresh, checked copy of the business database that local backup has just made (MISS-04e2).</summary>
/// <param name="BackupId">The local backup it belongs to.</param>
/// <param name="FileName">The local backup's file name.</param>
/// <param name="CreatedAt">When the copy was taken.</param>
/// <param name="SizeBytes">Size of the copy.</param>
/// <param name="Sha256">SHA-256 of the copy, lowercase hex.</param>
/// <param name="ApplicationVersion">The application version that made it.</param>
/// <param name="Migrations">The database migrations applied in the copy.</param>
/// <param name="Scheduled">True when the scheduler made it, false when a person did.</param>
public sealed record BackupCopy(
    Guid BackupId, string FileName, DateTimeOffset CreatedAt, long SizeBytes, string Sha256, string ApplicationVersion, IReadOnlyList<string> Migrations, bool Scheduled);

/// <summary>
/// Somewhere else a backup is kept as well - the optional CloudBackup module's encrypted copy. Local backup hands every new backup to each
/// replica without knowing who they are (the CloudBackup module is never referenced by the local backup). Best effort by contract: a
/// replica never throws and never fails or delays the local backup beyond its own work; it must take what it needs from
/// <paramref name="snapshotPath"/> before returning, because the file is deleted afterwards.
/// </summary>
public interface IBackupReplica
{
    Task ReplicateAsync(string snapshotPath, BackupCopy copy, CancellationToken cancellationToken = default);
}
