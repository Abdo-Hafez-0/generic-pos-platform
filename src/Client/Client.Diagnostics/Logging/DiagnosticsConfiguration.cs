using Microsoft.Extensions.Logging;

namespace Client.Diagnostics.Logging;

/// <summary>Bound from the "Diagnostics" configuration section (MISS-05). Defaults are the user's decisions of 2026-10-12.</summary>
public sealed class DiagnosticsConfiguration
{
    public const string SectionName = "Diagnostics";

    /// <summary>Where logs and crash reports go. Empty = "Logs" next to the database (with Backup and CloudBackup).</summary>
    public string? LogDirectory { get; set; }

    /// <summary>Log files older than this many days are removed.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>A new part of the day's file is started after this size.</summary>
    public long MaxFileBytes { get; set; } = 10L * 1024 * 1024;

    /// <summary>When all log files together are larger, the oldest are removed (crash reports are not counted and are kept).</summary>
    public long MaxTotalBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>The lowest level written to the file. Microsoft.* and System.* are written from Warning only.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}

/// <summary>The diagnostics folders: <c>Logs\genericpos-yyyyMMdd.log</c> and <c>Logs\crash\crash-*.txt</c>.</summary>
public sealed record DiagnosticsFolders(string Logs)
{
    public string Crashes => Path.Combine(Logs, "crash");

    /// <summary>For a failure before the host is built (the data folder is not known yet): %LOCALAPPDATA%\GenericPOS\Logs.</summary>
    public static DiagnosticsFolders Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GenericPOS", "Logs"));
}
