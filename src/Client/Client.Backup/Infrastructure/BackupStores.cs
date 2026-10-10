using System.Text.Json;
using System.Text.Json.Serialization;
using Client.Backup.Application;
using Client.Backup.Domain;
using Microsoft.Extensions.Logging;

namespace Client.Backup.Infrastructure;

/// <summary>
/// The backup component's own folder, next to the database (default %LOCALAPPDATA%\GenericPOS\Backup):
///
///   history.json    the backup history (what was made, where, its fingerprint, the last check)
///   settings.json   the backup folder and how many backups it keeps
///   staging\        copies being prepared or checked; emptied at every start
///   restore\        a backup being restored (checked, waiting for confirmation or for the restart)
///   restore-pending.json / restore-outcome.json   the confirmed restore, and what happened to the last one
///   before-restore\ the shop data as it was before each restore; never removed automatically
///
/// Kept OUTSIDE the business database on purpose: restoring a backup must never erase the record of backups or the settings.
/// </summary>
public sealed class BackupWorkspace(string root) : IBackupWorkspace
{
    public string Root { get; } = root;

    public string StagingDirectory => Path.Combine(Root, "staging");

    public string HistoryPath => Path.Combine(Root, "history.json");

    public string SettingsPath => Path.Combine(Root, "settings.json");

    public string RestoreDirectory => Path.Combine(Root, "restore");

    public string BeforeRestoreDirectory => Path.Combine(Root, "before-restore");

    public string PendingRestorePath => Path.Combine(Root, "restore-pending.json");

    public string RestoreOutcomePath => Path.Combine(Root, "restore-outcome.json");
}

/// <summary>Small JSON files written atomically (temporary file, then rename), so a crash leaves the old or the new file, never half of one.</summary>
internal static class JsonFile
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Options), cancellationToken);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Default when missing; an unreadable file is kept aside (renamed, never deleted) and the default returned.</summary>
    internal static async Task<T> ReadAsync<T>(string path, Func<T> fallback, ILogger logger, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return fallback();

        try
        {
            return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, cancellationToken), Options) ?? fallback();
        }
        catch (JsonException ex)
        {
            var aside = $"{path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
            logger.LogError(ex, "{Path} could not be read; it was kept as {Aside} and a new one is started.", path, aside);
            try
            {
                File.Move(path, aside);
            }
            catch (IOException)
            {
                // Keep reading the fallback; the file stays where it is.
            }

            return fallback();
        }
    }
}

public sealed class JsonBackupHistoryStore(BackupWorkspace workspace, ILogger<JsonBackupHistoryStore> logger) : IBackupHistoryStore
{
    public async Task<IReadOnlyList<BackupRecord>> ReadAsync(CancellationToken cancellationToken = default)
        => await JsonFile.ReadAsync<List<BackupRecord>>(workspace.HistoryPath, () => [], logger, cancellationToken);

    public Task WriteAsync(IReadOnlyList<BackupRecord> records, CancellationToken cancellationToken = default)
        => JsonFile.WriteAsync(workspace.HistoryPath, records, cancellationToken);
}

public sealed class JsonRestoreStateStore(BackupWorkspace workspace, ILogger<JsonRestoreStateStore> logger) : IRestoreStateStore
{
    public Task<PendingRestore?> ReadPendingAsync(CancellationToken cancellationToken = default)
        => JsonFile.ReadAsync<PendingRestore?>(workspace.PendingRestorePath, () => null, logger, cancellationToken);

    public Task WritePendingAsync(PendingRestore pending, CancellationToken cancellationToken = default)
        => JsonFile.WriteAsync(workspace.PendingRestorePath, pending, cancellationToken);

    public Task ClearPendingAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(workspace.PendingRestorePath)) File.Delete(workspace.PendingRestorePath);
        return Task.CompletedTask;
    }

    public Task<RestoreOutcome?> ReadOutcomeAsync(CancellationToken cancellationToken = default)
        => JsonFile.ReadAsync<RestoreOutcome?>(workspace.RestoreOutcomePath, () => null, logger, cancellationToken);

    public Task WriteOutcomeAsync(RestoreOutcome outcome, CancellationToken cancellationToken = default)
        => JsonFile.WriteAsync(workspace.RestoreOutcomePath, outcome, cancellationToken);
}

/// <summary>Settings saved by <c>backup.configure</c>; until then, the defaults from configuration (an installer may preset a folder).</summary>
public sealed class JsonBackupSettingsStore(BackupWorkspace workspace, BackupSettings defaults, ILogger<JsonBackupSettingsStore> logger) : IBackupSettingsStore
{
    public Task<BackupSettings> ReadAsync(CancellationToken cancellationToken = default)
        => JsonFile.ReadAsync(workspace.SettingsPath, () => defaults, logger, cancellationToken);

    public Task WriteAsync(BackupSettings settings, CancellationToken cancellationToken = default)
        => JsonFile.WriteAsync(workspace.SettingsPath, settings, cancellationToken);
}
