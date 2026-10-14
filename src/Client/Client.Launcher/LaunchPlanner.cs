using System.Text.Json;

namespace Client.Launcher;

/// <summary>What the launcher starts: the core folder, the module versions laid over it, and what it held back and why.</summary>
internal sealed record LaunchPlan(
    string CoreDirectory,
    string CoreVersion,
    IReadOnlyList<ModuleOverlay> Modules,
    IReadOnlyList<HeldBack> HeldBack)
{
    /// <summary>The versions the started application runs, for the updater's "confirm only what runs" (FIX-03).</summary>
    public string RunningHostVersion => CoreVersion;
}

internal sealed record ModuleOverlay(string ModuleId, string Version, string Directory);

/// <summary>An active version the launcher does not start, with the reason in plain words (logged, and shown by the application).</summary>
internal sealed record HeldBack(string Target, string Version, string Reason);

/// <summary>
/// PKG-01: decides what to start. Rules:
///   - nothing active: the installed application (no copy is made);
///   - an active core: its deployed folder, if it holds the application;
///   - active modules: their files laid over the core, if they only replace that module's own files (see <see cref="ModuleFiles"/>);
///   - an update activated but never confirmed healthy, started more than MaxStartupAttempts + 1 times by the launcher (so the
///     application did not even get far enough to roll it back itself), is held back when the previous binaries still fit the data.
/// The launcher never changes the updater's state: it only chooses what to start; the application's own startup recovery then sees
/// that the activated version did not run, and rolls it back (Stage 7 / FIX-03).
/// </summary>
internal sealed class LaunchPlanner(LauncherSettings settings, UpdateFolders folders)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string AttemptsPath => Path.Combine(folders.LauncherDirectory, "attempts.json");

    public LaunchPlan Plan()
    {
        var heldBack = new List<HeldBack>();
        var active = folders.ActiveVersions();
        var attempts = ReadAttempts();
        var stillUnconfirmed = new Dictionary<string, int>(StringComparer.Ordinal);

        // a version that keeps failing before the application can roll it back is replaced by the one before it
        string? Choose(ActiveVersion target)
        {
            if (folders.Unconfirmed(target.Target, target.Version) is not { } unconfirmed) return target.Version;

            var key = target.Target + "@" + target.Version;
            var launches = (attempts.TryGetValue(key, out var n) ? n : 0) + 1;
            stillUnconfirmed[key] = launches;
            if (launches <= settings.MaxStartupAttempts + 1) return target.Version;
            if (!unconfirmed.CanGoBack)
                return target.Version;   // the data was changed for the new version: only the application can decide (RecoveryRequired)

            heldBack.Add(new HeldBack(target.Target, target.Version, $"it was started {launches - 1} times without starting properly"));
            return target.PreviousVersion;   // null = the built-in version
        }

        // ---- the core
        var coreDirectory = settings.AppDirectory;
        var coreVersion = settings.BaselineHostVersion;
        if (active.FirstOrDefault(a => a.Target == UpdateFolders.CoreTarget) is { } core && Choose(core) is { } chosenCore)
        {
            var directory = folders.VersionDirectory(UpdateFolders.CoreTarget, chosenCore);
            if (File.Exists(Path.Combine(directory, LauncherSettings.ApplicationExe)))
                (coreDirectory, coreVersion) = (directory, chosenCore);
            else
                heldBack.Add(new HeldBack(UpdateFolders.CoreTarget, chosenCore, "its folder does not hold the application"));
        }

        // ---- the modules, over the chosen core
        var modules = new List<ModuleOverlay>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in active.Where(a => a.Target != UpdateFolders.CoreTarget))
        {
            if (Choose(module) is not { } version) continue;
            var directory = folders.VersionDirectory(module.Target, version);
            var problem = ModuleFiles.Check(module.Target, directory, coreDirectory, out var files);
            problem ??= files.FirstOrDefault(f => claimed.ContainsKey(f)) is { } shared ? $"its file {shared} is also delivered by {claimed[shared]}" : null;
            if (problem is not null)
            {
                heldBack.Add(new HeldBack(module.Target, version, problem));
                continue;
            }

            foreach (var file in files) claimed[file] = module.Target;
            modules.Add(new ModuleOverlay(module.Target, version, directory));
        }

        WriteAttempts(stillUnconfirmed);
        return new LaunchPlan(coreDirectory, coreVersion, modules, heldBack);
    }

    private Dictionary<string, int> ReadAttempts()
    {
        try
        {
            return File.Exists(AttemptsPath)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(AttemptsPath), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Only unconfirmed updates are counted; a confirmed or rolled-back one drops out.</summary>
    private void WriteAttempts(Dictionary<string, int> attempts)
    {
        try
        {
            Directory.CreateDirectory(folders.LauncherDirectory);
            File.WriteAllText(AttemptsPath, JsonSerializer.Serialize(attempts, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not counted this time: the application's own startup recovery still counts its starts
        }
    }
}

/// <summary>
/// Which files a module version may lay over the core. A module owns the assemblies named after it - module id "cash-management" owns
/// CashManagement.*  (the naming rule ARCH-PKG-002 keeps true) - and may only REPLACE files the core already has (the core's dependency
/// list names every assembly the application loads), plus their translations in a language folder (ar/POS.UI.resources.dll).
/// A new assembly, or another module's or the core's file, needs a core update.
/// </summary>
internal static class ModuleFiles
{
    /// <summary>Null when the module version can be laid over the core; otherwise why not. <paramref name="files"/> = its files, relative.</summary>
    public static string? Check(string moduleId, string versionDirectory, string coreDirectory, out IReadOnlyList<string> files)
    {
        files = [];
        if (!Directory.Exists(versionDirectory)) return "its folder is missing";

        var prefix = moduleId.Replace("-", string.Empty, StringComparison.Ordinal) + ".";
        var list = new List<string>();
        foreach (var path in Directory.EnumerateFiles(versionDirectory, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(versionDirectory, path);
            var segments = relative.Split(Path.DirectorySeparatorChar);
            if (segments[0] == UpdateFolders.PackageInfoFolder) continue;

            var name = segments[^1];
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return $"it contains {relative.Replace('\\', '/')}, which does not belong to it";

            var allowed = segments.Length switch
            {
                1 => File.Exists(Path.Combine(coreDirectory, name))
                     || (Path.GetExtension(name).ToLowerInvariant() is ".pdb" or ".xml"
                         && File.Exists(Path.Combine(coreDirectory, Path.ChangeExtension(name, ".dll")))),   // symbols / docs of an assembly it replaces
                2 => IsLanguageFolder(segments[0]) && name.EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase)
                     && File.Exists(Path.Combine(coreDirectory, name[..^".resources.dll".Length] + ".dll")),
                _ => false
            };
            if (!allowed)
                return $"it adds {relative.Replace('\\', '/')}, which the installed application does not have (that needs an application update)";
            list.Add(relative);
        }

        if (list.Count == 0) return "its folder holds no files";
        files = list;
        return null;
    }

    private static bool IsLanguageFolder(string name)
        => name.Length is >= 2 and <= 12 && name.All(c => char.IsAsciiLetter(c) || c == '-');
}
