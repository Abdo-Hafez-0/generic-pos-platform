using System.Globalization;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Desktop.Resources;
using Client.Desktop.Shell;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;

namespace UI.Tests;

/// <summary>MISS-04c: the shell tells the people who can act on it that backups are not set up, or that the last one failed.</summary>
public sealed class ShellBackupNoticeTests
{
    private readonly FakePermissions _permissions = new();
    private readonly FakeNoticeSource _notices = new();
    private readonly ShellViewModel _shell;

    public ShellBackupNoticeTests()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPermissionProvider>(_ => _permissions);
        var runner = new UiActionRunner(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        var catalog = new CapabilityCatalog([new CapabilityProvider(new CapabilityDescriptor("pos.sale.create", "pos", "Sell", "Sell"))]);
        var navigation = new NavigationBuilder([new ScreenProvider(Screens.Of("pos.sell", "pos", ScreenGroups.Sales, "pos.sale.create"))], catalog, new FakeLicensing("pos"));
        _shell = new ShellViewModel(runner, navigation, new FakeCurrentUser(), new FakeScreenFactory(), backups: _notices);
    }

    [Fact]
    public async Task Without_a_backup_folder_the_people_who_configure_backups_are_told()
    {
        _notices.Notice = new BackupNotice(BackupNoticeKind.NotConfigured);
        _permissions.Held.Add("backup.configure");

        await _shell.RefreshAsync();

        Assert.Equal(ShellText.BackupNotConfigured, _shell.BackupNoticeText);
    }

    [Fact]
    public async Task People_who_only_make_backups_are_not_told_about_the_folder_but_are_told_about_a_failure()
    {
        _notices.Notice = new BackupNotice(BackupNoticeKind.NotConfigured);
        _permissions.Held.Add("backup.create");
        await _shell.RefreshAsync();
        Assert.Equal(string.Empty, _shell.BackupNoticeText);

        var at = new DateTimeOffset(2026, 10, 10, 23, 0, 0, TimeSpan.Zero);
        _notices.Notice = new BackupNotice(BackupNoticeKind.LastBackupFailed, at, "The backup could not be written to E:\\Backups.");
        await _shell.UpdateBackupNoticeAsync();

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, ShellText.BackupFailed, at.ToString("g", CultureInfo.CurrentCulture), "The backup could not be written to E:\\Backups."),
            _shell.BackupNoticeText);
        Assert.Contains("E:\\Backups", _shell.BackupNoticeText);
    }

    [Fact]
    public async Task A_cashier_without_backup_permissions_sees_nothing()
    {
        _notices.Notice = new BackupNotice(BackupNoticeKind.LastBackupFailed, DateTimeOffset.Now, "failed");
        _permissions.Held.Add("pos.sale.create");

        await _shell.RefreshAsync();

        Assert.Equal(string.Empty, _shell.BackupNoticeText);
        Assert.Equal(0, _notices.Asked);
    }

    [Fact]
    public async Task The_notice_follows_backup_changes_without_reopening_anything_and_sign_out_clears_it()
    {
        _permissions.Held.Add("backup.create");
        await _shell.RefreshAsync();
        Assert.Equal(string.Empty, _shell.BackupNoticeText);

        _notices.Notice = new BackupNotice(BackupNoticeKind.LastBackupFailed, DateTimeOffset.Now, "The drive is not connected.");
        _notices.RaiseChanged();   // no synchronization context in the test: updated at once
        await WaitUntil(() => _shell.BackupNoticeText.Contains("The drive is not connected."));

        _notices.Notice = null;     // a later backup succeeded
        _notices.RaiseChanged();
        await WaitUntil(() => _shell.BackupNoticeText.Length == 0);

        _notices.Notice = new BackupNotice(BackupNoticeKind.LastBackupFailed, DateTimeOffset.Now, "again");
        await _shell.UpdateBackupNoticeAsync();
        await _shell.ResetAsync();
        Assert.Equal(string.Empty, _shell.BackupNoticeText);
        await _shell.UpdateBackupNoticeAsync();   // signed out: nobody to tell
        Assert.Equal(string.Empty, _shell.BackupNoticeText);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class FakeNoticeSource : IBackupNoticeSource
    {
        public BackupNotice? Notice { get; set; }

        public int Asked { get; private set; }

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<BackupNotice?> GetNoticeAsync(CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(Notice);
        }
    }
}
