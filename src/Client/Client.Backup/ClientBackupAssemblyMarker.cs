namespace Client.Backup;

/// <summary>
/// Marks the Client.Backup assembly (used by Architecture.Tests to locate it).
///
/// Client.Backup is the LOCAL backup of the business database for every shop (MISS-04, design: MISS-04_BACKUP_DESIGN.md):
///   Domain          - BackupRecord (one backup in the history), BackupSettings, error codes
///   Application     - BackupService (make, verify, list, delete, settings; retention), the backup.* capabilities and the
///                     user-facing handlers that check them, IDatabaseSnapshotter / IBackupDestination / stores (abstractions)
///   Infrastructure  - SqliteDatabaseSnapshotter (SQLite online backup + integrity check), LocalFolderDestination (a folder,
///                     USB drive or network share), JSON history and settings next to the database, host registration
///
/// Local backups are plain SQLite files by decision (design section 12, decision 2); encryption, the recovery code and escrow
/// belong to the optional CloudBackup module. It contains NO HTTP, NO EF Core, NO WPF, NO data protection and NO reference
/// to any business module: the database is copied as a whole file, never read table by table (except the migration list).
/// </summary>
public static class ClientBackupAssemblyMarker
{
    // Intentionally empty.
}
