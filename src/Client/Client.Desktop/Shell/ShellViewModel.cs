using System.Globalization;
using Client.Desktop.Resources;
using Client.Licensing.Application;
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
public sealed class ShellViewModel : ViewModelBase
{
    private readonly IUiActionRunner _runner;
    private readonly NavigationBuilder _navigation;
    private readonly ICurrentUser _currentUser;
    private readonly IScreenFactory _factory;
    private readonly ILicenseService? _licenses;
    private readonly Dictionary<string, ScreenInstance> _open = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<NavigationGroup> _groups = [];
    private NavigationEntry? _current;
    private object? _currentView;
    private string? _lockedReason;
    private string _signedInText = string.Empty;
    private string _licenseText = string.Empty;
    private string _statusText = ShellText.Ready;

    public ShellViewModel(
        IUiActionRunner runner,
        NavigationBuilder navigation,
        ICurrentUser currentUser,
        IScreenFactory factory,
        ILicenseService? licenses = null)
    {
        _runner = runner;
        _navigation = navigation;
        _currentUser = currentUser;
        _factory = factory;
        _licenses = licenses;
        OpenCommand = Command<NavigationEntry>(entry => entry is null ? Task.CompletedTask : OpenAsync(entry));
    }

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
            if (Set(ref _lockedReason, value)) Raise(nameof(IsHome));
        }
    }

    public string SignedInText { get => _signedInText; private set => Set(ref _signedInText, value); }

    public string LicenseText { get => _licenseText; private set => Set(ref _licenseText, value); }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Rebuilds everything for the user signed in now (after sign-in, and after sign-out + sign-in as someone else).</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        SignedInText = _currentUser.IsAuthenticated
            ? string.Format(CultureInfo.CurrentCulture, ShellText.SignedInAs, _currentUser.DisplayName, _currentUser.UserName)
            : string.Empty;

        var notice = _licenses is null ? null : LicenseNotice.Describe(_licenses.Current);
        LicenseText = notice?.Message ?? string.Empty;
        StatusText = _licenses is null ? ShellText.Ready : string.Format(CultureInfo.CurrentCulture, ShellText.ReadyWithLicense, _licenses.Current.State);

        await BusyAsync(async () =>
        {
            var userId = _currentUser.UserId;
            var permissions = await _runner.QueryAsync(
                (scope, ct) => scope.Get<IPermissionProvider>().GetPermissionsAsync(userId, ct), cancellationToken);

            // Fail closed: when the permissions cannot be read, show no screen (the message says what happened).
            Groups = Accept(permissions) ? _navigation.Build(permissions.Value) : [];
        });
    }

    /// <summary>Opens a screen, or explains why it cannot be opened. The screen loads its data if it wants to (INavigationAware).</summary>
    public async Task OpenAsync(NavigationEntry entry)
    {
        Current = entry;
        if (!entry.IsAvailable)
        {
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

        CurrentView = screen.View;
        if (screen.ViewModel is INavigationAware aware)
            await aware.OnNavigatedToAsync();
    }

    /// <summary>Forgets every open screen and the navigation (sign-out): nothing of one user's work stays visible to the next.</summary>
    public void Reset()
    {
        _open.Clear();
        Current = null;
        CurrentView = null;
        LockedReason = null;
        Groups = [];
        SignedInText = string.Empty;
        ErrorMessage = null;
        StatusMessage = null;
    }
}
