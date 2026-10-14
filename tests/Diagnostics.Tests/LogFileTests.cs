using Client.Diagnostics.Logging;
using Microsoft.Extensions.Logging;

namespace Diagnostics.Tests;

/// <summary>A clock whose local time zone is UTC, so the log file's "local day" is predictable on any test machine.</summary>
public sealed class UtcClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "genericpos-diag-" + Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>MISS-05: the log file - masking, one file per day, parts, retention, total size, shared reading, levels.</summary>
public sealed class LogFileTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly UtcClock _clock = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => _temp.Dispose();

    private DiagnosticsFolders Folders => new(Path.Combine(_temp.Path, "Logs"));

    private RollingLogWriter Writer(DiagnosticsConfiguration? options = null) => new(Folders, options ?? new DiagnosticsConfiguration(), _clock);

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Theory]
    [InlineData("Backup token gpb_zfAbc123-_xyzLONGTOKEN entered", "gpb_***")]
    [InlineData("admin key gpa_0123456789abcdef", "gpa_***")]
    [InlineData("activating with ABCDE-FGHJK-LMNPQ-RSTUV-WXYZ2", "activating with ***")]
    [InlineData("code K7QF-M2XA-9P3D-QW4E-R5TY-6V7N-8BC typed", "code *** typed")]
    [InlineData("code k7qfm2xa9p3dqw4er5ty6v7n8bc typed", "code *** typed")]
    [InlineData("Authorization: Bearer abc.def-ghi_jkl", "Bearer ***")]
    [InlineData("connect password=hunter2; user=sara", "password=***")]
    [InlineData("accessKey: s3cr3t-value", "accessKey: ***")]
    public void Secrets_are_masked(string text, string expected)
    {
        var masked = LogRedactor.Redact(text);
        Assert.Contains(expected, masked);
        foreach (var secret in new[] { "zfAbc123", "0123456789abcdef", "FGHJK", "M2XA", "m2xa", "abc.def", "hunter2", "s3cr3t" })
            Assert.DoesNotContain(secret, masked);
    }

    [Fact]
    public void Armored_blocks_and_private_keys_are_removed_whole_and_ordinary_text_is_kept()
    {
        var text = "before\n-----BEGIN PRIVATE KEY-----\nMIGHAgEAMBMGByqGSM49\n-----END PRIVATE KEY-----\nmiddle\n"
                   + "-----BEGIN GENERICPOS RESTORE KEY-----\neyJ2ZXJzaW9uIjox\n#cf5bb9f2\n-----END GENERICPOS RESTORE KEY-----\nafter";
        var masked = LogRedactor.Redact(text);
        Assert.DoesNotContain("MIGHAgEAMBMGByqGSM49", masked);
        Assert.DoesNotContain("eyJ2ZXJzaW9uIjox", masked);
        Assert.Contains("-----BEGIN GENERICPOS RESTORE KEY----- *** -----END GENERICPOS RESTORE KEY-----", masked);
        Assert.Contains("before", masked);
        Assert.Contains("after", masked);
        Assert.DoesNotContain("MIGH", LogRedactor.Redact("-----BEGIN PRIVATE KEY-----\nMIGHAgEA cut short"));   // no END line: removed to the end

        foreach (var ordinary in new[] { "Backup genericpos-20261012-150042.db made.", "Sale 9b2f6c1e-1d2a-4c55-8e7a-0f3d2b1a9c8e total 12.50",
                     "The backup access key was entered.", "User sara signed in (2 failed attempts before).", "Token expired at 10:00" })
            Assert.Equal(ordinary, LogRedactor.Redact(ordinary));
    }

    [Fact]
    public void Lines_go_to_the_days_file_masked_with_exceptions_indented_and_the_file_stays_readable()
    {
        using var writer = Writer();
        writer.Write("INF", "Client.Backup", "Backup made with gpb_secretsecretsecret");
        writer.Write("ERR", "POS", "Checkout failed", new InvalidOperationException("boom"));

        var path = Path.Combine(Folders.Logs, "genericpos-20261012.log");
        Assert.Equal(path, writer.CurrentFile);
        var text = ReadShared(path);   // while the writer still has it open
        Assert.Contains("2026-10-12 09:00:00.000 +00:00 [INF] Client.Backup: Backup made with gpb_***", text);
        Assert.DoesNotContain("secretsecret", text);
        Assert.Contains("[ERR] POS: Checkout failed" + Environment.NewLine + "    System.InvalidOperationException: boom", text);
        Assert.Equal(2, writer.RecentLines().Count);

        _clock.Advance(TimeSpan.FromDays(1));
        writer.Write("INF", "X", "next day");
        Assert.EndsWith("genericpos-20261013.log", writer.CurrentFile);
        Assert.DoesNotContain("next day", ReadShared(path));
    }

    [Fact]
    public void A_full_file_continues_in_a_new_part()
    {
        using var writer = Writer(new DiagnosticsConfiguration { MaxFileBytes = 2_000 });
        for (var i = 0; i < 60; i++) writer.Write("INF", "Test", $"line {i} " + new string('x', 60));

        var files = Directory.GetFiles(Folders.Logs).Select(Path.GetFileName).Order().ToList();
        Assert.Contains("genericpos-20261012.log", files);
        Assert.Contains("genericpos-20261012-2.log", files);
        Assert.True(files.Count >= 3);
        Assert.All(Directory.GetFiles(Folders.Logs), f => Assert.True(new FileInfo(f).Length < 2_000 + 200, f));
    }

    [Fact]
    public void Old_files_go_after_14_days_the_oldest_go_over_the_total_and_other_files_are_never_touched()
    {
        Directory.CreateDirectory(Folders.Logs);
        foreach (var day in new[] { "20260920", "20260928", "20260929", "20261001", "20261011" })
            File.WriteAllText(Path.Combine(Folders.Logs, $"genericpos-{day}.log"), new string('x', 400));
        File.WriteAllText(Path.Combine(Folders.Logs, "notes.txt"), "keep");
        Directory.CreateDirectory(Folders.Crashes);
        File.WriteAllText(Path.Combine(Folders.Crashes, "crash-20260901-100000-000.txt"), "keep");

        using (var writer = Writer(new DiagnosticsConfiguration { RetentionDays = 14, MaxTotalBytes = 1_000 }))
            writer.Write("INF", "Test", "start");

        var left = Directory.GetFiles(Folders.Logs).Select(Path.GetFileName).Order().ToList();
        Assert.DoesNotContain("genericpos-20260920.log", left);   // older than 14 days (12 Oct counts as day 1: 29 Sep is the oldest kept)
        Assert.DoesNotContain("genericpos-20260928.log", left);
        Assert.DoesNotContain("genericpos-20260929.log", left);   // kept by age, removed as the oldest over the 1000-byte total
        Assert.Contains("genericpos-20261001.log", left);
        Assert.Contains("genericpos-20261011.log", left);
        Assert.Contains("genericpos-20261012.log", left);
        Assert.Contains("notes.txt", left);
        Assert.True(File.Exists(Path.Combine(Folders.Crashes, "crash-20260901-100000-000.txt")));
    }

    [Fact]
    public void A_log_that_cannot_be_written_never_breaks_the_caller()
    {
        File.WriteAllText(Path.Combine(_temp.Path, "Logs"), "a file where the folder should be");
        using var writer = Writer();

        writer.Write("INF", "Test", "still fine");

        Assert.Single(writer.RecentLines());
    }

    [Fact]
    public void The_provider_writes_information_and_up_and_the_framework_from_warnings_only()
    {
        using var writer = Writer();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new FileLoggerProvider(writer, new DiagnosticsConfiguration())));

        factory.CreateLogger("Client.Backup").LogDebug("debug detail");
        factory.CreateLogger("Client.Backup").LogInformation("information {Value}", 42);
        factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogInformation("SELECT secret FROM x");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogWarning("framework warning");
        factory.CreateLogger("System.Net.Http.HttpClient").LogInformation("request sent");

        var text = ReadShared(writer.CurrentFile!);
        Assert.DoesNotContain("debug detail", text);
        Assert.Contains("[INF] Client.Backup: information 42", text);
        Assert.DoesNotContain("SELECT", text);
        Assert.Contains("[WRN] Microsoft.Hosting.Lifetime: framework warning", text);
        Assert.DoesNotContain("request sent", text);
    }
}
