using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Backup.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Auditing;
using Tests.Common.Security;

namespace Backup.Tests;

/// <summary>Keeps every business event so tests can assert what was audited.</summary>
public sealed class RecordingBusinessEventSink : IBusinessEventSink
{
    public List<BusinessEvent> Events { get; } = [];

    public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
    {
        lock (Events) Events.Add(businessEvent);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> Actions() { lock (Events) return Events.Select(e => e.Action).ToList(); }
}

/// <summary>
/// A shop database on disk (WAL mode, like the desktop's, with the shared EF migration history) and a backup service around it,
/// composed from the real infrastructure: the SQLite snapshotter, the local folder destination and the JSON stores. Everything lives in
/// one temporary folder that is removed afterwards.
/// </summary>
public sealed class BackupWorld : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 10, 23, 0, 0, TimeSpan.Zero);

    public static readonly string[] Migrations = ["20261005120224_InitialPurchasingSchema", "20261008161456_AddPartialReceiving"];

    public BackupWorld(bool createDatabase = true, IDatabaseSnapshotter? snapshotter = null, BackupSettings? defaults = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "genericpos-bak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        DatabasePath = Path.Combine(Root, "GenericPOS", "genericpos.db");
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        BackupFolder = Path.Combine(Root, "usb", "Backups");
        Workspace = new BackupWorkspace(Path.Combine(Root, "GenericPOS", "Backup"));

        if (createDatabase)
            CreateShopDatabase(DatabasePath, rows: 3);

        Defaults = defaults ?? new BackupSettings(BackupFolder, BackupSettings.DefaultKeepLocal);
        Snapshotter = snapshotter ?? new SqliteDatabaseSnapshotter(DatabasePath, NullLogger<SqliteDatabaseSnapshotter>.Instance);
        Service = NewService();
    }

    public string Root { get; }
    public string DatabasePath { get; }
    public string BackupFolder { get; }
    public BackupWorkspace Workspace { get; }
    public BackupSettings Defaults { get; }
    public IDatabaseSnapshotter Snapshotter { get; }
    public TestClock Clock { get; } = new(Start);
    public RecordingBusinessEventSink Events { get; } = new();
    public BackupService Service { get; private set; }

    /// <summary>A fresh service over the same files (an application restart).</summary>
    public BackupService NewService()
    {
        Service = new BackupService(
            Snapshotter,
            new LocalDestinationFactory(NullLogger<LocalFolderDestination>.Instance),
            new JsonBackupHistoryStore(Workspace, NullLogger<JsonBackupHistoryStore>.Instance),
            new JsonBackupSettingsStore(Workspace, Defaults, NullLogger<JsonBackupSettingsStore>.Instance),
            Workspace,
            new BackupRuntime("1.2.3"),
            Clock,
            NullLogger<BackupService>.Instance,
            Events);
        return Service;
    }

    public static void CreateShopDatabase(string path, int rows)
    {
        using var connection = Open(path);
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE \"__EFMigrationsHistory\" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);");
        foreach (var migration in Migrations)
            Execute(connection, $"INSERT INTO \"__EFMigrationsHistory\" VALUES ('{migration}', '10.0.11');");
        Execute(connection, "CREATE TABLE sal_Sales (Id INTEGER PRIMARY KEY, Total TEXT NOT NULL, Note TEXT);");
        for (var i = 1; i <= rows; i++)
            Execute(connection, $"INSERT INTO sal_Sales (Total, Note) VALUES ('{i}.50', 'sale {i}');");
    }

    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False{(readOnly ? ";Mode=ReadOnly" : "")}");
        connection.Open();
        return connection;
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static object? Scalar(string path, string sql)
    {
        using var connection = Open(path, readOnly: true);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void AddSale(string note)
    {
        using var connection = Open(DatabasePath);
        Execute(connection, $"INSERT INTO sal_Sales (Total, Note) VALUES ('9.99', '{note}');");
    }

    public string[] StagingFiles() => Directory.Exists(Workspace.StagingDirectory) ? Directory.GetFiles(Workspace.StagingDirectory) : [];

    public string[] BackupFiles(string? folder = null)
    {
        folder ??= BackupFolder;
        return Directory.Exists(folder) ? Directory.GetFiles(folder).Select(Path.GetFileName).OfType<string>().Order().ToArray() : [];
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temporary folder; Windows sometimes holds a file a moment longer.
        }
    }
}

/// <summary>A snapshotter that waits until the test lets it finish (to hold a backup "in progress").</summary>
public sealed class GatedSnapshotter(IDatabaseSnapshotter inner) : IDatabaseSnapshotter
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<Platform.Core.Results.Result<DatabaseSnapshot>> SnapshotAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Release.Task;
        return await inner.SnapshotAsync(targetPath, cancellationToken);
    }

    public Task<Platform.Core.Results.Result<DatabaseSnapshot>> InspectAsync(string path, CancellationToken cancellationToken = default)
        => inner.InspectAsync(path, cancellationToken);
}
