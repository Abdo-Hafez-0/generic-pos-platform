using System.Windows;
using System.Windows.Controls;
using Fiscal.UI.Resources;
using Fiscal.UI.ViewModels;
using Microsoft.Win32;

namespace Fiscal.UI.Views;

/// <summary>The electronic receipts screen. All behaviour lives in <see cref="FiscalViewModel"/>; the view only asks where to save the export.</summary>
public partial class FiscalView : UserControl
{
    public FiscalView() => InitializeComponent();

    /// <summary>MISS-11b: the EGS codes for the ETA portal - the file is chosen here, written by the view model.</summary>
    private async void OnExportCodes(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FiscalViewModel vm) return;
        var dialog = new SaveFileDialog { Title = FiscalText.CodesExport, FileName = "egs-codes.csv", Filter = "CSV (*.csv)|*.csv", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) await vm.Codes.ExportAsync(dialog.FileName);
    }

    /// <summary>MISS-11d-3: "Onboard this till..." / "Renew..." opens the onboarding window; once done the summary and documents are read again.</summary>
    private async void OnOnboardTill(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FiscalViewModel vm) return;
        var window = new OnboardingWindow(vm.CreateOnboarding()) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await vm.OnboardedAsync();
    }

    /// <summary>MISS-11c-3: "Register this till..." opens the registration window; after a save the summary is read again.</summary>
    private async void OnRegisterTill(object sender, RoutedEventArgs e)
    {
        if (DataContext is not FiscalViewModel vm) return;
        var window = new TillRegistrationWindow(vm.CreateRegistration()) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await vm.RegisteredAsync();
    }
}
