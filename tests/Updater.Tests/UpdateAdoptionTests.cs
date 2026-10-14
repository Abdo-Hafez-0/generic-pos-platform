using Client.Updater.Application;
using Client.Updater.Domain;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Tests.Common.Security;

namespace Updater.Tests;

/// <summary>
/// PKG-01b: the versions this process runs against the versions the updater made active - what the Updates screen shows and what makes
/// the shell offer "Restart now" - and what the launcher handed over (its path, what it did not start and why).
/// </summary>
public sealed class UpdateAdoptionTests : IDisposable
{
    private readonly UpdateWorld _w = new();
    private readonly ModuleRegistry _registry = new();
    private readonly Dictionary<string, ModuleVersion> _running = new() { ["core"] = new(1, 0, 0) };

    public UpdateAdoptionTests() => _registry.Register(new InstallRecoveryTests.FakeRuntimeModule("catalog", "1.0.0"));

    public void Dispose() => _w.Dispose();

    private sealed class Running(Dictionary<string, ModuleVersion> core, IModuleRegistry registry) : IRunningVersions
    {
        public ModuleVersion? Of(string targetId)
            => core.GetValueOrDefault(targetId) ?? registry.GetAll().FirstOrDefault(m => m.Manifest.ModuleId.Value == targetId)?.Manifest.Version;
    }

    private UpdateAdoption Adoption(LauncherHandOff? launcher = null)
        => new(_w.Store, new Running(_running, _registry), _registry, launcher ?? Launched(), new ModuleVersion(1, 0, 0));

    private static LauncherHandOff Launched(string heldBack = "")
        => LauncherHandOff.FromEnvironment(name => name switch
        {
            LauncherHandOff.LauncherPathVariable => @"C:\Program Files\GenericPOS\GenericPOS.exe",
            LauncherHandOff.HeldBackVariable => heldBack,
            _ => null
        });

    private async Task<UpdateJournal> InstallAsync(string module, string version)
    {
        var installed = await _w.Service.InstallAsync(_w.PublishModule(module, version));
        Assert.True(installed.IsSuccess, installed.IsFailure ? installed.Error.ToString() : null);
        return installed.Value;
    }

    private UpdatePart Part(UpdateAdoption adoption, string target) => adoption.Parts().Single(p => p.TargetId == target);

    [Fact]
    public void With_no_update_everything_is_in_use_at_its_built_in_version()
    {
        var parts = Adoption().Parts();

        Assert.Equal(["core", "catalog"], parts.Select(p => p.TargetId));
        Assert.All(parts, p => Assert.Equal((UpdatePartState.InUse, "1.0.0", "1.0.0", false), (p.State, p.Running, p.Active, p.CanGoBack)));
        Assert.Equal(string.Empty, parts[0].Name);   // the screen names the core "Application"
        Assert.Equal("catalog", parts[1].Name);
    }

    [Fact]
    public async Task An_installed_update_waits_for_a_restart_when_the_launcher_started_the_application_and_for_the_launcher_otherwise()
    {
        await InstallAsync("catalog", "1.3.0");

        var launched = Part(Adoption(), "catalog");
        Assert.Equal((UpdatePartState.WaitsForRestart, "1.0.0", "1.3.0", true), (launched.State, launched.Running, launched.Active, launched.CanGoBack));
        Assert.True(new UpdateOverview(Adoption().Parts(), StartedByLauncher: true).RestartWouldHelp);

        var direct = Part(Adoption(LauncherHandOff.None), "catalog");
        Assert.Equal(UpdatePartState.WaitsForLauncher, direct.State);
        Assert.False(new UpdateOverview(Adoption(LauncherHandOff.None).Parts(), StartedByLauncher: false).RestartWouldHelp);
    }

    [Fact]
    public async Task Once_the_new_version_runs_it_is_in_use_and_can_still_be_rolled_back()
    {
        await InstallAsync("catalog", "1.3.0");
        var registry = new ModuleRegistry();
        registry.Register(new InstallRecoveryTests.FakeRuntimeModule("catalog", "1.3.0"));   // started from the assembled folder

        var part = new UpdateAdoption(_w.Store, new Running(_running, registry), registry, Launched(), new ModuleVersion(1, 0, 0)).Parts().Single(p => p.TargetId == "catalog");

        Assert.Equal((UpdatePartState.InUse, "1.3.0", true), (part.State, part.Running, part.CanGoBack));
    }

