using System.Diagnostics;
using System.Globalization;
using Client.Backup.Application;
using Client.Backup.Infrastructure;
using Client.Desktop.Resources;
using Client.Desktop.Screens;
using Client.Desktop.Screens.Backup;
using Client.Desktop.Shell;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using Tests.Common.Security;

namespace UI.Tests;

/// <summary>
/// MISS-04d: the backup screen on the real Client.Backup services and a real database file - what the person sees, what they may do,
/// and that restoring and deleting are asked first in a sentence that says what happens.
/// </summary>
public sealed class BackupScreenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "genericpos-ui-bak-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRestarter _restarter = new();
    private ServiceProvider? _provider;

    public BackupScreenTests()
    {
        var database = Path.Combine(_root, "GenericPOS", "genericpos.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE "__EFMigrationsHistory" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('20261005120224_InitialSalesSchema', '10.0.11');
            CREATE TABLE sal_Sales (Id INTEGER PRIMARY KEY, Total TEXT NOT NULL);
            INSERT INTO sal_Sales (Total) VALUES ('2.50');
            """;
        command.ExecuteNonQuery();
    }

    private string BackupFolder => Path.Combine(_root, "usb");

    public void Dispose()
    {
        _provider?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private BackupViewModel Screen(params string[] allowed)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = _root,
            ["Backup:LocalFolder"] = BackupFolder,
        }).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAuthorizationService>(new ScriptedAuthorizationService(allowed));
        services.AddSingleton<ICurrentUser, FakeCurrentUser>();
        new ClientBackupHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration }, services);
        _provider?.Dispose();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        return new BackupViewModel(new UiActionRunner(_provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance), _restarter);
    }

    private static string[] Everything => [BackupCapabilities.Create, BackupCapabilities.Restore, BackupCapabilities.Delete, BackupCapabilities.Configure];

    [Fact]
    public void The_screen_is_in_administration_needs_backup_create_and_is_never_locked_by_the_license()
    {
        var screen = Assert.Single(new DesktopScreens().GetScreens(), s => s.Id == "backup.backups");

        Assert.Equal(ScreenGroups.Administration, screen.Group);
        Assert.Equal(BackupCapabilities.Create, screen.RequiredCapability);
        Assert.Equal(typeof(BackupViewModel), screen.ViewModelType);
        Assert.Equal(BackupText.ScreenTitle, screen.Title());
        Assert.All(BackupCapabilities.All, c => Assert.Equal(LicenseRequirement.None, c.License));
    }

    [Fact]
    public async Task The_owner_sees_the_state_backs_up_and_checks_a_backup()
    {
        var vm = Screen(Everything);
        await vm.OnNavigatedToAsync();

        Assert.True(vm.CanMake && vm.CanRestore && vm.CanDelete && vm.CanConfigure);
        Assert.Equal(BackupText.NoBackupYet, vm.LastBackupText);
        Assert.Equal(BackupFolder, vm.FolderText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, BackupText.ScheduleAt, "23:00"), vm.ScheduleText);
        Assert.Empty(vm.Backups);

        vm.BackUpNowCommand.Execute(null);
        await WaitIdle(vm);

        var row = Assert.Single(vm.Backups);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, BackupText.BackupMade, row.Record.FileName), vm.StatusMessage);
        Assert.Contains(row.Record.FileName, vm.LastBackupText);
        Assert.Equal(BackupText.NotChecked, row.CheckText);
        Assert.Equal(BackupText.WhereLocal, row.WhereText);
        Assert.Equal(BackupText.OriginManual, row.HowText);

        vm.Selected = row;
        vm.CheckCommand.Execute(null);
        await WaitIdle(vm);

        Assert.Equal(BackupText.CheckPassed, Assert.Single(vm.Backups).CheckText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, BackupText.CheckedGood, row.Record.FileName), vm.StatusMessage);
    }

    [Fact]
    public async Task A_restore_says_what_it_replaces_waits_for_a_confirmation_and_then_offers_to_restart()
    {
        var vm = Screen(Everything);
        await vm.OnNavigatedToAsync();
        vm.BackUpNowCommand.Execute(null);
        await WaitIdle(vm);
        vm.Selected = vm.Backups[0];

        vm.RestoreCommand.Execute(null);
        await WaitIdle(vm);

        Assert.True(vm.IsRestorePrepared);
        Assert.Contains(vm.Selected!.Record.FileName, vm.RestoreQuestion);
        Assert.Contains(vm.Selected.Record.CreatedAt.ToString("g", CultureInfo.CurrentCulture), vm.RestoreQuestion);
        Assert.False(vm.IsRestorePending);

        vm.ConfirmRestoreCommand.Execute(null);
        await WaitIdle(vm);

        Assert.False(vm.IsRestorePrepared);
        Assert.True(vm.IsRestorePending);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, BackupText.RestorePending, vm.Backups[0].Record.FileName), vm.PendingText);
        Assert.False(vm.RestoreCommand.CanExecute(null), "one restore at a time");

        vm.RestartNowCommand.Execute(null);
        await WaitIdle(vm);
        Assert.Equal(1, _restarter.Restarts);

        // a screen opened again (e.g. after signing in again before the restart) still knows the restore is waiting
        var again = Screen(Everything);
        await again.OnNavigatedToAsync();
        Assert.True(again.IsRestorePending);

        again.CancelRestoreCommand.Execute(null);
        await WaitIdle(again);
        Assert.False(again.IsRestorePending);
        Assert.Equal(BackupText.RestoreCancelled, again.StatusMessage);
    }

    [Fact]
    public async Task A_backup_file_from_another_pc_can_be_chosen_for_a_restore()
    {
        var vm = Screen(Everything);
        await vm.OnNavigatedToAsync();
        vm.BackUpNowCommand.Execute(null);
        await WaitIdle(vm);
        var file = Path.Combine(_root, "from-old-pc.db");
        File.Copy(vm.Backups[0].Record.Location, file);

        vm.RestoreFromFileCommand.Execute(file);
        await WaitIdle(vm);

        Assert.True(vm.IsRestorePrepared);
        Assert.Contains("from-old-pc.db", vm.RestoreQuestion);
    }

    [Fact]
    public async Task Deleting_asks_first_and_keeping_changes_nothing()
    {
        var vm = Screen(Everything);
        await vm.OnNavigatedToAsync();
        vm.BackUpNowCommand.Execute(null);
        await WaitIdle(vm);
        vm.Selected = vm.Backups[0];
        var file = vm.Selected.Record.Location;

        vm.DeleteCommand.Execute(null);
        await WaitIdle(vm);
        Assert.True(vm.IsDeleteAsked);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, BackupText.DeleteQuestion, vm.Selected!.Record.FileName), vm.DeleteQuestion);
        Assert.True(File.Exists(file));

        vm.KeepCommand.Execute(null);
        await WaitIdle(vm);
        Assert.False(vm.IsDeleteAsked);
        Assert.True(File.Exists(file));

        vm.DeleteCommand.Execute(null);
        await WaitIdle(vm);
        vm.ConfirmDeleteCommand.Execute(null);
        await WaitIdle(vm);
        Assert.False(File.Exists(file));
        Assert.Empty(vm.Backups);
        Assert.Equal(BackupText.NoBackupYet, vm.LastBackupText);
    }

    [Fact]
    public async Task Settings_are_checked_on_the_screen_and_saved_through_the_handler()
    {
        var vm = Screen(Everything);
        await vm.OnNavigatedToAsync();

        vm.DailyAtText = "25:99";
        vm.SaveSettingsCommand.Execute(null);
        await WaitIdle(vm);
        Assert.Equal(BackupText.InvalidTime, vm.ErrorMessage);

        vm.DailyAtText = "6:30";
        vm.KeepText = "many";
        vm.SaveSettingsCommand.Execute(null);
        await WaitIdle(vm);
        Assert.Equal(BackupText.InvalidKeep, vm.ErrorMessage);

        var other = Path.Combine(_root, "share");
        vm.Folder = other;
        vm.KeepText = "7";
        vm.ScheduleEnabled = true;
        vm.SaveSettingsCommand.Execute(null);
        await WaitIdle(vm);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(BackupText.Saved, vm.StatusMessage);
        Assert.Equal(other, vm.FolderText);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, BackupText.ScheduleAt, "06:30"), vm.ScheduleText);
        Assert.Equal("06:30", vm.DailyAtText);

        vm.Folder = "relative";
        vm.SaveSettingsCommand.Execute(null);
        await WaitIdle(vm);
        Assert.StartsWith("Enter the full path", vm.ErrorMessage);   // the business message (English, FIX-13 decision)
    }

    [Fact]
    public async Task Someone_who_may_only_make_backups_sees_no_restore_delete_or_settings()
    {
        var vm = Screen(BackupCapabilities.Create);
        await vm.OnNavigatedToAsync();
        vm.BackUpNowCommand.Execute(null);
        await WaitIdle(vm);
        vm.Selected = vm.Backups[0];

        Assert.True(vm.CanMake);
        Assert.False(vm.CanRestore || vm.CanDelete || vm.CanConfigure);
        Assert.False(vm.RestoreCommand.CanExecute(null));
        Assert.False(vm.RestoreFromFileCommand.CanExecute("x.db"));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.SaveSettingsCommand.CanExecute(null));
        Assert.True(vm.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task Without_backup_create_nothing_is_read()
    {
        var vm = Screen();
        await vm.OnNavigatedToAsync();

        Assert.False(vm.CanMake);
        Assert.Empty(vm.Backups);
        Assert.Equal(string.Empty, vm.LastBackupText);
        Assert.Null(vm.ErrorMessage);
        Assert.Empty(((ScriptedAuthorizationService)_provider!.GetRequiredService<IAuthorizationService>()).Asked);   // no handler was even called
    }

    // ------------------------------------------------------------------ restart hand-off

    [Fact]
    public void A_restarted_copy_waits_until_the_previous_copy_has_ended()
    {
        using var previous = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 > nul") { UseShellExecute = false, CreateNoWindow = true })!;
        Environment.SetEnvironmentVariable(RestartHandoff.PreviousProcessVariable, previous.Id.ToString(CultureInfo.InvariantCulture));

        RestartHandoff.WaitForPreviousInstance();

        Assert.True(previous.HasExited);
        Assert.Null(Environment.GetEnvironmentVariable(RestartHandoff.PreviousProcessVariable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not a number")]
    [InlineData("self")]
    [InlineData("999999")]
    public void A_normal_start_or_an_unknown_previous_copy_does_not_wait(string? value)
    {
        Environment.SetEnvironmentVariable(RestartHandoff.PreviousProcessVariable, value == "self" ? Environment.ProcessId.ToString(CultureInfo.InvariantCulture) : value);
        var watch = Stopwatch.StartNew();

        RestartHandoff.WaitForPreviousInstance();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Null(Environment.GetEnvironmentVariable(RestartHandoff.PreviousProcessVariable));
    }

    private static async Task WaitIdle(BackupViewModel vm)
    {
        for (var i = 0; i < 500 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private sealed class FakeRestarter : IApplicationRestarter
    {
        public int Restarts { get; private set; }

        public void Restart() => Restarts++;
    }
}
