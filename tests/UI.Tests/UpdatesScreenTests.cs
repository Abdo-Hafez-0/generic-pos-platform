using System.Globalization;
using Client.Desktop.Resources;
using Client.Desktop.Screens;
using Client.Desktop.Screens.Updates;
using Client.Desktop.Shell;
using Client.Updater.Application;
using Client.Updater.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using Tests.Common.Security;
using Updates.Contracts;

namespace UI.Tests;

/// <summary>
/// PKG-01b: Administration > Updates (the versions in use and the active ones, check and install, install from a file, go back, restart
/// now) through the real handlers (updates.manage) over a fake updater; and the shell's "an update is ready - Restart now" notice.
/// </summary>
public sealed class UpdatesScreenTests
{
    internal sealed class FakeAdoption : IUpdateAdoption
    {
        public LauncherHandOff Launcher { get; set; } = new(@"C:\GenericPOS\GenericPOS.exe", new Dictionary<string, string>());
        public List<UpdatePart> Current { get; set; } = [];
        public int Reads { get; private set; }

        public IReadOnlyList<UpdatePart> Parts()
        {
            Reads++;
            return Current;
        }

        public event EventHandler? Changed;
        public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class FakeUpdates(FakeAdoption adoption) : IUpdateService
    {
        public List<UpdateInfo> Offered { get; } = [];
        public string? Unavailable { get; set; }
        public List<string> Installed { get; } = [];
        public List<string> RolledBack { get; } = [];

        public Task<Result<IReadOnlyList<UpdateInfo>>> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Unavailable is { } why
                ? Result.Failure<IReadOnlyList<UpdateInfo>>(Error.Failure(UpdateErrorCodes.ServerUnavailable, why))
                : Result.Success<IReadOnlyList<UpdateInfo>>(Offered.ToList()));

        public Task<Result<string>> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success($@"C:\Updates\downloads\{update.TargetId}-{update.Version}.gpkg"));

        public Task<Result<UpdateJournal>> InstallAsync(string packagePath, CancellationToken cancellationToken = default)
        {
            Installed.Add(packagePath);
            var (target, version) = packagePath.Contains("pos-1.1.0") || packagePath.EndsWith("from-vendor.gpkg") ? ("pos", "1.1.0") : ("core", "1.2.0");
            adoption.Current = adoption.Current.Select(p => p.TargetId == target
                ? p with { Active = version, State = adoption.Launcher.StartedByLauncher ? UpdatePartState.WaitsForRestart : UpdatePartState.WaitsForLauncher, CanGoBack = true }
                : p).ToList();
            return Task.FromResult(Result.Success(new UpdateJournal { TargetId = target, Version = version, State = UpdateState.Activated }));
        }

        public Task<IReadOnlyList<UpdateJournal>> RecoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<UpdateJournal>>([]);
        public Task<Result> ConfirmHealthyAsync(string targetId, CancellationToken cancellationToken = default) => Task.FromResult(Result.Success());

