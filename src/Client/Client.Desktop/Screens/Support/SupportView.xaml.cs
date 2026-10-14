using System.Windows;
using System.Windows.Controls;
using Client.Desktop.Resources;
using Microsoft.Win32;

namespace Client.Desktop.Screens.Support;

/// <summary>The support screen. All behaviour lives in <see cref="SupportViewModel"/>; this file only asks where to save the support file.</summary>
public partial class SupportView : UserControl
{
    public SupportView() => InitializeComponent();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SupportViewModel vm) return;
        var dialog = new SaveFileDialog
        {
            Title = SupportText.SaveTitle,
            Filter = $"{SupportText.ZipFiles} (*.zip)|*.zip",
            FileName = $"genericpos-support-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            vm.SaveCommand.Execute(dialog.FileName);
    }
}
