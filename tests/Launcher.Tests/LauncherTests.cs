using System.Diagnostics;
using Client.Launcher;
using Client.Updater.Domain;
using Client.Updater.Infrastructure;
using Updates.Contracts;

namespace Launcher.Tests;

/// <summary>
/// PKG-01: GenericPOS.exe starts what the updater made active - the installed application, an activated core, or a folder assembled from
/// the core and the activated modules - and holds back what cannot be started safely. The update folders are written with the real
/// Client.Updater store, so these tests also hold the on-disk contract between the two.
/// </summary>
public sealed class LauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gp-launcher-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _app;
    private readonly string _updates;
    private readonly UpdateStore _store;
    private readonly LauncherLog _log;

    public LauncherTests()
    {
        _app = Path.Combine(_root, "Program Files", "GenericPOS", "app");
        _updates = Path.Combine(_root, "Updates");
        _store = new UpdateStore(_updates);
        _log = new LauncherLog(Path.Combine(_updates, "launcher"));
        foreach (var file in new[] { "Client.Desktop.exe", "Client.Desktop.dll", "Client.Desktop.deps.json", "Platform.Core.dll", "POS.UI.dll", "POS.Infrastructure.dll", "CashManagement.UI.dll", "ar/POS.UI.resources.dll" })
            Write(Path.Combine(_app, file), "installed " + file);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private LauncherSettings Settings(int maxStartupAttempts = 2) => new(_app, _updates, "1.0.0", maxStartupAttempts);

    private ProcessStartInfo Prepare(int maxStartupAttempts = 2, params string[] args)
        => Program.Prepare(Settings(maxStartupAttempts), @"C:\GenericPOS\GenericPOS.exe", args, _log);

    /// <summary>Deploys a version the way the updater does (payload + .package/manifest.json) and activates it.</summary>
    private string Deploy(string target, string version, IEnumerable<string> files, string? previous = null, bool activate = true)
    {
        var directory = _store.VersionDir(target, version);
        foreach (var file in files) Write(Path.Combine(directory, file), $"{target} {version} {file}");
        Write(Path.Combine(directory, ".package", "manifest.json"), "{}");
        if (activate) _store.WriteActive(target, new ActivePointer { Version = version, PreviousVersion = previous });
        return directory;
    }

    private void Journal(string target, string version, UpdateState state, bool migration = false, bool oldBinaryCompatible = true)
        => _store.SaveJournal(new UpdateJournal
        {
            PackageId = Guid.NewGuid(), PackageType = target == "core" ? PackageType.Core : PackageType.Module, TargetId = target, Version = version,
            State = state, MigrationRequired = migration, OldBinaryCompatible = oldBinaryCompatible
        });

    private static string Held(ProcessStartInfo start) => start.Environment[Program.HeldBackVariable] ?? string.Empty;

    // ---- what is started

    [Fact]
    public void With_nothing_active_the_installed_application_starts_as_it_is_without_a_copy()
    {
        var start = Prepare(args: [.. LauncherSettings.ApplicationArguments(["--app", _app, "--safe"])]);

        Assert.Equal(Path.Combine(_app, "Client.Desktop.exe"), start.FileName);
        Assert.Equal(_app, start.WorkingDirectory);
        Assert.Equal(["--safe"], start.ArgumentList);   // only the launcher's own arguments are kept back (Program removes --app)
        Assert.Equal("1.0.0", start.Environment[Program.UpdaterRunningHostVersion]);
        Assert.Equal(_updates, start.Environment["GENERICPOS_Updater__UpdateRoot"]);
        Assert.Equal(@"C:\GenericPOS\GenericPOS.exe", start.Environment[Program.LauncherPathVariable]);
        Assert.Equal(_app, start.Environment[Program.AppDirectoryVariable]);
        Assert.Equal(string.Empty, start.Environment[Program.ModulesVariable]);
        Assert.Equal(string.Empty, Held(start));
        Assert.False(Directory.Exists(Path.Combine(_updates, "runs")));
    }

    [Fact]
    public void An_activated_core_runs_from_its_own_folder_and_says_which_version_runs()
    {
        var core = Deploy("core", "1.1.0", ["Client.Desktop.exe", "Client.Desktop.dll", "Platform.Core.dll"], previous: null);

        var start = Prepare();

        // a copy, never the deployed folder itself: the updater may remove that one at any time (rollback, pruning)
        Assert.StartsWith(Path.Combine(_updates, "runs"), start.WorkingDirectory);
        Assert.Equal(Path.Combine(start.WorkingDirectory, "Client.Desktop.exe"), start.FileName);
        Assert.Equal("core 1.1.0 Client.Desktop.dll", File.ReadAllText(Path.Combine(start.WorkingDirectory, "Client.Desktop.dll")));
        Assert.False(File.Exists(Path.Combine(start.WorkingDirectory, "POS.UI.dll")));   // nothing of the installed application
        Assert.Equal("1.1.0", start.Environment[Program.UpdaterRunningHostVersion]);
        Assert.NotEqual(core, start.WorkingDirectory);
    }

    [Fact]
    public void A_core_folder_without_the_application_is_held_back_and_the_installed_one_starts()
    {
        Deploy("core", "1.1.0", ["Platform.Core.dll"]);

        var start = Prepare();

        Assert.Equal(Path.Combine(_app, "Client.Desktop.exe"), start.FileName);
        Assert.Equal("1.0.0", start.Environment[Program.UpdaterRunningHostVersion]);
        Assert.Contains("core@1.1.0: its folder does not hold the application", Held(start));
    }

    [Fact]
    public void An_activated_module_is_laid_over_the_core_in_an_assembled_folder_which_is_reused()
    {
        Deploy("pos", "1.0.1", ["POS.UI.dll", "POS.Infrastructure.dll", "POS.UI.pdb", "ar/POS.UI.resources.dll"], previous: null);

        var start = Prepare();

        var run = start.WorkingDirectory;
        Assert.StartsWith(Path.Combine(_updates, "runs"), run);
        Assert.Equal(Path.Combine(run, "Client.Desktop.exe"), start.FileName);
        Assert.Equal("installed Client.Desktop.dll", File.ReadAllText(Path.Combine(run, "Client.Desktop.dll")));   // the core
        Assert.Equal("installed CashManagement.UI.dll", File.ReadAllText(Path.Combine(run, "CashManagement.UI.dll")));   // other modules: built in
        Assert.Equal("pos 1.0.1 POS.UI.dll", File.ReadAllText(Path.Combine(run, "POS.UI.dll")));   // the update
        Assert.Equal("pos 1.0.1 ar/POS.UI.resources.dll", File.ReadAllText(Path.Combine(run, "ar", "POS.UI.resources.dll")));
        Assert.True(File.Exists(Path.Combine(run, "POS.UI.pdb")));
        Assert.False(Directory.Exists(Path.Combine(run, ".package")));
        Assert.Equal("1.0.0", start.Environment[Program.UpdaterRunningHostVersion]);
        Assert.Equal("pos@1.0.1", start.Environment[Program.ModulesVariable]);   // the application knows POS is not the installed one

        File.Delete(Path.Combine(run, "POS.UI.pdb"));   // a finished folder is used as it is, not built again
        Assert.Equal(run, Prepare().WorkingDirectory);
        Assert.False(File.Exists(Path.Combine(run, "POS.UI.pdb")));
    }

    [Theory]
    [InlineData("Platform.Core.dll", "it contains Platform.Core.dll, which does not belong to it")]
    [InlineData("CashManagement.UI.dll", "it contains CashManagement.UI.dll, which does not belong to it")]
    [InlineData("POS.Reports.dll", "it adds POS.Reports.dll, which the installed application does not have")]
    [InlineData("plugins/POS.UI.dll", "it adds plugins/POS.UI.dll, which the installed application does not have")]
    [InlineData("ar/POS.Reports.resources.dll", "it adds ar/POS.Reports.resources.dll, which the installed application does not have")]
    public void A_module_that_would_change_more_than_its_own_files_is_held_back(string file, string reason)
    {
        Deploy("pos", "1.0.1", ["POS.UI.dll", file]);

        var start = Prepare();

        Assert.Equal(Path.Combine(_app, "Client.Desktop.exe"), start.FileName);   // nothing to lay over: the installed application
        Assert.Contains("pos@1.0.1: " + reason, Held(start));
        Assert.Contains(reason, File.ReadAllText(_log.Path));
    }

    [Fact]
    public void A_module_id_with_a_dash_owns_the_assemblies_named_without_it()
    {
        Deploy("cash-management", "1.2.0", ["CashManagement.UI.dll"]);

        var start = Prepare();

        Assert.Equal("cash-management 1.2.0 CashManagement.UI.dll", File.ReadAllText(Path.Combine(start.WorkingDirectory, "CashManagement.UI.dll")));
        Assert.Equal(string.Empty, Held(start));
    }

    [Fact]
    public void Modules_are_laid_over_an_activated_core()
    {
        var core = Deploy("core", "1.1.0", ["Client.Desktop.exe", "Client.Desktop.dll", "POS.UI.dll"]);
        Deploy("pos", "1.0.1", ["POS.UI.dll"]);

        var start = Prepare();

        Assert.Equal($"core 1.1.0 Client.Desktop.dll", File.ReadAllText(Path.Combine(start.WorkingDirectory, "Client.Desktop.dll")));
        Assert.Equal("pos 1.0.1 POS.UI.dll", File.ReadAllText(Path.Combine(start.WorkingDirectory, "POS.UI.dll")));
        Assert.Equal("1.1.0", start.Environment[Program.UpdaterRunningHostVersion]);
        Assert.NotEqual(core, start.WorkingDirectory);
    }

    // ---- updates that never start properly

    [Fact]
    public void An_update_that_never_gets_far_enough_to_roll_itself_back_is_held_back_after_the_application_had_its_chances()
    {
        Deploy("pos", "1.0.0", ["POS.UI.dll"], activate: false);
        Deploy("pos", "1.0.1", ["POS.UI.dll"], previous: "1.0.0");
        Journal("pos", "1.0.1", UpdateState.Activated);

        for (var start = 1; start <= 3; start++)   // MaxStartupAttempts 2: the application rolls back at its third start itself
            Assert.Equal("pos 1.0.1 POS.UI.dll", File.ReadAllText(Path.Combine(Prepare().WorkingDirectory, "POS.UI.dll")));

        var fourth = Prepare();   // it never did: the launcher starts the version before it
        Assert.Equal("pos 1.0.0 POS.UI.dll", File.ReadAllText(Path.Combine(fourth.WorkingDirectory, "POS.UI.dll")));
        Assert.Contains("pos@1.0.1: it was started 3 times without starting properly", Held(fourth));
    }

    [Fact]
    public void A_core_held_back_without_a_previous_version_falls_back_to_the_installed_one()
    {
        Deploy("core", "1.1.0", ["Client.Desktop.exe"]);
        Journal("core", "1.1.0", UpdateState.Activated);

        for (var i = 0; i < 3; i++) Assert.Equal("1.1.0", Prepare(maxStartupAttempts: 2).Environment[Program.UpdaterRunningHostVersion]);

        var start = Prepare(maxStartupAttempts: 2);
        Assert.Equal(Path.Combine(_app, "Client.Desktop.exe"), start.FileName);
        Assert.Equal("1.0.0", start.Environment[Program.UpdaterRunningHostVersion]);
    }

    [Fact]
    public void An_update_that_changed_the_data_for_the_new_version_is_never_held_back_by_the_launcher()
    {
        Deploy("pos", "1.0.1", ["POS.UI.dll"], previous: null);
        Journal("pos", "1.0.1", UpdateState.Activated, migration: true, oldBinaryCompatible: false);

        for (var i = 0; i < 6; i++)
            Assert.Equal("pos 1.0.1 POS.UI.dll", File.ReadAllText(Path.Combine(Prepare().WorkingDirectory, "POS.UI.dll")));
    }

    [Fact]
    public void A_confirmed_update_is_no_longer_counted()
    {
        Deploy("pos", "1.0.1", ["POS.UI.dll"]);
        Journal("pos", "1.0.1", UpdateState.Activated);
        Prepare();
        Assert.Contains("pos@1.0.1", File.ReadAllText(Path.Combine(_updates, "launcher", "attempts.json")));

        foreach (var file in Directory.GetFiles(Path.Combine(_updates, "journal"))) File.Delete(file);
        Journal("pos", "1.0.1", UpdateState.Confirmed);
        Prepare();

        Assert.DoesNotContain("pos@1.0.1", File.ReadAllText(Path.Combine(_updates, "launcher", "attempts.json")));
    }

    // ---- folders

    [Fact]
    public void A_reinstalled_application_gets_a_new_assembled_folder_and_only_the_current_and_previous_folders_are_kept()
    {
        Deploy("pos", "1.0.1", ["POS.UI.dll"]);
        var first = Prepare().WorkingDirectory;

        Write(Path.Combine(_app, "Client.Desktop.dll"), "installed again, another build");
        File.SetLastWriteTimeUtc(Path.Combine(_app, "Client.Desktop.dll"), DateTime.UtcNow.AddMinutes(1));
        var second = Prepare().WorkingDirectory;
        Assert.NotEqual(first, second);
        Assert.Equal("installed again, another build", File.ReadAllText(Path.Combine(second, "Client.Desktop.dll")));

        Deploy("pos", "1.0.2", ["POS.UI.dll"], previous: "1.0.1");
        var third = Prepare().WorkingDirectory;

        Assert.Equal(new[] { second, third }.Order(StringComparer.OrdinalIgnoreCase), Directory.GetDirectories(Path.Combine(_updates, "runs")).Order(StringComparer.OrdinalIgnoreCase));
        Assert.False(Directory.Exists(first));
    }

    [Fact]
    public void An_unreadable_pointer_counts_as_no_update()
    {
        Write(Path.Combine(_updates, "installed", "pos", "active.json"), "{ not json");
        Write(Path.Combine(_updates, "installed", "sales", "active.json"), """{ "version": "../../evil" }""");

        var start = Prepare();

        Assert.Equal(Path.Combine(_app, "Client.Desktop.exe"), start.FileName);
        Assert.Equal(string.Empty, Held(start));
    }

    [Fact]
    public void When_the_update_folder_cannot_be_used_the_installed_application_starts_and_the_reason_is_passed_on()
    {
        Deploy("pos", "1.0.1", ["POS.UI.dll"]);
        Write(Path.Combine(_updates, "runs"), "a file where the folder should be");

        var start = Prepare();

        Assert.Equal(Path.Combine(_app, "Client.Desktop.exe"), start.FileName);
        Assert.StartsWith("updates@-: the update folder could not be used", Held(start));
        Assert.Contains("the installed application is started", File.ReadAllText(_log.Path));
    }

    // ---- settings

    [Fact]
    public void The_settings_are_read_like_the_application_reads_them()
    {
        Write(Path.Combine(_app, "appsettings.json"), """
            {
              // comments are allowed, as in the application
              "Updater": { "UpdateRoot": "D:\\Shop\\Updates", "MaxStartupAttempts": 4, "BaselineHostVersion": "1.3.0" },
            }
            """);
        var environment = new Dictionary<string, string> { ["GENERICPOS_LAUNCHER_APPDIR"] = _app };

        var fromFile = LauncherSettings.Resolve(@"C:\Elsewhere", [], n => environment.GetValueOrDefault(n));
        Assert.Equal((_app, @"D:\Shop\Updates", "1.3.0", 4), (fromFile.AppDirectory, fromFile.UpdateRoot, fromFile.BaselineHostVersion, fromFile.MaxStartupAttempts));

        environment["GENERICPOS_Updater__UpdateRoot"] = @"E:\Other";
        Assert.Equal(@"E:\Other", LauncherSettings.Resolve(@"C:\Elsewhere", [], n => environment.GetValueOrDefault(n)).UpdateRoot);

        var byDefault = LauncherSettings.Resolve(_root, [], _ => null);
        Assert.Equal(Path.Combine(_root, "app"), byDefault.AppDirectory);
        Assert.EndsWith(Path.Combine("GenericPOS", "Updates"), byDefault.UpdateRoot);
        Assert.Equal(("1.0.0", 2), (byDefault.BaselineHostVersion, byDefault.MaxStartupAttempts));

        Assert.Equal(["--safe", "x"], LauncherSettings.ApplicationArguments(["--app", @"C:\x", "--safe", "x"]));
    }

    [Fact]
    public void The_launcher_depends_on_nothing_of_the_solution()
    {
        // ARCH-PKG-001: it is replaced only by the installer, so it must not share assemblies with what updates replace
        var references = typeof(LauncherSettings).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.All(references, name => Assert.True(name.StartsWith("System", StringComparison.Ordinal) || name == "Microsoft.Win32.Primitives", name));
    }
}
