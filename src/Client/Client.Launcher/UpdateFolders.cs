using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client.Launcher;

/// <summary>
/// Reads (never writes) what Client.Updater keeps on disk (Stage 7 layout, a contract checked by tests):
///   installed/&lt;target&gt;/&lt;version&gt;/    a deployed version (payload files + .package/manifest.json)
///   installed/&lt;target&gt;/active.json   the active version and the one before it
///   journal/&lt;packageId&gt;.json         the state of each update (Activated = not yet confirmed healthy)
/// Anything unreadable counts as "not there": the launcher then starts what it can be sure of.
/// </summary>
internal sealed class UpdateFolders(string root)
{
    public const string CoreTarget = "core";
    public const string PackageInfoFolder = ".package";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public string Root { get; } = root;
    public string InstalledDirectory => Path.Combine(Root, "installed");
    public string RunsDirectory => Path.Combine(Root, "runs");
    public string LauncherDirectory => Path.Combine(Root, "launcher");

    public string VersionDirectory(string target, string version) => Path.Combine(InstalledDirectory, target, version);

    /// <summary>Targets with an active pointer: (target, active version, previous version).</summary>
    public IReadOnlyList<ActiveVersion> ActiveVersions()
    {
        var result = new List<ActiveVersion>();
        if (!Directory.Exists(InstalledDirectory)) return result;

        foreach (var targetDirectory in Directory.GetDirectories(InstalledDirectory).OrderBy(d => d, StringComparer.Ordinal))
        {
            var pointerPath = Path.Combine(targetDirectory, "active.json");
            try
            {
                if (!File.Exists(pointerPath)) continue;
                var pointer = JsonSerializer.Deserialize<PointerFile>(File.ReadAllText(pointerPath), Json);
                if (pointer is { Version.Length: > 0 } && IsVersion(pointer.Version))
                    result.Add(new ActiveVersion(Path.GetFileName(targetDirectory), pointer.Version, IsVersion(pointer.PreviousVersion) ? pointer.PreviousVersion : null));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // unreadable pointer: the target runs its built-in version
            }
        }

        return result;
    }

    /// <summary>The update that activated this version and is not yet confirmed healthy, or null.</summary>
    public UnconfirmedUpdate? Unconfirmed(string target, string version)
    {
        var journalDirectory = Path.Combine(Root, "journal");
        if (!Directory.Exists(journalDirectory)) return null;

        foreach (var file in Directory.GetFiles(journalDirectory, "*.json"))
        {
            try
            {
                var journal = JsonSerializer.Deserialize<JournalFile>(File.ReadAllText(file), Json);
                if (journal is null || journal.State != "Activated" || journal.TargetId != target || journal.Version != version) continue;
                return new UnconfirmedUpdate(target, version, !journal.MigrationRequired || journal.OldBinaryCompatible);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // an unreadable journal never makes the launcher hold anything back
            }
        }

        return null;
    }

    private static bool IsVersion(string? value)
        => value is { Length: > 0 } && value.Split('.') is { Length: 3 } parts && parts.All(p => int.TryParse(p, out var n) && n >= 0);

    private sealed class PointerFile
    {
        public string Version { get; set; } = string.Empty;
        public string? PreviousVersion { get; set; }
    }

    private sealed class JournalFile
    {
        public string TargetId { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string State { get; set; } = string.Empty;
        public bool MigrationRequired { get; set; }
        public bool OldBinaryCompatible { get; set; } = true;
    }
}

internal sealed record ActiveVersion(string Target, string Version, string? PreviousVersion);

/// <summary>An activated update that has not proven a healthy start yet; <see cref="CanGoBack"/> = the previous binaries still fit the data.</summary>
internal sealed record UnconfirmedUpdate(string Target, string Version, bool CanGoBack);
