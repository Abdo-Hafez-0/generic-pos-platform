using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for diagnostics (MISS-05). ARCH-DIA-001 .. ARCH-DIA-004.
///
/// Shape being protected:
///   Client.Diagnostics : the log file next to the database (masked), crash reports, the support file. Everything stays on this PC: no
///                        HTTP and no telemetry ("logs remain local"); no EF, no WPF, no business module. The support file takes log
///                        files and crash reports only - never the database, a backup or a key file.
/// </summary>
public sealed class DiagnosticsBoundaryTests
{
    private static IEnumerable<string> Refs(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), "src", "Client", "Client.Diagnostics", .. parts]));

    [Fact(DisplayName = "ARCH-DIA-001: Client.Diagnostics keeps everything local: no HTTP or network, no EF Core, WPF, ASP.NET or business module")]
    public void DiagnosticsStayLocal()
    {
        foreach (var forbidden in new[] { "System.Net", "Microsoft.EntityFrameworkCore", "System.Windows", "Microsoft.AspNetCore" })
        {
            var result = Types.InAssembly(Assemblies.ClientDiagnostics).Should().NotHaveDependencyOn(forbidden).GetResult();
            Assert.True(result.IsSuccessful, $"Client.Diagnostics depends on {forbidden}: " + string.Join(", ", result.FailingTypeNames ?? []));
        }

        var refs = Refs(Assemblies.ClientDiagnostics).ToList();
        Assert.DoesNotContain(refs, r => r.StartsWith("System.Net", StringComparison.Ordinal) || r.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                                         || r is "PresentationFramework" or "PresentationCore" or "WindowsBase");
        string[] businessModules = ["Catalog", "Inventory", "Sales", "POS", "Customers", "Suppliers", "Purchasing", "Pricing", "Payments", "Users", "Audit", "CashManagement", "Reporting", "CloudBackup"];
        Assert.DoesNotContain(refs, r => businessModules.Any(m => r.StartsWith(m + ".", StringComparison.Ordinal)));
        Assert.DoesNotContain(refs, r => r is "Client.Backup" or "Client.Licensing" or "Client.Security" or "Client.Updater");
    }

    [Fact(DisplayName = "ARCH-DIA-002: Everything written to the log file or a crash report is masked, and the support file takes log files and crash reports only")]
    public void WritesAreMaskedAndTheSupportFileTakesOnlyLogs()
    {
        var writer = Source("Logging", "RollingLogWriter.cs");
        Assert.Contains("LogRedactor.Redact(text.ToString())", writer);

        var crash = Source("Crash", "CrashReporter.cs");
        Assert.Contains("File.WriteAllText(path, LogRedactor.Redact(Compose(exception, origin, now))", crash);

        var support = Source("Support", "SupportService.cs");
        Assert.Contains("RollingLogWriter.LogFiles(writer.Folders.Logs)", support);
        Assert.Contains("crashes.List()", support);
        foreach (var forbidden in new[] { "EnumerateFiles", "GetFiles", "*.db", ".bin\"", "ResolveDatabasePath", "Backup" })
            Assert.DoesNotContain(forbidden, support);
    }

    [Fact(DisplayName = "ARCH-DIA-003: No log message template anywhere names a secret (passwords, tokens, keys, recovery and activation codes)")]
    public void NoLogTemplateNamesASecret()
    {
        var secretPlaceholder = new Regex(@"Log(Information|Warning|Error|Debug|Critical|Trace)?\([^;]*\{(Password|NewPassword|TemporaryPassword|Token|AccessKey|RecoveryCode|ActivationKey|Secret|PrivateKey|RestoreKey|BackupToken|AdminKey)\}",
            RegexOptions.Singleline);
        var files = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        foreach (var file in files)
            Assert.False(secretPlaceholder.IsMatch(File.ReadAllText(file)), $"{file} logs a value named like a secret: {secretPlaceholder.Match(File.ReadAllText(file)).Value}");
    }

    [Fact(DisplayName = "ARCH-DIA-004: The desktop registers diagnostics and hands every unhandled failure to the crash handling")]
    public void DesktopReportsUnhandledFailures()
    {
        var composition = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "DesktopComposition.cs"));
        Assert.Contains("new DiagnosticsHostingModule()", composition);

        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "App.xaml.cs"));
        Assert.Contains("_crashes.Attach(Dispatcher);", app);
        Assert.Contains("_crashes.OnStartupFailure(ex);", app);
        Assert.True(app.IndexOf("_crashes.Attach(Dispatcher);", StringComparison.Ordinal) < app.IndexOf("ApplicationHostBuilder.Create()", StringComparison.Ordinal),
            "Crash handling must be attached before anything else can fail.");

        var handling = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "Shell", "CrashHandling.cs"));
        foreach (var hook in new[] { "dispatcher.UnhandledException", "AppDomain.CurrentDomain.UnhandledException", "TaskScheduler.UnobservedTaskException" })
            Assert.Contains(hook, handling);
    }
}
