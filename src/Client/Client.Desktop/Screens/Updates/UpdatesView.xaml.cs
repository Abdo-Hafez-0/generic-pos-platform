using System.Windows;
using System.Windows.Controls;
using Client.Desktop.Resources;
using Microsoft.Win32;
using PackageFormat = Updates.Package.PackageFormat;

namespace Client.Desktop.Screens.Updates;

/// <summary>The updates screen. All behaviour lives in <see cref="UpdatesViewModel"/>; this file only asks which update file to install.</summary>
public partial class UpdatesView : UserControl
{
    public UpdatesView() => InitializeComponent();

    private void OnInstallFromFile(object sender, RoutedEventArgs e)
    {
        if (DataContext is not UpdatesViewModel vm) return;
        var dialog = new OpenFileDialog
        {
            Title = UpdatesText.OpenTitle,
            Filter = $"{UpdatesText.UpdateFiles} (*{PackageFormat.Extension})|*{PackageFormat.Extension}",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            vm.InstallFromFileCommand.Execute(dialog.FileName);
    }
}
