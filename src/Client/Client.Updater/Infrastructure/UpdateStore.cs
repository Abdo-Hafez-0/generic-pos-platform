using System.Text.Json;
using System.Text.Json.Serialization;
using Client.Updater.Application;
using Client.Updater.Domain;

namespace Client.Updater.Infrastructure;

/// <summary>
/// Filesystem layout for updates. Kept OUTSIDE the operational SQLite database and outside the application directory
/// (default %LOCALAPPDATA%\GenericPOS\Updates), so updates preserve data and survive application updates:
///
///   downloads/&lt;packageId&gt;.gpkg                 downloaded, UNTRUSTED packages
///   staging/&lt;packageId&gt;/                       verified payload being prepared (never active)
///   installed/&lt;target&gt;/&lt;version&gt;/              side-by-side deployed versions (with .package/manifest.json)
///   installed/&lt;target&gt;/active.json             THE active-version pointer (atomic switch; target = "core" or a module id)
///   journal/&lt;packageId&gt;.json                   persisted update state machine
///   restore/&lt;packageId&gt;/                       database restore points
///
/// Old versions are never overwritten: a new version is deployed next to the old one and activation is a single atomic
/// pointer write. Rollback is another pointer write, so there is never a half-old/half-new installation.
/// </summary>
public sealed class UpdateStore(string root) : IUpdateStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string Root { get; } = root;
    public string DownloadsDir => Path.Combine(Root, "downloads");
    public string StagingDir => Path.Combine(Root, "staging");
    public string InstalledDir => Path.Combine(Root, "installed");
    public string JournalDir => Path.Combine(Root, "journal");
    public string RestoreDir => Path.Combine(Root, "restore");

    public string DownloadPath(Guid packageId) => Path.Combine(DownloadsDir, packageId.ToString("N") + Updates.Package.PackageFormat.Extension);
    public string StagingPath(Guid packageId) => Path.Combine(StagingDir, packageId.ToString("N"));
    public string TargetDir(string targetId) => Path.Combine(InstalledDir, targetId);
    public string VersionDir(string targetId, string version) => Path.Combine(TargetDir(targetId), version);
    public string ActivePointerPath(string targetId) => Path.Combine(TargetDir(targetId), "active.json");
    public string JournalPath(Guid packageId) => Path.Combine(JournalDir, packageId.ToString("N") + ".json");

    public void EnsureDirectories()
    {
        foreach (var dir in new[] { DownloadsDir, StagingDir, InstalledDir, JournalDir, RestoreDir })
            Directory.CreateDirectory(dir);
    }

    // ----------------------------------------------------------- active pointer

    public ActivePointer? ReadActive(string targetId)
    {
        var path = ActivePointerPath(targetId);
        if (!File.Exists(path)) return null;

        try
        {
            var pointer = JsonSerializer.Deserialize<ActivePointer>(File.ReadAllText(path), Json);
            return pointer is { Version.Length: > 0 } ? pointer : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Atomically switches the active version. Throws IOException if the pointer cannot be replaced (nothing is changed then).</summary>
    public void WriteActive(string targetId, ActivePointer pointer)
    {
        Directory.CreateDirectory(TargetDir(targetId));
        AtomicWrite(ActivePointerPath(targetId), JsonSerializer.Serialize(pointer, Json));
    }

    /// <summary>Removes the pointer, reverting the target to the built-in baseline.</summary>
    public void ClearActive(string targetId)
    {
        var path = ActivePointerPath(targetId);
        if (File.Exists(path)) File.Delete(path);
    }

    public IReadOnlyList<string> ListInstalledTargets()
        => Directory.Exists(InstalledDir)
            ? Directory.GetDirectories(InstalledDir).Select(d => Path.GetFileName(d)!).ToList()
            : [];

    // ----------------------------------------------------------- journal

    public void SaveJournal(UpdateJournal journal)
    {
        Directory.CreateDirectory(JournalDir);
        AtomicWrite(JournalPath(journal.PackageId), JsonSerializer.Serialize(journal, Json));
    }

    public UpdateJournal? LoadJournal(Guid packageId)
    {
        var path = JournalPath(packageId);
        if (!File.Exists(path)) return null;

        try
        {
            return JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    public IReadOnlyList<UpdateJournal> ListJournals()
    {
        if (!Directory.Exists(JournalDir)) return [];

        var result = new List<UpdateJournal>();
        foreach (var file in Directory.GetFiles(JournalDir, "*.json"))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllText(file), Json);
                if (journal is not null) result.Add(journal);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // An unreadable journal is skipped; it can never activate anything.
            }
        }

        return result;
    }

    // ----------------------------------------------------------- helpers

    private static void AtomicWrite(string path, string content)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, content);
        try
        {
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temp)) File.Delete(temp);
            throw;
        }
    }

    public static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers in staging/downloads are harmless: they are never activated.
        }
    }
}
