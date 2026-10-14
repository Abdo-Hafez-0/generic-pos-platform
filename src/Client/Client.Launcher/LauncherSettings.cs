using System.Text.Json;

namespace Client.Launcher;

/// <summary>
/// Where the launcher finds the installed application and the updater's folders. Read the way the application reads them (its
/// appsettings.json, then GENERICPOS_ environment variables), so both always look at the same update folder.
/// </summary>
internal sealed record LauncherSettings(string AppDirectory, string UpdateRoot, string BaselineHostVersion, int MaxStartupAttempts)
{
    public const string ApplicationExe = "Client.Desktop.exe";
    public const string EnvironmentPrefix = "GENERICPOS_";

    /// <summary>
    /// The installed application is the "app" folder next to GenericPOS.exe; "--app &lt;folder&gt;" or GENERICPOS_LAUNCHER_APPDIR
    /// choose another (development, tests).
    /// </summary>
    public static LauncherSettings Resolve(string launcherDirectory, IReadOnlyList<string> args, Func<string, string?> environment)
    {
        var appDirectory = ArgumentValue(args, "--app") ?? environment(EnvironmentPrefix + "LAUNCHER_APPDIR") ?? Path.Combine(launcherDirectory, "app");
        appDirectory = Path.GetFullPath(appDirectory);

        var updater = ReadUpdaterSection(Path.Combine(appDirectory, "appsettings.json"));
        string? Setting(string name) => environment(EnvironmentPrefix + "Updater__" + name) is { Length: > 0 } fromEnvironment
            ? fromEnvironment
            : updater.TryGetValue(name, out var fromFile) && fromFile.Length > 0 ? fromFile : null;

        var root = Setting("UpdateRoot")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenericPOS", "Updates");
        var attempts = int.TryParse(Setting("MaxStartupAttempts"), out var max) && max > 0 ? max : 2;
        return new LauncherSettings(appDirectory, Path.GetFullPath(root), Setting("BaselineHostVersion") ?? "1.0.0", attempts);
    }

    /// <summary>The arguments the launcher keeps for itself; the others are handed to the application.</summary>
    public static IReadOnlyList<string> ApplicationArguments(IReadOnlyList<string> args)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], "--app", StringComparison.OrdinalIgnoreCase)) { i++; continue; }
            rest.Add(args[i]);
        }

        return rest;
    }

    private static string? ArgumentValue(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    private static Dictionary<string, string> ReadUpdaterSection(string appSettingsPath)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(appSettingsPath)) return values;
            using var document = JsonDocument.Parse(File.ReadAllText(appSettingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var section in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(section.Name, "Updater", StringComparison.OrdinalIgnoreCase) || section.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var setting in section.Value.EnumerateObject())
                    if (setting.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                        values[setting.Name] = setting.Value.ToString();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // an unreadable appsettings.json: the defaults are used; the application reports its own configuration problems
        }

        return values;
    }
}
