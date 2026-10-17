using System.Globalization;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Desktop.Resources;
using Client.Licensing.Application;
using CloudBackup.Application;
using Client.Diagnostics.Support;
using Client.Updater.Application;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using System.Windows.Input;

namespace Client.Desktop.Shell;

/// <summary>
/// The shell's state (FIX-01a): who is signed in, the navigation that person may use, and the screen that is open.
///
/// It decides nothing about permissions itself: <see cref="NavigationBuilder"/> applies the visibility rules to the permissions the user
/// holds NOW (read through the action runner, in its own scope), and every handler behind a screen still authorizes each action.
/// Screens are created once per signed-in session and kept, so switching between screens keeps what the user was doing; signing out
/// discards them all.
/// </summary>
public sealed class ShellViewModel : ViewModelBase, IShellNavigation
{
    private readonly IUiActionRunner _runner;
    private readonly NavigationBuilder _navigation;
    private readonly ICurrentUser _currentUser;
    private readonly IScreenFactory _factory;
    private readonly ILicenseService? _licenses;
    private readonly IBackupNoticeSource? _backups;
    private readonly ICloudBackupNotice? _cloud;
    private readonly ICrashNoticeSource? _crashes;
    private readonly IUpdateAdoption? _updates;
    private readonly IApplicationRestarter? _restarter;
    private readonly SynchronizationContext? _uiContext;
    private readonly Dictionary<string, ScreenInstance> _open = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<NavigationGroup> _groups = [];
    private NavigationEntry? _current;
    private ScreenInstance? _shown;
    private object? _currentView;
    private string? _lockedReason;
    private string _signedInText = string.Empty;
    private string _licenseText = string.Empty;
    private string _statusText = ShellText.Ready;
    private string _backupNoticeText = string.Empty;
    private bool _mayConfigureBackups;
    private bool _mayMakeBackups;
    private bool _mayManageCloud;
    private bool _maySeeCrashes;
    private string _supportNoticeText = string.Empty;
    private string _updateNoticeText = string.Empty;
    private bool _canRestartForUpdate;
    private bool _mayManageUpdates;

    public ShellViewModel(
        IUiActionRunner runner,
        NavigationBuilder navigation,
        ICurrentUser currentUser,
        IScreenFactory factory,
        ILicenseService? licenses = null,
        IBackupNoticeSource? backups = null,
        ICloudBackupNotice? cloud = null,
        ICrashNoticeSource? crashes = null,
        IUpdateAdoption? updates = null,
        IApplicationRestarter? restarter = null)
    {
        _runner = runner;
        _navigation = navigation;
        _currentUser = currentUser;
        _factory = factory;
        _licenses = licenses;
        _backups = backups;
        // MISS-04c: a scheduled backup finishes on a background thread; the notice is updated on the thread that created the shell (the UI)
        _uiContext = SynchronizationContext.Current;
        if (_backups is not null) _backups.Changed += (_, _) => OnBackupsChanged();
        _cloud = cloud;
        if (_cloud is not null) _cloud.Changed += (_, _) => OnBackupsChanged();
        _crashes = crashes;
        if (_crashes is not null) _crashes.Changed += (_, _) => OnBackupsChanged();
        _updates = updates;
        if (_updates is not null) _updates.Changed += (_, _) => OnBackupsChanged();
        _restarter = restarter;
        OpenCommand = Command<NavigationEntry>(entry => entry is null ? Task.CompletedTask : OpenAsync(entry));
        RestartForUpdateCommand = Command(() => { _restarter?.Restart(); return Task.CompletedTask; }, () => CanRestartForUpdate);
    }

    /// <summary>PKG-01b: restarts through GenericPOS.exe so the update made active is used (offered, never forced).</summary>
    public ICommand RestartForUpdateCommand { get; }

    /// <summary>
    /// PKG-01b: "an update is ready; it is used after a restart" for everyone (with "Restart now"); an update that could not be used, or
    /// that waits for the application to be started by GenericPOS.exe, for those who manage updates (updates.manage); empty otherwise.
    /// </summary>
    public string UpdateNoticeText { get => _updateNoticeText; private set => Set(ref _updateNoticeText, value); }

    public bool CanRestartForUpdate { get => _canRestartForUpdate; private set => Set(ref _canRestartForUpdate, value); }

    /// <summary>Opens the screen of the entry given as the command parameter.</summary>
    public ICommand OpenCommand { get; }

    public IReadOnlyList<NavigationGroup> Groups
    {
        get => _groups;
        private set
        {
            if (Set(ref _groups, value)) Raise(nameof(HasNoScreens));
        }
    }

