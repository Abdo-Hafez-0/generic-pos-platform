using System.IO.Compression;
using Client.Desktop.Resources;
using Client.Desktop.Screens;
using Client.Desktop.Screens.Support;
using Client.Desktop.Shell;
using Client.Diagnostics;
using Client.Diagnostics.Crash;
using Client.Diagnostics.Logging;
using Client.Diagnostics.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using Tests.Common.Security;

namespace UI.Tests;

/// <summary>
/// MISS-05: Administration > Support on the real diagnostics services (real files), the shell's "closed unexpectedly" notice, and what the
/// desktop does with a failure nobody handled.
/// </summary>
public sealed class SupportScreenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genericpos-ui-support-" + Guid.NewGuid().ToString("N"));
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        foreach (var provider in _providers)
        {
            provider.GetService<RollingLogWriter>()?.Dispose();
            provider.Dispose();
        }

        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ServiceProvider Services(params string[] allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = _root,
        }).Build();
        var services = new ServiceCollection().AddLogging().AddClientDiagnostics(configuration);
        services.AddSingleton<IAuthorizationService>(new ScriptedAuthorizationService(allowed));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _providers.Add(provider);
        return provider;
    }

    private static SupportViewModel Screen(ServiceProvider services)
        => new(new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));

    [Fact]
    public void The_screen_opens_with_support_export_which_no_license_state_locks()
    {
        var screen = Assert.Single(new DesktopScreens().GetScreens(), s => s.Id == "support.support");

        Assert.Equal(SupportCapabilities.Export, screen.RequiredCapability);
        Assert.Equal(ScreenGroups.Administration, screen.Group);
        Assert.Equal(SupportText.ScreenTitle, screen.Title());
        Assert.Equal(LicenseRequirement.None, SupportCapabilities.All.Single().License);
    }

    [Fact]
    public async Task The_screen_shows_the_logs_and_crash_reports_marks_them_seen_and_saves_the_support_file()
    {
        var services = Services(SupportCapabilities.Export);
        services.GetRequiredService<ILogger<SupportScreenTests>>().LogInformation("a sale was completed");
        var vm = Screen(services);

        await vm.OnNavigatedToAsync();
        Assert.True(vm.HasNoCrashes);
        Assert.Contains(Path.Combine(_root, "GenericPOS", "Logs"), vm.LogFolder);
        Assert.Contains("1", vm.LogFilesText);

        services.GetRequiredService<CrashReporter>().Report(new InvalidOperationException("The till froze."), CrashOrigin.Screen);
        Assert.Single(services.GetRequiredService<ICrashNoticeSource>().UnseenCrashes());
        await vm.OnNavigatedToAsync();
        var row = Assert.Single(vm.CrashReports);
        Assert.Contains("InvalidOperationException: The till froze.", row.Summary);
        Assert.True(vm.HasCrashes);
        Assert.Empty(services.GetRequiredService<ICrashNoticeSource>().UnseenCrashes());   // opening the screen counts as seen

        var target = Path.Combine(_root, "Desktop", "support.zip");
        vm.SaveCommand.Execute(target);
        for (var i = 0; i < 1000 && (vm.IsBusy || vm.StatusMessage is null); i++) await Task.Delay(10);
        Assert.Null(vm.ErrorMessage);
        Assert.Contains("support.zip", vm.StatusMessage);
        using var zip = ZipFile.OpenRead(target);
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("crash/", StringComparison.Ordinal));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("logs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_support_export_nothing_is_shown()
    {
        var services = Services();
        services.GetRequiredService<CrashReporter>().Report(new InvalidOperationException("x"), CrashOrigin.Screen);
        var vm = Screen(services);

        await vm.OnNavigatedToAsync();

        Assert.NotNull(vm.ErrorMessage);
        Assert.Empty(vm.CrashReports);
        Assert.Equal(string.Empty, vm.LogFolder);
        Assert.Single(services.GetRequiredService<ICrashNoticeSource>().UnseenCrashes());   // still unseen
    }

    [Fact]
    public async Task The_shell_tells_those_who_may_save_a_support_file_until_the_reports_are_seen()
    {
        var services = Services(SupportCapabilities.Export);
        var support = services.GetRequiredService<SupportService>();
        services.GetRequiredService<CrashReporter>().Report(new InvalidOperationException("x"), CrashOrigin.Screen);

        var permissions = new FakePermissions();
        var shellServices = new ServiceCollection();
        shellServices.AddScoped<IPermissionProvider>(_ => permissions);
        var runner = new UiActionRunner(shellServices.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        var catalog = new CapabilityCatalog([new CapabilityProvider(new CapabilityDescriptor("pos.sale.create", "pos", "Sell", "Sell"))]);
        var navigation = new NavigationBuilder([new ScreenProvider(Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"))], catalog, new FakeLicensing("pos"));
        var shell = new ShellViewModel(runner, navigation, new FakeCurrentUser(), new FakeScreenFactory(), crashes: support);

        permissions.Held.Add("pos.sale.create");
        await shell.RefreshAsync();
        Assert.Equal(string.Empty, shell.SupportNoticeText);   // a cashier is not told

        permissions.Held.Add(SupportCapabilities.Export);
        await shell.RefreshAsync();
        Assert.StartsWith(ShellText.CrashNotice[..20], shell.SupportNoticeText);

        support.MarkCrashesSeen();   // someone opened the Support screen
        for (var i = 0; i < 1000 && shell.SupportNoticeText.Length > 0; i++) await Task.Delay(10);
        Assert.Equal(string.Empty, shell.SupportNoticeText);

        await shell.ResetAsync();
        Assert.Equal(string.Empty, shell.SupportNoticeText);
    }

    [Fact]
    public void A_failure_on_the_screen_is_reported_the_person_told_and_the_application_closed_once()
    {
        var services = Services(SupportCapabilities.Export);
        var messages = new List<string>();
        var exits = new List<int>();
        var handling = new CrashHandling(messages.Add, exits.Add);
        handling.Use(services);

        Assert.True(handling.OnScreenFailure(new InvalidOperationException("first")));
        Assert.True(handling.OnScreenFailure(new InvalidOperationException("second, while closing")));

        Assert.Equal([ShellText.CrashMessage], messages);
        Assert.Equal([1], exits);
        var report = Assert.Single(services.GetRequiredService<CrashReporter>().List());
        Assert.Contains("first", report.Summary);

        handling.OnBackgroundFailure(new InvalidOperationException("background"));
        handling.OnStartupFailure(new InvalidOperationException("startup"));
        Assert.Equal(3, services.GetRequiredService<CrashReporter>().List().Count);

        handling.OnUnobservedTask(new AggregateException(new TimeoutException("nobody waited")));
        var log = services.GetRequiredService<RollingLogWriter>().RecentLines();
        Assert.Contains(log, l => l.Contains("[ERR] Client.Desktop.Shell.CrashHandling: A background task failed", StringComparison.Ordinal));
    }
}
