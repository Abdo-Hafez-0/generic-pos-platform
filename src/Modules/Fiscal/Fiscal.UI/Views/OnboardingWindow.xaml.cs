using System.Windows;
using Fiscal.UI.ViewModels;

namespace Fiscal.UI.Views;

/// <summary>
/// MISS-11d-3: the till's onboarding (or renewal) window. The behaviour lives in <see cref="OnboardingViewModel"/>; the window stays open
/// after the run so its ticks and words can be read, and reports whether the till was onboarded when it closes.
/// </summary>
public partial class OnboardingWindow : Window
{
    private bool _done;

    public OnboardingWindow(OnboardingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        FlowDirection = System.Globalization.CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Platform.Presentation.Screens.OwnedWindows.Follow(this);   // the dark title bar in the dark theme
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm && await vm.RunAsync()) _done = true;
    }

    private void OnClose(object sender, RoutedEventArgs e) => DialogResult = _done;
}
