using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Platform.Core.Results;
using Users.Application.Security;

namespace Client.Desktop;

/// <summary>
/// The start screen: first-run setup, sign-in, or replacing a temporary password. DialogResult is true once someone is signed in; closing it
/// any other way means "nobody signed in" and the application does not open its shell.
///
/// Glue only (Stage 11 decision): the flow lives in <see cref="InteractiveSignInService"/>, which is unit-tested; this window maps its stages to
/// panels and its errors to text. Each attempt runs in its own DI scope (the services read the database through scoped contexts).
/// Passwords are taken from the PasswordBox controls when used and cleared afterwards; they are never bound, kept or logged.
/// </summary>
public partial class SignInWindow : Window
{
    private enum Mode { FirstRun, SignIn, ChangePassword }

    private readonly IServiceScopeFactory _scopes;
    private Mode _mode = Mode.SignIn;
    private string? _pendingUsername;

    public SignInWindow(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
        InitializeComponent();
        Loaded += async (_, _) => await ShowCurrentStageAsync();
    }

    private async Task ShowCurrentStageAsync()
    {
        var status = await Run(s => s.GetStatusAsync());
        switch (status.Stage)
        {
            case SignInStage.SignedIn:
                DialogResult = true;
                return;
            case SignInStage.FirstRunSetup:
                SetMode(Mode.FirstRun);
                return;
            default:
                SetMode(Mode.SignIn);
                return;
        }
    }

    private void SetMode(Mode mode)
    {
        _mode = mode;
        ErrorText.Text = string.Empty;
        ClearPasswords();

        IdentityPanel.Visibility = mode == Mode.ChangePassword ? Visibility.Collapsed : Visibility.Visible;
        DisplayNamePanel.Visibility = mode == Mode.FirstRun ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabel.Visibility = PasswordBox.Visibility = mode == Mode.FirstRun ? Visibility.Collapsed : Visibility.Visible;
        NewPasswordPanel.Visibility = mode == Mode.SignIn ? Visibility.Collapsed : Visibility.Visible;

        (Heading.Text, Explanation.Text, PrimaryButton.Content) = mode switch
        {
            Mode.FirstRun => ("Welcome - create the administrator",
                "No user exists yet. The account you create now holds every permission and can create the other users. Keep its password safe.",
                "Create and sign in"),
            Mode.ChangePassword => ("Choose your own password",
                "Your password was set by an administrator. Replace it with one only you know before you continue.",
                "Change and sign in"),
            _ => ("Sign in", string.Empty, "Sign in")
        };

        (mode == Mode.ChangePassword ? (UIElement)NewPasswordBox : UsernameBox).Focus();
    }

    private async void OnPrimary(object sender, RoutedEventArgs e)
    {
        PrimaryButton.IsEnabled = false;
        try
        {
            var result = _mode switch
            {
                Mode.FirstRun => await Run(s => s.CompleteFirstRunAsync(UsernameBox.Text, DisplayNameBox.Text, NewPasswordBox.Password, ConfirmPasswordBox.Password)),
                Mode.ChangePassword => await Run(s => s.ChangePasswordAndSignInAsync(_pendingUsername!, PasswordBox.Password, NewPasswordBox.Password, ConfirmPasswordBox.Password)),
                _ => await Run(s => s.SignInAsync(UsernameBox.Text, PasswordBox.Password))
            };

            if (result.IsSuccess)
            {
                ClearPasswords();
                DialogResult = true;
                return;
            }

            if (_mode == Mode.SignIn && result.Error.Code == UsersSecurityErrors.PasswordChangeRequiredCode)
            {
                // keep the (correct) temporary password: the change re-proves it
                _pendingUsername = UsernameBox.Text;
                var temporary = PasswordBox.Password;
                SetMode(Mode.ChangePassword);
                PasswordBox.Password = temporary;
                return;
            }

            ShowError(result.Error);
        }
        finally
        {
            PrimaryButton.IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        ClearPasswords();
        DialogResult = false;
    }

    private void ShowError(Error error)
    {
        ErrorText.Text = error.Description;
        if (_mode != Mode.ChangePassword) PasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }

    private void ClearPasswords()
    {
        PasswordBox.Clear();
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
    }

    private async Task<T> Run<T>(Func<InteractiveSignInService, Task<T>> action)
    {
        using var scope = _scopes.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<InteractiveSignInService>());
    }
}