    [Fact]
    public async Task Going_back_while_the_update_runs_waits_for_a_restart_into_the_version_installed_with_the_application()
    {
        // found by the real run: POS 1.0.1 runs from the assembled folder, is rolled back (no update active any more), and the screen
        // said "in use" - the installed POS is not the one running
        await InstallAsync("catalog", "1.3.0");
        var registry = new ModuleRegistry();
        registry.Register(new InstallRecoveryTests.FakeRuntimeModule("catalog", "1.3.0"));
        var laidOver = LauncherHandOff.FromEnvironment(name => name switch
        {
            LauncherHandOff.LauncherPathVariable => @"C:\GenericPOS\GenericPOS.exe",
            LauncherHandOff.ModulesVariable => "catalog@1.3.0",
            _ => null
        });
        Assert.True(laidOver.RunsUpdated("catalog"));
        Assert.True((await _w.Service.RollbackAsync("catalog")).IsSuccess);

        var part = new UpdateAdoption(_w.Store, new Running(_running, registry), registry, laidOver, new ModuleVersion(1, 0, 0)).Parts().Single(p => p.TargetId == "catalog");

        Assert.Equal(("1.3.0", null, UpdatePartState.WaitsForRestart), (part.Running, part.Active, part.State));
    }

    [Fact]
    public async Task A_version_the_launcher_did_not_start_says_why()
    {
        await InstallAsync("catalog", "1.3.0");

        var part = Part(Adoption(Launched("catalog@1.3.0: it adds Catalog.Reports.dll, which the installed application does not have\ncore@1.1.0: other")), "catalog");

        Assert.Equal(UpdatePartState.NotStarted, part.State);
        Assert.Equal("it adds Catalog.Reports.dll, which the installed application does not have", part.Reason);
    }

    [Fact]
    public async Task An_update_that_needs_a_decision_says_so_with_the_updater_s_last_words()
    {
        var journal = await InstallAsync("catalog", "1.3.0");
        journal.State = UpdateState.RecoveryRequired;
        journal.History.Add(new UpdateStateEntry(UpdateState.RecoveryRequired, _w.Clock.GetUtcNow(), "The database was migrated and the old binaries may not be schema-compatible."));
        _w.Store.SaveJournal(journal);

        var part = Part(Adoption(), "catalog");

        Assert.Equal(UpdatePartState.NeedsAttention, part.State);
        Assert.Contains("schema-compatible", part.Reason);
    }

    [Fact]
    public async Task A_module_this_application_does_not_contain_is_listed_as_not_running()
    {
        await InstallAsync("accounting", "1.0.0");

        var part = Part(Adoption(), "accounting");

        Assert.Equal((null, "1.0.0", UpdatePartState.WaitsForRestart), (part.Running, part.Active, part.State));
    }

    [Fact]
    public void What_the_launcher_hands_over_is_read_from_the_environment()
    {
        var handOff = Launched("pos@1.0.1: it was started 3 times without starting properly\nbroken line\ncore@1.2.0: its folder does not hold the application");

        Assert.True(handOff.StartedByLauncher);
        Assert.Equal("it was started 3 times without starting properly", handOff.WhyNotStarted("pos", "1.0.1"));
        Assert.Equal("its folder does not hold the application", handOff.WhyNotStarted("core", "1.2.0"));
        Assert.Null(handOff.WhyNotStarted("pos", "1.0.2"));
        Assert.False(LauncherHandOff.FromEnvironment(_ => null).StartedByLauncher);
    }

    // ---- the screen's entry points

    [Fact]
    public async Task The_overview_and_the_check_need_updates_manage()
    {
        var nobody = new ScriptedAuthorizationService();
        var manager = new ScriptedAuthorizationService(UpdatesCapabilities.Manage);

        Assert.True((await new GetUpdateOverviewQueryHandler(Adoption(), nobody).HandleAsync(new GetUpdateOverviewQuery())).IsFailure);
        Assert.True((await new CheckForUpdatesQueryHandler(_w.Service, nobody).HandleAsync(new CheckForUpdatesQuery())).IsFailure);

        var overview = await new GetUpdateOverviewQueryHandler(Adoption(), manager).HandleAsync(new GetUpdateOverviewQuery());
        Assert.True(overview.IsSuccess);
        Assert.True(overview.Value.StartedByLauncher);
        Assert.Equal(2, overview.Value.Parts.Count);
    }

    [Fact]
    public async Task Installing_and_going_back_tell_the_shell_so_it_can_offer_a_restart()
    {
        var adoption = Adoption();
        var changes = 0;
        adoption.Changed += (_, _) => changes++;
        var manager = new ScriptedAuthorizationService(UpdatesCapabilities.Manage);

        var installed = await new InstallUpdateCommandHandler(_w.Service, manager, adoption).HandleAsync(new InstallUpdateCommand(_w.PublishModule("catalog", "1.3.0")));
        Assert.True(installed.IsSuccess);
        Assert.Equal(1, changes);

        Assert.True((await new RollbackUpdateCommandHandler(_w.Service, manager, adoption).HandleAsync(new RollbackUpdateCommand("catalog"))).IsSuccess);
        Assert.Equal(2, changes);
        Assert.Equal(UpdatePartState.InUse, Part(adoption, "catalog").State);   // back to the built-in version, which runs

        Assert.True((await new InstallUpdateCommandHandler(_w.Service, manager, adoption).HandleAsync(new InstallUpdateCommand(Path.Combine(_w.Dir, "missing.gpkg")))).IsFailure);
        Assert.Equal(2, changes);   // nothing changed, nothing announced
    }
}
