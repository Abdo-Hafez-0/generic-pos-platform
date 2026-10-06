using Client.Licensing.Application;
using Microsoft.Extensions.Logging;
using Platform.Application.Abstractions.Authorization;
using System.Windows;

namespace Client.Desktop;

/// <summary>
/// The application shell window.
///
/// It contains no business logic, navigation, or module content (modules are hosted here in a later stage). Since Stage 11 it
/// only ever opens after someone has signed in (see <see cref="App"/>), shows who that is and the license state in plain
/// words, and offers sign-out, which returns to the start screen.
///
/// Services are injected via constructor injection.
/// Do NOT use the service locator pattern here or in derived windows.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly ICurrentUser _currentUser;
    private readonly Func<SignInWindow> _signInWindow;
    private readonly Func<Task> _signOut;
    private readonly ILicenseService? _licenses;

    public MainWindow(
        ILogger<MainWindow> logger,
        ICurrentUser currentUser,
        Func<SignInWindow> signInWindow,
        DesktopSignOut signOut,
        ILicenseService? licenses = null)
    {
        _logger = logger;
        _currentUser = currentUser;
        _signInWindow = signInWindow;
        _signOut = signOut.SignOutAsync;
        _licenses = licenses;
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        SignedInText.Text = _currentUser.IsAuthenticated ? $"Signed in as {_currentUser.DisplayName} ({_currentUser.UserName})" : string.Empty;

        var notice = _licenses is null ? null : LicenseNotice.Describe(_licenses.Current);
        LicenseText.Text = notice?.Message ?? string.Empty;
        StatusText.Text = _licenses is null ? "Ready" : $"Ready - license: {_licenses.Current.State}";
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await _signOut();
        _logger.LogInformation("User signed out; returning to the start screen.");

        Hide();
        var signIn = _signInWindow();
        if (signIn.ShowDialog() != true)
        {
            Application.Current.Shutdown();
            return;
        }

        Refresh();
        Show();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _logger.LogInformation("Main window rendered. Application is ready.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _logger.LogInformation("Main window closed. Application shutting down.");
        base.OnClosed(e);
    }
}