        public Task<Result> RollbackAsync(string targetId, bool restoreData = false, CancellationToken cancellationToken = default)
        {
            RolledBack.Add(targetId);
            adoption.Current = adoption.Current.Select(p => p.TargetId == targetId ? p with { Active = p.Running ?? "-", State = UpdatePartState.InUse, CanGoBack = false } : p).ToList();
            return Task.FromResult(Result.Success());
        }
    }

    internal sealed class Restarts : IApplicationRestarter
    {
        public int Count { get; private set; }
        public void Restart() => Count++;
    }

    private readonly FakeAdoption _adoption = new()
    {
        Current =
        [
            new UpdatePart("core", string.Empty, "1.0.0", "1.0.0", UpdatePartState.InUse, null, false),
            new UpdatePart("pos", "Point of Sale", "1.0.0", "1.0.0", UpdatePartState.InUse, null, false),
            new UpdatePart("sales", "Sales", "1.0.0", "1.0.1", UpdatePartState.NotStarted, "it was started 3 times without starting properly", true),
        ]
    };
    private readonly Restarts _restarts = new();
    private readonly FakeUpdates _updates;

    public UpdatesScreenTests() => _updates = new FakeUpdates(_adoption);

    private UpdatesViewModel Screen(params string[] allowed)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAuthorizationService>(new ScriptedAuthorizationService(allowed));
        services.AddSingleton<IUpdateService>(_updates);
        services.AddSingleton<IUpdateAdoption>(_adoption);
        services.AddTransient<GetUpdateOverviewQueryHandler>();
        services.AddTransient<CheckForUpdatesQueryHandler>();
        services.AddTransient<DownloadUpdateCommandHandler>();
        services.AddTransient<InstallUpdateCommandHandler>();
        services.AddTransient<RollbackUpdateCommandHandler>();
        var runner = new UiActionRunner(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }).GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        return new UpdatesViewModel(runner, _restarts);
    }

    private static async Task Run(UpdatesViewModel vm, System.Windows.Input.ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        for (var i = 0; i < 200 && vm.IsBusy; i++) await Task.Delay(5);
        Assert.False(vm.IsBusy);
    }

    private static UpdateInfo Offer(string target, string version, long size)
        => new(Guid.NewGuid(), target == "core" ? PackageType.Core : PackageType.Module, target, version, size, "00", new SignedPackageManifest("e30=", "KEY", "ES256", "c2ln"));

    [Fact]
    public void The_screen_opens_with_updates_manage_which_no_license_state_locks()
    {
        var screen = Assert.Single(new DesktopScreens().GetScreens(), s => s.Id == "updates.updates");

        Assert.Equal(UpdatesCapabilities.Manage, screen.RequiredCapability);
        Assert.Equal(ScreenGroups.Administration, screen.Group);
        Assert.Equal(UpdatesText.ScreenTitle, screen.Title());
        Assert.Equal(LicenseRequirement.None, UpdatesCapabilities.All.Single().License);
    }

    [Fact]
    public async Task The_versions_are_shown_with_what_happens_to_them()
    {
        var vm = Screen(UpdatesCapabilities.Manage);

        await vm.OnNavigatedToAsync();

        Assert.Equal([UpdatesText.Application, "Point of Sale", "Sales"], vm.Parts.Select(p => p.NameText));
        Assert.Equal([UpdatesText.StateInUse, UpdatesText.StateInUse], vm.Parts.Take(2).Select(p => p.StateText));
        Assert.Contains("it was started 3 times without starting properly", vm.Parts[2].StateText);
        Assert.Contains("1.0.1", vm.Parts[2].ActiveText);
        Assert.True(vm.StartedByLauncher);
        Assert.False(vm.RestartWouldHelp);
        Assert.False(vm.GoBackCommand.CanExecute(null));
        vm.SelectedPart = vm.Parts[1];
        Assert.False(vm.GoBackCommand.CanExecute(null));   // nothing installed for it
        vm.SelectedPart = vm.Parts[2];
        Assert.True(vm.GoBackCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_found_update_is_downloaded_and_installed_and_a_restart_is_offered()
    {
        _updates.Offered.Add(Offer("pos", "1.1.0", 3 * 1024 * 1024));
        var vm = Screen(UpdatesCapabilities.Manage);
        await vm.OnNavigatedToAsync();

        await Run(vm, vm.CheckCommand);
        var found = Assert.Single(vm.Found);
        Assert.Equal("Point of Sale", found.NameText);
        Assert.Contains("1.1.0", found.VersionText);
        Assert.Contains("3.0 MB", found.SizeText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, UpdatesText.FoundCount, 1), vm.StatusMessage);

        await Run(vm, vm.InstallFoundCommand);

        Assert.Equal([@"C:\Updates\downloads\pos-1.1.0.gpkg"], _updates.Installed);
        Assert.Empty(vm.Found);
        Assert.Contains("Point of Sale", vm.StatusMessage);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, UpdatesText.Installed, "Point of Sale", "‎1.1.0‎"), vm.StatusMessage);
        Assert.Equal(UpdatesText.StateAfterRestart, vm.Parts[1].StateText);
        Assert.True(vm.RestartWouldHelp);

        await Run(vm, vm.RestartCommand);
        Assert.Equal(1, _restarts.Count);
    }

    [Fact]
    public async Task An_update_file_from_the_vendor_is_installed_and_without_the_launcher_the_screen_says_when_it_is_used()
    {
        _adoption.Launcher = LauncherHandOff.None;
        var vm = Screen(UpdatesCapabilities.Manage);
        await vm.OnNavigatedToAsync();
        Assert.True(vm.NotStartedByLauncher);

        await Run(vm, vm.InstallFromFileCommand, @"E:\from-vendor.gpkg");

        Assert.Equal([@"E:\from-vendor.gpkg"], _updates.Installed);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, UpdatesText.InstalledNeedsLauncher, "Point of Sale", "‎1.1.0‎"), vm.StatusMessage);
        Assert.Equal(UpdatesText.StateNeedsLauncher, vm.Parts[1].StateText);
        Assert.False(vm.RestartWouldHelp);   // a restart without the launcher would not use it
    }

    [Fact]
    public async Task Going_back_rolls_the_selected_part_back()
    {
        var vm = Screen(UpdatesCapabilities.Manage);
        await vm.OnNavigatedToAsync();
        vm.SelectedPart = vm.Parts[2];

        await Run(vm, vm.GoBackCommand);

        Assert.Equal(["sales"], _updates.RolledBack);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, UpdatesText.WentBack, "Sales"), vm.StatusMessage);
        Assert.Equal(UpdatesText.StateInUse, vm.Parts[2].StateText);
    }

    [Fact]
    public async Task Nothing_found_or_no_server_is_said_plainly()
    {
        var vm = Screen(UpdatesCapabilities.Manage);
        await vm.OnNavigatedToAsync();

        await Run(vm, vm.CheckCommand);
        Assert.True(vm.HasNoneFound);
        Assert.Equal(UpdatesText.NoneFound, vm.StatusMessage);

        _updates.Unavailable = "The update source could not be reached: no internet.";
        await Run(vm, vm.CheckCommand);
        Assert.Equal("The update source could not be reached: no internet.", vm.ErrorMessage);
    }

    [Fact]
    public async Task Without_updates_manage_nothing_is_shown_checked_or_installed()
    {
        _updates.Offered.Add(Offer("pos", "1.1.0", 1000));
        var vm = Screen("pos.sale.create");

        await vm.OnNavigatedToAsync();
        Assert.Empty(vm.Parts);
        Assert.NotNull(vm.ErrorMessage);

        await Run(vm, vm.CheckCommand);
        Assert.Empty(vm.Found);
        await Run(vm, vm.InstallFromFileCommand, @"E:\from-vendor.gpkg");
        Assert.Empty(_updates.Installed);
    }
}

