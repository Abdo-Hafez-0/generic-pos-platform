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
    private readonly DesktopSession _session;
    private readonly ScannerKeyboard _scanner;
    private bool _replaced;

    public MainWindow(
        ILogger<MainWindow> logger,
        ShellViewModel shell,
        DesktopSession session,
        ScannerKeyboard? scanner = null)
    {
        _logger = logger;
        _shell = shell;
        _session = session;
        _scanner = scanner ?? ScannerKeyboard.None;
        InitializeComponent();
        FlowDirection = UiCulture.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        DataContext = shell;
        Loaded += async (_, _) => await _shell.RefreshAsync();

        // FIX-13b: the signed-in user's own language; choosing another reopens the shell in it
        LanguageBox.ItemsSource = UiLanguages.All;
        LanguageBox.SelectedItem = UiLanguages.Find(UiCulture.Current.Name);
        LanguageBox.SelectionChanged += OnLanguageChosen;

        // Preview (tunnelling) events: the window sees every key before the focused control does.
        PreviewTextInput += (_, e) => _scanner.OnText(e.Text, FocusIsInTextInput());
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _scanner.OnEnter(FocusIsInTextInput())) e.Handled = true;
        };
    }

    /// <summary>A text box (also the editable part of a combo box) or a password box has the keyboard focus.</summary>
    private static bool FocusIsInTextInput() => Keyboard.FocusedElement is TextBoxBase or PasswordBox;

    private async void OnSignOut(object sender, RoutedEventArgs e) => await _session.SignOutAsync(this);

    private async void OnLanguageChosen(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not UiLanguage chosen || UiLanguages.Find(UiCulture.Current.Name) == chosen) return;

        var saved = await _session.ChangeMyLanguageAsync(chosen.Code);
        if (saved.IsFailure)
        {
            _logger.LogWarning("The language could not be saved: {Error}", saved.Error);
            LanguageBox.SelectedItem = UiLanguages.Find(UiCulture.Current.Name);
        }
    }

    /// <summary>Closes this window because a new shell window (another user or language) took its place; the application keeps running.</summary>
    public void CloseForReplacement()
    {
        _replaced = true;
        Close();
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        _logger.LogInformation("Main window rendered. Application is ready.");
    }

    protected override void OnClosed(EventArgs e)
    {
        _logger.LogInformation(_replaced ? "Main window replaced (sign-in or language change)." : "Main window closed. Application shutting down.");
        base.OnClosed(e);
    }
}
