using Client.Desktop.Shell;
using Microsoft.Extensions.Logging;
using Platform.Presentation.Localization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Client.Desktop;

/// <summary>
/// The application shell window.
///
/// It contains no business logic. It only ever opens after someone has signed in (see <see cref="App"/>); its state - who is signed in,
/// the navigation that person may use, the open screen, the license notice - is <see cref="ShellViewModel"/> (FIX-01a). The window
/// itself only handles what a window must: the reading direction of the language, sign-out, which returns to the start screen, and
/// handing key presses to the barcode scanner decoder (FIX-02, see <see cref="ScannerKeyboard"/>).
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
    private readonly ScannerKeyboard _scanner;

    public MainWindow(
        ILogger<MainWindow> logger,
        ShellViewModel shell,
        Func<SignInWindow> signInWindow,
        DesktopSignOut signOut,
        ScannerKeyboard? scanner = null)
    {
        _logger = logger;
        _shell = shell;
        _signInWindow = signInWindow;
        _signOut = signOut.SignOutAsync;
        _scanner = scanner ?? ScannerKeyboard.None;
        InitializeComponent();
        FlowDirection = UiCulture.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        DataContext = shell;
        Loaded += async (_, _) => await _shell.RefreshAsync();

        // Preview (tunnelling) events: the window sees every key before the focused control does.
        PreviewTextInput += (_, e) => _scanner.OnText(e.Text, FocusIsInTextInput());
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _scanner.OnEnter(FocusIsInTextInput())) e.Handled = true;
        };
    }

    /// <summary>A text box (also the editable part of a combo box) or a password box has the keyboard focus.</summary>
    private static bool FocusIsInTextInput() => Keyboard.FocusedElement is TextBoxBase or PasswordBox;

    private async void OnSignOut(object sender, RoutedEventArgs e)
    {
        await _signOut();
        await _shell.ResetAsync();
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
