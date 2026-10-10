using System.Security.Cryptography;
using Client.Backup.Application;
using Client.Backup.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Platform.Core.Results;

namespace Client.Backup.Infrastructure;

/// <summary>
/// Copies the live database with the SQLite online backup API (the technique of the updater's restore points, Stage 7): a consistent
/// copy while the application keeps reading and writing. The live database is opened read-only. The copy is switched to the plain
/// rollback journal (a single self-contained file that any SQLite tool opens), checked with <c>PRAGMA integrity_check</c> and
/// fingerprinted. Pooling is off, so no connection to a copy outlives the copy (and no process-wide pool is ever cleared).
/// </summary>
public sealed class SqliteDatabaseSnapshotter(string databasePath, ILogger<SqliteDatabaseSnapshotter> logger) : IDatabaseSnapshotter
{
    private const string MigrationHistoryTable = "__EFMigrationsHistory";

    public Task<Result<DatabaseSnapshot>> SnapshotAsync(string targetPath, CancellationToken cancellationToken = default)
        => Task.Run(() => Snapshot(targetPath), cancellationToken);

    public Task<Result<DatabaseSnapshot>> InspectAsync(string path, CancellationToken cancellationToken = default)
        => Task.Run(() => Inspect(path), cancellationToken);

    private Result<DatabaseSnapshot> Snapshot(string targetPath)
    {
        if (!File.Exists(databasePath))
            return Error.Failure(BackupErrorCodes.NoDatabase, "There is no shop data to back up yet.");

        try
        {
            using (var source = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False"))
            using (var destination = new SqliteConnection($"Data Source={targetPath};Pooling=False"))
            {
                source.Open();
                destination.Open();
                source.BackupDatabase(destination);

                using var journal = destination.CreateCommand();
                journal.CommandText = "PRAGMA journal_mode=DELETE;";
                journal.ExecuteNonQuery();
            }
        }
        catch (SqliteException ex)
        {
            logger.LogError(ex, "Copying the database for a backup failed.");
            TryDelete(targetPath);
            return Error.Failure(BackupErrorCodes.Failed, "The shop data could not be copied for the backup. Try again in a moment. Your data was not changed.");
        }

        var inspected = Inspect(targetPath);
        if (inspected.IsFailure)
        {
            TryDelete(targetPath);
            return Error.Failure(BackupErrorCodes.CheckFailed,
                "The copy of the shop data failed its check, so no backup was made. Your data was not changed; contact support if this happens again.");
        }

        return inspected;
    }

    private Result<DatabaseSnapshot> Inspect(string path)
    {
        try
        {
            List<string> migrations;
            using (var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False"))
            {
                connection.Open();

                using (var check = connection.CreateCommand())
                {
                    check.CommandText = "PRAGMA integrity_check;";
                    using var reader = check.ExecuteReader();
                    var lines = new List<string>();
                    while (reader.Read()) lines.Add(reader.GetString(0));
                    if (lines is not ["ok"])
                    {
                        logger.LogWarning("Database file {Path} failed its integrity check: {Lines}", path, string.Join(" | ", lines.Take(5)));
                        return Error.Failure(BackupErrorCodes.CheckFailed, "The database file failed its integrity check.");
                    }
                }

                migrations = ReadMigrations(connection);
            }

            var size = new FileInfo(path).Length;
            using var stream = File.OpenRead(path);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            return new DatabaseSnapshot(path, size, sha256, migrations);
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Database file {Path} could not be read.", path);
            return Error.Failure(BackupErrorCodes.CheckFailed, "The file is not a readable database.");
        }
    }

    /// <summary>Every module records its migrations in the one shared EF history table; a database without migrations has none.</summary>
    private static List<string> ReadMigrations(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        exists.Parameters.AddWithValue("$name", MigrationHistoryTable);
        if (Convert.ToInt64(exists.ExecuteScalar()) == 0)
            return [];

        using var read = connection.CreateCommand();
        read.CommandText = $"SELECT MigrationId FROM \"{MigrationHistoryTable}\" ORDER BY MigrationId;";
        using var reader = read.ExecuteReader();
        var migrations = new List<string>();
        while (reader.Read()) migrations.Add(reader.GetString(0));
        return migrations;
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove {Path}; it is removed at the next start.", path);
        }
    }
}