    /// <summary>True when the signed-in user may use no screen at all (the content area says so in plain words).</summary>
    public bool HasNoScreens => Groups.Count == 0;

    /// <summary>The entry whose screen is shown (or whose lock reason is shown), or null.</summary>
    public NavigationEntry? Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value)) Raise(nameof(CurrentTitle));
        }
    }

    public string? CurrentTitle => Current?.Title;

    /// <summary>The open screen's view, or null when nothing is open.</summary>
    public object? CurrentView
    {
        get => _currentView;
        private set
        {
            if (Set(ref _currentView, value)) Raise(nameof(IsHome));
        }
    }

    /// <summary>True when no screen is open (the welcome text shows).</summary>
    public bool IsHome => CurrentView is null && LockedReason is null;

    /// <summary>Why the selected screen cannot be opened (license), or null.</summary>
    public string? LockedReason
    {
        get => _lockedReason;
        private set
        {
            if (!Set(ref _lockedReason, value)) return;
            Raise(nameof(IsHome));
            Raise(nameof(IsLocked));
        }
    }

    /// <summary>True when the selected screen cannot be opened: only then is the lock explanation shown (it would lie over an open screen).</summary>
    public bool IsLocked => LockedReason is not null;

    public string SignedInText { get => _signedInText; private set => Set(ref _signedInText, value); }

    public string LicenseText { get => _licenseText; private set => Set(ref _licenseText, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>
    /// MISS-04c: a backup problem in plain words, for the people who can act on it (backup.create / backup.configure); empty when all is well.
    /// Updated when a backup attempt ends or the settings change, without reopening anything. MISS-04f: after a restore with the vendor's
    /// restore key it asks those who manage cloud backup (backup.cloud.manage) for a new recovery code, before any other backup notice.
    /// </summary>
    public string BackupNoticeText { get => _backupNoticeText; private set => Set(ref _backupNoticeText, value); }

    /// <summary>
    /// MISS-05: "the application closed unexpectedly on ..." for those who may save a support file (support.export), until someone opens
    /// the Support screen; empty otherwise.
    /// </summary>
    public string SupportNoticeText { get => _supportNoticeText; private set => Set(ref _supportNoticeText, value); }

    /// <summary>Rebuilds everything for the user signed in now (after sign-in, and after sign-out + sign-in as someone else).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        SignedInText = _currentUser.IsAuthenticated
            ? string.Format(CultureInfo.CurrentCulture, ShellText.SignedInAs, _currentUser.DisplayName, _currentUser.UserName)
            : string.Empty;

        var notice = _licenses is null ? null : LicenseNotice.Describe(_licenses.Current);
        LicenseText = notice?.Message ?? string.Empty;
        StatusText = _licenses is null ? ShellText.Ready : string.Format(CultureInfo.CurrentCulture, ShellText.ReadyWithLicense, Screens.Licensing.LicenseViewModel.Describe(_licenses.Current.State));

        await BusyAsync(async () =>
        {
            var userId = _currentUser.UserId;
            var permissions = await _runner.QueryAsync(
                (scope, ct) => scope.Get<IPermissionProvider>().GetPermissionsAsync(userId, ct), cancellationToken);

            // Fail closed: when the permissions cannot be read, show no screen (the message says what happened).
            Groups = Accept(permissions) ? _navigation.Build(permissions.Value) : [];
            _mayConfigureBackups = permissions.IsSuccess && permissions.Value.Contains("backup.configure", StringComparer.OrdinalIgnoreCase);
            _mayMakeBackups = permissions.IsSuccess && permissions.Value.Contains("backup.create", StringComparer.OrdinalIgnoreCase);
            _mayManageCloud = permissions.IsSuccess && permissions.Value.Contains(CloudBackupCapabilities.Manage, StringComparer.OrdinalIgnoreCase);
            _maySeeCrashes = permissions.IsSuccess && permissions.Value.Contains(SupportCapabilities.Export, StringComparer.OrdinalIgnoreCase);
            _mayManageUpdates = permissions.IsSuccess && permissions.Value.Contains(UpdatesCapabilities.Manage, StringComparer.OrdinalIgnoreCase);
        });

        await UpdateBackupNoticeAsync(cancellationToken);
    }

    /// <summary>Reads the backup notice and shows it if the signed-in person can act on it. Never fails the shell.</summary>
    public async Task UpdateBackupNoticeAsync(CancellationToken cancellationToken = default)
    {
        UpdateSupportNotice();
        UpdateUpdateNotice();
        if (_cloud is not null && _mayManageCloud)
        {
            bool needsCode;
            try
            {
                needsCode = await _cloud.NeedsNewRecoveryCodeAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                needsCode = false;   // the cloud backup files cannot be read now; the next change tries again
            }

            if (needsCode)
            {
                BackupNoticeText = ShellText.NewRecoveryCodeNeeded;
                return;
            }
        }

        if (_backups is null || (!_mayConfigureBackups && !_mayMakeBackups))
        {
            BackupNoticeText = string.Empty;
            return;
        }

        BackupNotice? notice;
        try
        {
            notice = await _backups.GetNoticeAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            notice = null;   // the backup files cannot be read now; the next change tries again
        }

        BackupNoticeText = notice switch
        {
            { Kind: BackupNoticeKind.NotConfigured } when _mayConfigureBackups => ShellText.BackupNotConfigured,
            { Kind: BackupNoticeKind.LastBackupFailed } => string.Format(CultureInfo.CurrentCulture, ShellText.BackupFailed,
                notice.At?.ToString("g", CultureInfo.CurrentCulture) ?? string.Empty, notice.Reason ?? string.Empty),
            _ => string.Empty
        };
    }

    private void UpdateUpdateNotice()
    {
        IReadOnlyList<UpdatePart> parts;
        try
        {
            parts = _updates is null || !_currentUser.IsAuthenticated ? [] : _updates.Parts();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            parts = [];
        }

        var ready = _updates is { Launcher.StartedByLauncher: true } && parts.Any(p => p.State == UpdatePartState.WaitsForRestart);
        CanRestartForUpdate = ready && _restarter is not null;
        UpdateNoticeText = ready ? ShellText.UpdateReady
            : _mayManageUpdates && parts.Any(p => p.State is UpdatePartState.NotStarted or UpdatePartState.NeedsAttention) ? ShellText.UpdateProblem
            : _mayManageUpdates && parts.Any(p => p.State == UpdatePartState.WaitsForLauncher) ? ShellText.UpdateNeedsLauncher
            : string.Empty;
    }

    private void UpdateSupportNotice()
    {
        if (_crashes is null || !_maySeeCrashes)
        {
            SupportNoticeText = string.Empty;
            return;
        }

        IReadOnlyList<Client.Diagnostics.Crash.CrashReportInfo> unseen;
        try
        {
            unseen = _crashes.UnseenCrashes();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            unseen = [];
        }

        SupportNoticeText = unseen.Count == 0
            ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, ShellText.CrashNotice, unseen.Max(r => r.At).ToString("g", CultureInfo.CurrentCulture));
    }

    private void OnBackupsChanged()
    {
        if (_uiContext is null) _ = UpdateBackupNoticeAsync();
        else _uiContext.Post(_ => _ = UpdateBackupNoticeAsync(), null);
    }

    /// <summary>
    /// Opens a screen, or explains why it cannot be opened. The screen that was shown before is told it was left, and the new one loads its
    /// data if it wants to (INavigationAware).
    /// </summary>
    public async Task OpenAsync(NavigationEntry entry)
    {
        Current = entry;
        if (!entry.IsAvailable)
        {
            await LeaveShownScreenAsync();
            CurrentView = null;
            LockedReason = entry.Reason;
            return;
        }

        LockedReason = null;
        if (!_open.TryGetValue(entry.Screen.Id, out var screen))
        {
            screen = _factory.Create(entry.Screen);
            _open[entry.Screen.Id] = screen;
        }

        if (!ReferenceEquals(screen, _shown)) await LeaveShownScreenAsync();
        _shown = screen;
        CurrentView = screen.View;
        if (screen.ViewModel is INavigationAware aware)
            await aware.OnNavigatedToAsync();
    }

    /// <summary>
    /// Forgets every open screen and the navigation (sign-out): nothing of one user's work stays visible to the next. The screen that was
    /// shown is told it was left first (FIX-02: the cashier screen stops listening to the scanner).
    /// </summary>
    public async Task ResetAsync()
    {
        await LeaveShownScreenAsync();
        _open.Clear();
        Current = null;
        CurrentView = null;
        LockedReason = null;
        Groups = [];
        SignedInText = string.Empty;
        BackupNoticeText = string.Empty;
        _mayConfigureBackups = false;
        _mayMakeBackups = false;
        _mayManageCloud = false;
        _maySeeCrashes = false;
        SupportNoticeText = string.Empty;
        _mayManageUpdates = false;
        UpdateNoticeText = string.Empty;
        CanRestartForUpdate = false;
        ErrorMessage = null;
        StatusMessage = null;
    }

    private async Task LeaveShownScreenAsync()
    {
        var shown = _shown;
        _shown = null;
        if (shown?.ViewModel is INavigationAware aware)
            await aware.OnNavigatedFromAsync();
    }
}
