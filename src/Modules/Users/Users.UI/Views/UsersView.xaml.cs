using System.Windows;
using System.Windows.Controls;
using Users.UI.ViewModels;

namespace Users.UI.Views;

/// <summary>
/// Users. All behaviour lives in <see cref="UsersViewModel"/>; the code-behind only hands a password from a password box to a command at
/// the moment of use and clears the box straight away (passwords are never bound).
/// </summary>
public partial class UsersView : UserControl
{
    public UsersView() => InitializeComponent();

    private UsersViewModel? ViewModel => DataContext as UsersViewModel;

    private void OnCreate(object sender, RoutedEventArgs e) => Hand(NewPasswordBox, ViewModel?.CreateCommand);

    private void OnResetPassword(object sender, RoutedEventArgs e) => Hand(ResetPasswordBox, ViewModel?.ResetPasswordCommand);

    private static void Hand(PasswordBox box, System.Windows.Input.ICommand? command)
    {
        var password = box.Password;
        box.Clear();
        if (command?.CanExecute(password) == true) command.Execute(password);
    }
}
