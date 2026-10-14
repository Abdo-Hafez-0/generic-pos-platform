using System.IO.Compression;
using Client.Diagnostics;
using Client.Diagnostics.Crash;
using Client.Diagnostics.Logging;
using Client.Diagnostics.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Tests.Common.Security;

namespace Diagnostics.Tests;

public sealed class RecordingEvents : IBusinessEventSink
{
    public List<BusinessEvent> Events { get; } = [];

    public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(businessEvent);
        return Task.CompletedTask;
    }
}

/// <summary>MISS-05: crash reports, the crash notice, the support file and the composition.</summary>
public sealed class CrashAndSupportTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly UtcClock _clock = new(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingEvents _events = new();
    private readonly RollingLogWriter _writer;
    private readonly CrashReporter _crashes;

    public CrashAndSupportTests()
    {
        _writer = new RollingLogWriter(new DiagnosticsFolders(Path.Combine(_temp.Path, "Logs")), new DiagnosticsConfiguration(), _clock);
        _crashes = new CrashReporter(_writer, _clock);
    }

    public void Dispose()
    {
        _writer.Dispose();
        _temp.Dispose();
    }

    private SupportService Support() => new(_writer, _crashes, _clock, NullLogger<SupportService>.Instance, events: _events);

    private static Exception Thrown()
    {
        try
        {
            try
            {
                throw new IOException("disk said no for gpb_supersecrettoken123");
            }
            catch (IOException inner)
            {
                throw new InvalidOperationException("Checkout failed.", inner);
            }
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [Fact]
    public void A_crash_report_says_when_where_what_with_versions_and_the_last_log_lines_and_no_secret()
    {
        _writer.Write("INF", "POS", "Sale started");
        _writer.Write("INF", "POS", "Scanned 4006381333931");

        var path = _crashes.Report(Thrown(), CrashOrigin.Screen);

        Assert.Equal(Path.Combine(_temp.Path, "Logs", "crash", "crash-20261012-090000-000.txt"), path);
        var text = File.ReadAllText(path!);
        Assert.Contains("GenericPOS crash report", text);
        Assert.Contains("Time:        2026-10-12 09:00:00 +00:00", text);
        Assert.Contains("Where:       Screen", text);
        Assert.Contains("Summary:     IOException: disk said no for gpb_***", text);
        Assert.Contains("Application: ", text);
        Assert.Contains("Windows:     ", text);
        Assert.Contains("System.InvalidOperationException: Checkout failed.", text);
        Assert.Contains("Scanned 4006381333931", text);
        Assert.DoesNotContain("supersecret", text);

        // the failure is in the log too
        using var log = new FileStream(_writer.CurrentFile!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Contains("[CRT] Client.Diagnostics.Crash: Unexpected failure (Screen)", new StreamReader(log).ReadToEnd());
    }

    [Fact]
    public void Reports_are_listed_newest_first_and_stay_unseen_until_the_support_screen_is_opened()
    {
        _crashes.Report(new InvalidOperationException("first"), CrashOrigin.Background);
        _clock.Advance(TimeSpan.FromMinutes(5));
        _crashes.Report(new InvalidOperationException("second"), CrashOrigin.Screen);

        var support = Support();
        var changed = 0;
        support.Changed += (_, _) => changed++;
        var listed = _crashes.List();
        Assert.Equal(["InvalidOperationException: second", "InvalidOperationException: first"], listed.Select(r => r.Summary));
        Assert.Equal(2, support.UnseenCrashes().Count);

        _clock.Advance(TimeSpan.FromMinutes(1));
        support.MarkCrashesSeen();
        Assert.Empty(support.UnseenCrashes());
        Assert.Equal(1, changed);

        _clock.Advance(TimeSpan.FromMinutes(1));
        _crashes.Report(new InvalidOperationException("third"), CrashOrigin.Screen);
        Assert.Single(support.UnseenCrashes());
    }

    [Fact]
    public void A_report_that_cannot_be_written_never_throws()
    {
        Directory.CreateDirectory(Path.Combine(_temp.Path, "Logs"));
        File.WriteAllText(Path.Combine(_temp.Path, "Logs", "crash"), "a file where the folder should be");

        Assert.Null(_crashes.Report(new InvalidOperationException("x"), CrashOrigin.Startup));
        Assert.Empty(_crashes.List());
    }

    [Fact]
    public async Task The_support_file_holds_the_logs_the_crash_reports_and_about_and_never_anything_else()
    {
        _writer.Write("INF", "POS", "Sale completed");
        _crashes.Report(Thrown(), CrashOrigin.Screen);
        File.WriteAllText(Path.Combine(_temp.Path, "Logs", "genericpos.db"), "SQLite format 3");   // must never be taken
        File.WriteAllText(Path.Combine(_temp.Path, "Logs", "keyring.bin"), "secret");
        var support = Support();

        var overview = support.GetOverview();
        Assert.Equal(1, overview.LogFileCount);
        Assert.Single(overview.CrashReports);

        var target = Path.Combine(_temp.Path, "out", "support");
        var saved = await support.SaveSupportFileAsync(target);

        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
        Assert.Equal(target + ".zip", saved.Value.Path);
        Assert.Equal((1, 1), (saved.Value.LogFiles, saved.Value.CrashReports));
        using (var zip = ZipFile.OpenRead(saved.Value.Path))
        {
            Assert.Equal(["about.txt", "crash/crash-20261012-090000-000.txt", "logs/genericpos-20261012.log"], zip.Entries.Select(e => e.FullName).Order());
            using var about = new StreamReader(zip.GetEntry("about.txt")!.Open());
            Assert.Contains("GenericPOS support file", about.ReadToEnd());
            using var log = new StreamReader(zip.GetEntry("logs/genericpos-20261012.log")!.Open());
            Assert.Contains("Sale completed", log.ReadToEnd());
        }

        Assert.False(File.Exists(target + ".zip.part"));
        Assert.Equal("support.file-saved", Assert.Single(_events.Events).Action);
    }

    [Fact]
    public async Task The_support_file_needs_a_real_place_outside_the_log_folder()
    {
        var support = Support();

        Assert.Equal(SupportErrorCodes.InvalidTarget, (await support.SaveSupportFileAsync(null)).Error.Code);
        Assert.Equal(SupportErrorCodes.InvalidTarget, (await support.SaveSupportFileAsync("relative.zip")).Error.Code);
        Assert.Equal(SupportErrorCodes.InvalidTarget, (await support.SaveSupportFileAsync(Path.Combine(_temp.Path, "Logs", "s.zip"))).Error.Code);

        File.WriteAllText(Path.Combine(_temp.Path, "blocked"), "a file where the folder should be");
        var failed = await support.SaveSupportFileAsync(Path.Combine(_temp.Path, "blocked", "s.zip"));
        Assert.Equal(SupportErrorCodes.Failed, failed.Error.Code);
        Assert.Empty(_events.Events);
    }

    [Fact]
    public async Task Every_support_action_needs_support_export_first()
    {
        var support = Support();
        var none = new ScriptedAuthorizationService();
        var target = Path.Combine(_temp.Path, "s.zip");

        Assert.True((await new GetSupportOverviewQueryHandler(support, none).HandleAsync(new GetSupportOverviewQuery())).IsFailure);
        Assert.True((await new MarkCrashReportsSeenCommandHandler(support, none).HandleAsync(new MarkCrashReportsSeenCommand())).IsFailure);
        Assert.True((await new SaveSupportFileCommandHandler(support, none).HandleAsync(new SaveSupportFileCommand(target))).IsFailure);
        Assert.Equal(Enumerable.Repeat(SupportCapabilities.Export, 3), none.Asked);
        Assert.False(File.Exists(target));

        var capability = Assert.Single(SupportCapabilities.All);
        Assert.True(capability.IsSensitive);
        Assert.Equal(LicenseRequirement.None, capability.License);
    }

    [Fact]
    public void The_module_puts_the_logs_next_to_the_data_and_every_logger_writes_there()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = _temp.Path,
        }).Build();
        var services = new ServiceCollection().AddLogging().AddClientDiagnostics(configuration);
        services.AddSingleton<IAuthorizationService>(new AllowAllAuthorizationService());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        provider.GetRequiredService<ILogger<CrashAndSupportTests>>().LogInformation("composed");

        var folders = provider.GetRequiredService<DiagnosticsFolders>();
        Assert.Equal(Path.Combine(_temp.Path, "GenericPOS", "Logs"), folders.Logs);
        var file = Assert.Single(Directory.GetFiles(folders.Logs));
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.Contains("composed", new StreamReader(stream).ReadToEnd());
        Assert.Contains(provider.GetServices<ICapabilityProvider>(), p => p is SupportCapabilityProvider);
        Assert.Same(provider.GetRequiredService<SupportService>(), provider.GetRequiredService<ICrashNoticeSource>());
        provider.GetRequiredService<RollingLogWriter>().Dispose();
    }
}