/// <summary>PKG-01b: the shell offers "Restart now" when an update waits for a restart, and tells the people who manage updates about a problem.</summary>
public sealed class ShellUpdateNoticeTests
{
    private readonly FakePermissions _permissions = new();
    private readonly UpdatesScreenTests.FakeAdoption _adoption = new();
    private readonly UpdatesScreenTests.Restarts _restarts = new();
    private readonly ShellViewModel _shell;

    public ShellUpdateNoticeTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPermissionProvider>(_ => _permissions);
        var runner = new UiActionRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        var catalog = new CapabilityCatalog([new CapabilityProvider(new CapabilityDescriptor("pos.sale.create", "pos", "Sell", "Sell"))]);
        var navigation = new NavigationBuilder([new ScreenProvider(Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"))], catalog, new FakeLicensing("pos"));
        _shell = new ShellViewModel(runner, navigation, new FakeCurrentUser(), new FakeScreenFactory(), updates: _adoption, restarter: _restarts);
        _permissions.Held.Add("pos.sale.create");
    }

    private static UpdatePart Pos(UpdatePartState state) => new("pos", "Point of Sale", "1.0.0", "1.1.0", state, state == UpdatePartState.NotStarted ? "why" : null, true);

    [Fact]
    public async Task An_update_waiting_for_a_restart_is_announced_to_everyone_with_Restart_now()
    {
        _adoption.Current = [Pos(UpdatePartState.WaitsForRestart)];

        await _shell.RefreshAsync();

        Assert.Equal(ShellText.UpdateReady, _shell.UpdateNoticeText);
        Assert.True(_shell.CanRestartForUpdate);
        _shell.RestartForUpdateCommand.Execute(null);
        Assert.Equal(1, _restarts.Count);
    }

    [Fact]
    public async Task Problems_and_a_missing_launcher_are_told_only_to_the_people_who_manage_updates()
    {
        _adoption.Current = [Pos(UpdatePartState.NotStarted)];
        await _shell.RefreshAsync();
        Assert.Equal(string.Empty, _shell.UpdateNoticeText);   // a cashier
        Assert.False(_shell.CanRestartForUpdate);

        _permissions.Held.Add(UpdatesCapabilities.Manage);
        await _shell.RefreshAsync();
        Assert.Equal(ShellText.UpdateProblem, _shell.UpdateNoticeText);

        _adoption.Launcher = LauncherHandOff.None;
        _adoption.Current = [Pos(UpdatePartState.WaitsForLauncher)];
        await _shell.RefreshAsync();
        Assert.Equal(ShellText.UpdateNeedsLauncher, _shell.UpdateNoticeText);
        Assert.False(_shell.CanRestartForUpdate);
    }

    [Fact]
    public async Task The_notice_follows_an_install_without_reopening_anything_and_sign_out_clears_it()
    {
        await _shell.RefreshAsync();
        Assert.Equal(string.Empty, _shell.UpdateNoticeText);

        _adoption.Current = [Pos(UpdatePartState.WaitsForRestart)];
        _adoption.NotifyChanged();   // no synchronization context in the test: updated at once
        await Task.Delay(50);
        Assert.Equal(ShellText.UpdateReady, _shell.UpdateNoticeText);

        await _shell.ResetAsync();
        Assert.Equal(string.Empty, _shell.UpdateNoticeText);
        Assert.False(_shell.CanRestartForUpdate);
    }
}
