using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Client.Launcher;

/// <summary>
/// PKG-01 (user decision 2026-10-14: an assembled folder): when modules are active, the application runs from a folder that holds the
/// core's files with each active module's files laid over them - one folder per combination of versions, under runs/ in the update
/// folder. It is built next to its final place and moved there in one step, so a half-built folder is never started; the folder in use
/// and the one before it are kept (the way back), older ones are removed.
/// </summary>
internal sealed class RunFolders(UpdateFolders folders)
{
    private const string CompleteMarker = ".run.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string HistoryPath => Path.Combine(folders.RunsDirectory, "last.json");

    /// <summary>The folder to start the application from: the core itself when no module is laid over it.</summary>
    public string Prepare(LaunchPlan plan)
    {
        if (plan.Modules.Count == 0) return plan.CoreDirectory;

        var folder = Path.Combine(folders.RunsDirectory, Key(plan));
        if (!File.Exists(Path.Combine(folder, CompleteMarker)))
            Build(plan, folder);
        Remember(folder);
        return folder;
    }

    /// <summary>The same versions over the same core give the same folder; a reinstalled core (another build) gives a new one.</summary>
    private static string Key(LaunchPlan plan)
    {
        var text = new StringBuilder();
        text.Append("core\t").Append(plan.CoreDirectory).Append('\t').Append(plan.CoreVersion).Append('\t').Append(Fingerprint(plan.CoreDirectory)).Append('\n');
        foreach (var module in plan.Modules.OrderBy(m => m.ModuleId, StringComparer.Ordinal))
            text.Append("module\t").Append(module.ModuleId).Append('\t').Append(module.Version).Append('\t').Append(module.Directory).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    private static string Fingerprint(string coreDirectory)
    {
        var text = new StringBuilder();
        foreach (var name in new[] { "Client.Desktop.dll", "Client.Desktop.deps.json" })
        {
            var file = new FileInfo(Path.Combine(coreDirectory, name));
            text.Append(name).Append(':').Append(file.Exists ? $"{file.Length}/{file.LastWriteTimeUtc.Ticks}" : "-").Append(';');
        }

        return text.ToString();
    }

    private static void Build(LaunchPlan plan, string folder)
    {
        var building = folder + ".building-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            Copy(plan.CoreDirectory, building, skip: name => name == UpdateFolders.PackageInfoFolder);
            foreach (var module in plan.Modules)
                Copy(module.Directory, building, skip: name => name == UpdateFolders.PackageInfoFolder);

            File.WriteAllText(Path.Combine(building, CompleteMarker), JsonSerializer.Serialize(new
            {
                core = plan.CoreDirectory,
                coreVersion = plan.CoreVersion,
                modules = plan.Modules.Select(m => new { m.ModuleId, m.Version }),
                builtAt = DateTimeOffset.Now
            }, Json));

            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);   // an earlier, unfinished attempt
            Directory.Move(building, folder);
        }
        catch
        {
            TryDelete(building);
            throw;
        }
    }

    private static void Copy(string source, string target, Func<string, bool> skip)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (!skip(name)) Copy(directory, Path.Combine(target, name), _ => false);
        }

        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
    }

    /// <summary>Keeps the folder now in use and the one used before it; removes the others it can (a folder still in use stays).</summary>
    private void Remember(string current)
    {
        var history = ReadHistory();
        if (!string.Equals(history.Current, current, StringComparison.OrdinalIgnoreCase))
            history = new RunHistory(current, history.Current);

        try
        {
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // without the history nothing is removed
        }

        foreach (var directory in Directory.GetDirectories(folders.RunsDirectory))
        {
            if (string.Equals(directory, history.Current, StringComparison.OrdinalIgnoreCase)
                || string.Equals(directory, history.Previous, StringComparison.OrdinalIgnoreCase)) continue;
            Retire(directory);
        }
    }

    /// <summary>
    /// Renamed first, deleted after: a folder an application still runs from cannot be renamed, so it is left whole (never half
    /// deleted) and removed at a later start.
    /// </summary>
    private static void Retire(string directory)
    {
        var retired = directory.Contains(".retired-", StringComparison.Ordinal) || directory.Contains(".building-", StringComparison.Ordinal)
            ? directory
            : directory + ".retired-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            if (retired != directory) Directory.Move(directory, retired);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        TryDelete(retired);
    }

    private RunHistory ReadHistory()
    {
        try
        {
            return File.Exists(HistoryPath) ? JsonSerializer.Deserialize<RunHistory>(File.ReadAllText(HistoryPath), Json) ?? new RunHistory(null, null) : new RunHistory(null, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new RunHistory(null, null);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still in use (another copy of the application runs from it): removed at a later start
        }
    }

    private sealed record RunHistory(string? Current, string? Previous);
}
