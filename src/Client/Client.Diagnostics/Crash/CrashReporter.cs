using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Client.Diagnostics.Logging;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Modules;

namespace Client.Diagnostics.Crash;

/// <summary>Where an unexpected failure happened.</summary>
public enum CrashOrigin
{
    /// <summary>On the screen's thread: the application closes after the report.</summary>
    Screen,

    /// <summary>On a background thread: the process ends.</summary>
    Background,

    /// <summary>While starting: the application could not open.</summary>
    Startup,
}

/// <summary>One crash report kept in Logs\crash.</summary>
public sealed record CrashReportInfo(string FileName, DateTimeOffset At, string Summary);

/// <summary>
/// Writes a crash report (MISS-05) when something fails that nobody handled: <c>Logs\crash\crash-yyyyMMdd-HHmmss-fff.txt</c> with the time,
/// where it happened, the application, Windows and .NET versions, the modules, the exception and the last log lines - all masked by
/// <see cref="LogRedactor"/>, never business data from the database. The failure is also written to the log. It never throws: a report that
/// cannot be written must not hide the original failure.
/// </summary>
public sealed class CrashReporter(RollingLogWriter writer, TimeProvider? clock = null, IServiceProvider? services = null)
{
    public const string FilePrefix = "crash-";
    private const string SeenFile = "seen.txt";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public DiagnosticsFolders Folders => writer.Folders;

    /// <summary>For a failure before the host exists: writes into %LOCALAPPDATA%\GenericPOS\Logs.</summary>
    public static CrashReporter Fallback() => new(new RollingLogWriter(DiagnosticsFolders.Default, new DiagnosticsConfiguration()));

    /// <summary>Writes the report; returns its path, or null when it could not be written.</summary>
    public string? Report(Exception exception, CrashOrigin origin)
    {
        try
        {
            writer.Write("CRT", "Client.Diagnostics.Crash", $"Unexpected failure ({origin}); a crash report is written.", exception);
            var now = _clock.GetLocalNow();
            Directory.CreateDirectory(Folders.Crashes);
            var path = Path.Combine(Folders.Crashes, FilePrefix + now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, LogRedactor.Redact(Compose(exception, origin, now)), new UTF8Encoding(false));
            return path;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"The crash report could not be written: {ex.Message}");
            return null;
        }
    }

    private string Compose(Exception exception, CrashOrigin origin, DateTimeOffset now)
    {
        var text = new StringBuilder()
            .AppendLine("GenericPOS crash report")
            .AppendLine($"Time:        {now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}")
            .AppendLine($"Where:       {origin}")
            .AppendLine($"Summary:     {Summary(exception)}")
            .AppendLine($"Application: {ApplicationVersion()}")
            .AppendLine($"Windows:     {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})")
            .AppendLine($".NET:        {RuntimeInformation.FrameworkDescription}")
            .AppendLine($"Culture:     {CultureInfo.CurrentCulture.Name} / {CultureInfo.CurrentUICulture.Name}, time zone {TimeZoneInfo.Local.Id}")
            .AppendLine("Modules:");
        foreach (var module in Modules()) text.AppendLine("  " + module);
        text.AppendLine().AppendLine("Exception:").AppendLine(exception.ToString())
            .AppendLine().AppendLine($"Last log lines (up to {RollingLogWriter.RecentLineCount}):");
        foreach (var line in writer.RecentLines()) text.AppendLine(line);
        return text.ToString();
    }

    private IEnumerable<string> Modules()
    {
        try
        {
            var registry = services?.GetService<IModuleRegistry>();
            if (registry is null) return ["(not known yet)"];
            var loaded = registry.GetAll().Select(m => $"{m.Manifest.ModuleId.Value} {m.Manifest.Version}").Order(StringComparer.Ordinal).ToList();
            return loaded.Count == 0 ? ["(none loaded yet)"] : loaded;
        }
        catch (Exception)
        {
            return ["(could not be read)"];
        }
    }

    /// <summary>"Type: message" of the innermost exception, one line.</summary>
    public static string Summary(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var line = $"{inner.GetType().Name}: {inner.Message}".ReplaceLineEndings(" ");
        return LogRedactor.Redact(line.Length > 300 ? line[..300] + "..." : line);
    }

    public static string ApplicationVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(CrashReporter).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() ?? "unknown" : informational.Split('+')[0];
    }

    // ------------------------------------------------------------------ the reports kept

    /// <summary>The crash reports in the folder, newest first.</summary>
    public IReadOnlyList<CrashReportInfo> List()
    {
        if (!Directory.Exists(Folders.Crashes)) return [];
        var reports = new List<CrashReportInfo>();
        foreach (var file in new DirectoryInfo(Folders.Crashes).EnumerateFiles(FilePrefix + "*.txt"))
        {
            if (!DateTime.TryParseExact(file.Name[FilePrefix.Length..^4], "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) continue;
            reports.Add(new CrashReportInfo(file.Name, new DateTimeOffset(at, _clock.LocalTimeZone.GetUtcOffset(at)), ReadSummary(file.FullName)));
        }

        return reports.OrderByDescending(r => r.At).ToList();
    }

    private static string ReadSummary(string path)
    {
        try
        {
            return File.ReadLines(path).Take(10).FirstOrDefault(l => l.StartsWith("Summary:", StringComparison.Ordinal))?["Summary:".Length..].Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reports newer than the last time someone looked at them on the Support screen.</summary>
    public IReadOnlyList<CrashReportInfo> Unseen()
    {
        var seen = SeenAt();
        return List().Where(r => seen is null || r.At > seen).ToList();
    }

    /// <summary>Someone looked at the reports (the Support screen was opened).</summary>
    public void MarkSeen()
    {
        try
        {
            Directory.CreateDirectory(Folders.Crashes);
            File.WriteAllText(Path.Combine(Folders.Crashes, SeenFile), _clock.GetLocalNow().ToString("o", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private DateTimeOffset? SeenAt()
    {
        try
        {
            var path = Path.Combine(Folders.Crashes, SeenFile);
            return File.Exists(path) && DateTimeOffset.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
