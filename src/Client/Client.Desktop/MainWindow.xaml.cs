using Client.Desktop.Shell;
using Microsoft.Extensions.Logging;
using Platform.Presentation.Localization;
using System.Windows;

namespace Client.Desktop;

/// <summary>
/// The application shell window.
///
/// It contains no business logic. It only ever opens after someone has signed in (see <see cref="App"/>); its state - who is signed in,
/// the navigation that person may use, the open screen, the license notice - is <see cref="ShellViewModel"/> (FIX-01a). The window
/// itself only handles what a window must: the reading direction of the language and sign-out, which returns to the start screen.
///
/// Services are injected via constructor injection.
/// Do NOT use the service locator pattern here or in derived windows.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ILogger<MainWindow> _logger;
    private readonly ShellViewModel _shell;
    private readonly Func<SignInWindow> _signInWindow;
    private readonly Func<Task> _signOut;

    public MainWindow(
        ILogger<MainWindow> logger,
        ShellViewModel shell,
        Func<SignInWindow> signInWindow,
        DesktopSignOut signOut)
    {
        _logger = logger;
        _shell = shell;
        _signInWindow = signInWindow;
        _signOut = signOut.SignOutAsync;
        InitializeComponent();
        FlowDirection = UiCulture.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        DataContext = shell;
        Loaded += async (_, _) => await _shell.RefreshAsync();
    }

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await _signOut();
        _shell.Reset();
        _logger.LogInformation("User signed out; returning to the start screen.");

        Hide();
        var signIn = _signInWindow();
        if (signIn.ShowDialog() != true)
        {
            Application.Current.Shutdown();
            return;
        }

        Show();
        await _shell.RefreshAsync();
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
