using Client.Updater.Application;
using Client.Updater.Domain;
using Client.Updater.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Modules;
using Platform.Core.Modules;

namespace Updater.Tests;

/// <summary>
/// FIX-03: after a healthy start the host confirms the activated updates that really run in this process; an update that is activated
/// on disk but not running is left unconfirmed, and startup recovery rolls it back after MaxStartupAttempts starts.
/// </summary>
public sealed class StartupHealthConfirmationTests : IDisposable
{
    private readonly UpdateWorld _w = new();
    private readonly Dictionary<string, ModuleVersion> _running = new() { ["core"] = new(1, 0, 0), ["catalog"] = new(1, 0, 0) };

    public void Dispose() => _w.Dispose();

    private sealed class Running(Dictionary<string, ModuleVersion> versions) : IRunningVersions
    {
        public Exception? Throw { get; set; }

        public ModuleVersion? Of(string targetId) => Throw is { } ex ? throw ex : versions.GetValueOrDefault(targetId);
    }

    private StartupHealthConfirmation Confirmation(IRunningVersions? running = null)
        => new(_w.Store, _w.Service, running ?? new Running(_running), _w.Options, NullLogger<StartupHealthConfirmation>.Instance);

    private async Task<UpdateJournal> InstallOk(string package)
    {
        var r = await _w.Service.InstallAsync(package);
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    private UpdateState StateOf(UpdateJournal journal) => _w.Store.LoadJournal(journal.PackageId)!.State;

    [Fact]
    public async Task An_activated_module_update_that_runs_is_confirmed_and_survives_later_starts()
    {
        var journal = await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        await _w.Service.RecoverAsync();                  // the restart into the new version
        _running["catalog"] = new(1, 3, 0);               // the module host loaded 1.3.0

        var confirmed = await Confirmation().ConfirmAsync();

        Assert.Equal(["catalog"], confirmed);
        Assert.Equal(UpdateState.Confirmed, StateOf(journal));
        for (var start = 0; start < 5; start++) await _w.Service.RecoverAsync();
        Assert.Equal(UpdateState.Confirmed, StateOf(journal));
        Assert.Equal("1.3.0", _w.Store.ReadActive("catalog")!.Version);
    }

    [Fact]
    public async Task An_activated_update_that_does_not_run_is_not_confirmed_and_is_rolled_back_after_the_attempt_limit()
    {
        var journal = await InstallOk(_w.PublishModule("catalog", "1.3.0"));

        // the process still runs the built-in catalog 1.0.0 (today: no launcher loads activated versions - PKG-01)
        for (var start = 1; start <= _w.Options.MaxStartupAttempts; start++)
        {
            await _w.Service.RecoverAsync();
            Assert.Empty(await Confirmation().ConfirmAsync());
            Assert.Equal(UpdateState.Activated, StateOf(journal));
        }

        await _w.Service.RecoverAsync();
        Assert.Equal(UpdateState.RolledBack, StateOf(journal));
        Assert.Null(_w.Store.ReadActive("catalog"));
    }

    [Fact]
    public async Task A_module_that_is_not_loaded_at_all_is_not_confirmed()
    {
        var journal = await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        _running.Remove("catalog");

        Assert.Empty(await Confirmation().ConfirmAsync());
        Assert.Equal(UpdateState.Activated, StateOf(journal));
    }

    [Fact]
    public async Task A_core_update_is_confirmed_only_when_that_core_version_runs()
    {
        var journal = await InstallOk(_w.Publish(_w.CoreSpec("1.1.0", _w.PayloadDir("core-1.1.0"))));

        Assert.Empty(await Confirmation().ConfirmAsync());     // the built-in core 1.0.0 runs
        Assert.Equal(UpdateState.Activated, StateOf(journal));

        _running["core"] = new(1, 1, 0);                       // started by the launcher
        Assert.Equal(["core"], await Confirmation().ConfirmAsync());
        Assert.Equal(UpdateState.Confirmed, StateOf(journal));
    }

    [Fact]
    public async Task Only_the_targets_that_run_are_confirmed_when_several_are_activated()
    {
        var catalog = await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        var core = await InstallOk(_w.Publish(_w.CoreSpec("1.1.0", _w.PayloadDir("core-1.1.0"))));
        _running["catalog"] = new(1, 3, 0);

        Assert.Equal(["catalog"], await Confirmation().ConfirmAsync());
        Assert.Equal(UpdateState.Confirmed, StateOf(catalog));
        Assert.Equal(UpdateState.Activated, StateOf(core));
    }

    [Fact]
    public async Task It_confirms_once_per_process()
    {
        await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        _running["catalog"] = new(1, 3, 0);
        var confirmation = Confirmation();

        Assert.Single(await confirmation.ConfirmAsync());
        await InstallOk(_w.PublishModule("catalog", "1.4.0"));      // installed while running: needs a restart to be proven
        _running["catalog"] = new(1, 4, 0);

        Assert.Empty(await confirmation.ConfirmAsync());
    }

    [Fact]
    public async Task Nothing_installed_and_no_update_folder_is_simply_nothing_to_confirm()
    {
        Assert.False(Directory.Exists(_w.Store.Root));
        Assert.Empty(await Confirmation().ConfirmAsync());       // no update folder at all

        _w.Store.EnsureDirectories();
        Assert.Empty(await Confirmation().ConfirmAsync());       // an empty one
    }

    [Fact]
    public async Task A_failure_while_confirming_never_reaches_the_application()
    {
        var journal = await InstallOk(_w.PublishModule("catalog", "1.3.0"));
        var running = new Running(_running) { Throw = new InvalidOperationException("registry broken") };

        Assert.Empty(await Confirmation(running).ConfirmAsync());
        Assert.Equal(UpdateState.Activated, StateOf(journal));
    }

    [Fact]
    public void The_running_versions_are_the_configured_core_and_the_loaded_module_manifests()
    {
        var configured = Compose(new() { ["Updater:UpdateRoot"] = _w.Store.Root });
        var launched = Compose(new() { ["Updater:UpdateRoot"] = _w.Store.Root, ["Updater:RunningHostVersion"] = "1.4.0" });

        var running = configured.GetRequiredService<IRunningVersions>();
        Assert.Equal(new ModuleVersion(1, 0, 0), running.Of("core"));        // the built-in installation (BaselineHostVersion)
        Assert.Equal(new ModuleVersion(1, 2, 0), running.Of("catalog"));     // as loaded by the module host
        Assert.Null(running.Of("accounting"));                                // not loaded here
        Assert.Equal(new ModuleVersion(1, 4, 0), launched.GetRequiredService<IRunningVersions>().Of("core"));
        Assert.NotNull(configured.GetRequiredService<StartupHealthConfirmation>());
    }

    private ServiceProvider Compose(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var registry = new ModuleRegistry();
        registry.Register(new InstallRecoveryTests.FakeRuntimeModule("catalog", "1.2.0"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IModuleRegistry>(registry);
        services.AddSingleton<Platform.Application.Abstractions.Licensing.ILicenseEntitlementService>(_w.Entitlements);
        services.AddClientUpdater(configuration);
        return services.BuildServiceProvider();
    }
}
