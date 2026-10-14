using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Client.Launcher;

/// <summary>
/// PKG-01: GenericPOS.exe. Starts the application the updater made active (see <see cref="LaunchPlanner"/>), and exits.
/// If anything about the updates cannot be handled, it starts the installed application as it is: an update problem must never
/// keep the shop from selling. Only when the installed application itself is missing does it show an error.
/// </summary>
internal static class Program
{
    /// <summary>Set for the application: restart through the launcher (so a newly activated update is used).</summary>
    public const string LauncherPathVariable = "GENERICPOS_LAUNCHER_PATH";

    /// <summary>Set for the application: the active versions the launcher did not start ("target@version: reason" lines).</summary>
    public const string HeldBackVariable = "GENERICPOS_LAUNCHER_HELD_BACK";

    [STAThread]
    private static int Main(string[] args)
    {
        var launcherPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GenericPOS.exe");
        var settings = LauncherSettings.Resolve(AppContext.BaseDirectory, args, Environment.GetEnvironmentVariable);
        var log = new LauncherLog(new UpdateFolders(settings.UpdateRoot).LauncherDirectory);

        var start = Prepare(settings, launcherPath, LauncherSettings.ApplicationArguments(args), log);
        if (!File.Exists(start.FileName))
        {
            log.Write($"The application was not found at {start.FileName}.");
            ShowError($"GenericPOS cannot start: the application was not found in\n{Path.GetDirectoryName(start.FileName)}\n\nPlease reinstall GenericPOS.");
            return 2;
        }

        try
        {
            using var process = Process.Start(start);
            log.Write($"Started {start.FileName} (core {start.Environment[UpdaterRunningHostVersion]}).");
            return 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.Write($"Could not start {start.FileName}: {ex.Message}");
            ShowError("GenericPOS cannot start: " + ex.Message);
            return 3;
        }
    }

    public const string UpdaterRunningHostVersion = "GENERICPOS_Updater__RunningHostVersion";

    /// <summary>What to start and with what settings; falls back to the installed application when the update folders cannot be used.</summary>
    public static ProcessStartInfo Prepare(LauncherSettings settings, string launcherPath, IReadOnlyList<string> applicationArgs, LauncherLog log)
    {
        var folder = settings.AppDirectory;
        var version = settings.BaselineHostVersion;
        var heldBack = new List<HeldBack>();
        try
        {
            var folders = new UpdateFolders(settings.UpdateRoot);
            var plan = new LaunchPlanner(settings, folders).Plan();
            folder = new RunFolders(folders).Prepare(plan);
            version = plan.RunningHostVersion;
            heldBack.AddRange(plan.HeldBack);
            foreach (var held in plan.HeldBack)
                log.Write($"Not started: {held.Target} {held.Version} - {held.Reason}.");
            if (plan.Modules.Count > 0)
                log.Write($"Modules laid over core {plan.CoreVersion}: {string.Join(", ", plan.Modules.Select(m => m.ModuleId + " " + m.Version))}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            log.Write($"The updates could not be prepared ({ex.Message}); the installed application is started.");
            (folder, version) = (settings.AppDirectory, settings.BaselineHostVersion);
            heldBack.Add(new HeldBack("updates", "-", "the update folder could not be used: " + ex.Message));
        }

        var start = new ProcessStartInfo(Path.Combine(folder, LauncherSettings.ApplicationExe))
        {
            WorkingDirectory = folder,
            UseShellExecute = false
        };
        foreach (var arg in applicationArgs) start.ArgumentList.Add(arg);
        start.Environment[UpdaterRunningHostVersion] = version;
        start.Environment["GENERICPOS_Updater__UpdateRoot"] = settings.UpdateRoot;
        start.Environment[LauncherPathVariable] = launcherPath;
        start.Environment[HeldBackVariable] = string.Join("\n", heldBack.Select(h => $"{h.Target}@{h.Version}: {h.Reason}"));
        return start;
    }

    private static void ShowError(string text)
    {
        if (OperatingSystem.IsWindows()) _ = MessageBoxW(IntPtr.Zero, text, "GenericPOS", 0x10 /* MB_ICONERROR */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
}

/// <summary>A short log of what the launcher did (launcher/launcher.log in the update folder; restarted when it grows past 1 MB).</summary>
internal sealed class LauncherLog(string directory)
{
    private const long MaxBytes = 1024 * 1024;

    public string Path => System.IO.Path.Combine(directory, "launcher.log");

    public void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(Path) && new FileInfo(Path).Length > MaxBytes) File.Delete(Path);
            File.AppendAllText(Path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the launcher never fails because of its log
        }
    }
}
