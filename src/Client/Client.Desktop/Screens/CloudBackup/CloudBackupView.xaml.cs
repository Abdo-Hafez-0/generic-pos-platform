using System.IO;
using System.Windows;
using System.Windows.Controls;
using Client.Desktop.Resources;
using Microsoft.Win32;

namespace Client.Desktop.Screens.CloudBackup;

/// <summary>
/// The cloud backup screen. All behaviour lives in <see cref="CloudBackupViewModel"/>; this file only puts the recovery request on the
/// clipboard or into a file the person chooses (MISS-04f).
/// </summary>
public partial class CloudBackupView : UserControl
{
    public CloudBackupView() => InitializeComponent();

    private CloudBackupViewModel? ViewModel => DataContext as CloudBackupViewModel;

    private void OnCopyRequest(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsRecoveryRequestShown: true } vm) return;
        Clipboard.SetText(vm.RecoveryRequestText);
        vm.RequestCopied();
    }

    private void OnSaveRequest(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { IsRecoveryRequestShown: true } vm) return;
        var dialog = new SaveFileDialog
        {
            Title = CloudBackupText.SaveRequestTitle,
            Filter = $"{CloudBackupText.TextFiles} (*.txt)|*.txt",
            FileName = "recovery-request.txt",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        File.WriteAllText(dialog.FileName, vm.RecoveryRequestText);
        vm.RequestSaved(dialog.FileName);
    }
}
