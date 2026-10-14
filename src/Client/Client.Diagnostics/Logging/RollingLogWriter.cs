using System.Globalization;
using System.Text;

namespace Client.Diagnostics.Logging;

/// <summary>
/// The log file (MISS-05): <c>genericpos-yyyyMMdd.log</c> per local day, a new part (<c>-2</c>, <c>-3</c>...) after the size limit; files
/// older than the retention and the oldest beyond the total limit are removed when a file is opened. Every line is masked by
/// <see cref="LogRedactor"/> and written at once (a crash loses nothing that was logged). The file stays readable while it is written, so
/// the support file can include it. The last lines are also kept in memory for a crash report. Never throws: a log that cannot be written
/// must not break the till.
/// </summary>
public sealed class RollingLogWriter : IDisposable
{
    public const string FilePrefix = "genericpos-";
    public const string FileExtension = ".log";
    public const int RecentLineCount = 200;

    private readonly object _lock = new();
    private readonly DiagnosticsFolders _folders;
    private readonly DiagnosticsConfiguration _options;
    private readonly TimeProvider _clock;
    private readonly Queue<string> _recent = new();
    private StreamWriter? _writer;
    private string? _path;
    private DateOnly _day;
    private bool _failed;

    public RollingLogWriter(DiagnosticsFolders folders, DiagnosticsConfiguration options, TimeProvider? clock = null)
    {
        _folders = folders;
        _options = options;
        _clock = clock ?? TimeProvider.System;
    }

    public DiagnosticsFolders Folders => _folders;

    /// <summary>The file being written now (null before the first line).</summary>
    public string? CurrentFile
    {
        get { lock (_lock) return _path; }
    }

    public void Write(string level, string category, string message, Exception? exception = null)
    {
        var now = _clock.GetLocalNow();
        var text = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)).Append(" [").Append(level).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception is not null) text.AppendLine().Append(exception);
        var entry = LogRedactor.Redact(text.ToString()).Replace("\r\n", "\n").Replace("\n", Environment.NewLine + "    ");

        lock (_lock)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RecentLineCount) _recent.Dequeue();

            try
            {
                EnsureFile(now);
                _writer!.WriteLine(entry);
                _writer.Flush();
                if (_writer.BaseStream.Length >= _options.MaxFileBytes) Close();   // the next line opens the next part
                _failed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Close();
                if (!_failed) System.Diagnostics.Debug.WriteLine($"The log file cannot be written: {ex.Message}");
                _failed = true;
            }
        }
    }

    /// <summary>The last lines written (already masked), oldest first - for a crash report.</summary>
    public IReadOnlyList<string> RecentLines()
    {
        lock (_lock) return _recent.ToList();
    }

    private void EnsureFile(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.DateTime);
        if (_writer is not null && day == _day) return;
        Close();
        _day = day;
        Directory.CreateDirectory(_folders.Logs);
        CleanUp(day);

        var stem = FilePrefix + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        for (var part = 1; ; part++)
        {
            var path = Path.Combine(_folders.Logs, (part == 1 ? stem : $"{stem}-{part}") + FileExtension);
            if (File.Exists(path) && new FileInfo(path).Length >= _options.MaxFileBytes) continue;
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            _path = path;
            return;
        }
    }

    /// <summary>Removes files older than the retention, then the oldest while all together are over the total limit.</summary>
    private void CleanUp(DateOnly today)
    {
        var files = LogFiles(_folders.Logs).ToList();
        var oldest = today.AddDays(-Math.Max(1, _options.RetentionDays) + 1);
        foreach (var file in files.Where(f => f.Day < oldest).ToList())
        {
            TryDelete(file.Info);
            files.Remove(file);
        }

        var total = files.Sum(f => f.Info.Length);
        foreach (var file in files.OrderBy(f => f.Day).ThenBy(f => f.Info.Name, StringComparer.Ordinal).ToList())
        {
            if (total <= _options.MaxTotalBytes) break;
            total -= file.Info.Length;
            TryDelete(file.Info);
        }
    }

    /// <summary>The log files of a folder with the day their name gives (other files are never touched).</summary>
    public static IEnumerable<(FileInfo Info, DateOnly Day)> LogFiles(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var info in new DirectoryInfo(folder).EnumerateFiles(FilePrefix + "*" + FileExtension))
        {
            var stem = info.Name[FilePrefix.Length..^FileExtension.Length];
            if (stem.Length >= 8 && DateOnly.TryParseExact(stem[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                yield return (info, day);
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // removed next time
        }
    }

    private void Close()
    {
        try
        {
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _writer = null;
    }

    public void Dispose()
    {
        lock (_lock) Close();
    }
}
