using System.Windows;
using Fiscal.UI.ViewModels;

namespace Fiscal.UI.Views;

/// <summary>
/// MISS-11c-3: the till's registration window. The behaviour lives in <see cref="TillRegistrationViewModel"/>; the view only hands it the
/// password boxes' contents when Save is pressed (a password box cannot be bound) and closes once saved.
/// </summary>
public partial class TillRegistrationWindow : Window
{
    public TillRegistrationWindow(TillRegistrationViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        FlowDirection = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Platform.Presentation.Screens.OwnedWindows.Follow(this);   // the dark title bar in the dark theme
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TillRegistrationViewModel vm) return;
        if (await vm.SaveAsync(ClientSecretBox.Password, PresharedKeyBox.Password))
        {
            ClientSecretBox.Clear();
            PresharedKeyBox.Clear();
            DialogResult = true;
        }
    }
}
