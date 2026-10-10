using System.Windows;
using System.Windows.Controls;
using Client.Desktop.Resources;
using Microsoft.Win32;

namespace Client.Desktop.Screens.Backup;

/// <summary>
/// The backup screen. All behaviour lives in <see cref="BackupViewModel"/>; this file only opens the Windows dialogs for choosing a folder
/// or a backup file and hands the chosen path to the view model.
/// </summary>
public partial class BackupView : UserControl
{
    public BackupView() => InitializeComponent();

    private BackupViewModel? ViewModel => DataContext as BackupViewModel;

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm) return;
        var dialog = new OpenFolderDialog { Title = BackupText.ChooseFolder, InitialDirectory = vm.Folder ?? string.Empty };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            vm.Folder = dialog.FolderName;
    }

    private void OnRestoreFromFile(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } vm || !vm.RestoreFromFileCommand.CanExecute("x")) return;
        var dialog = new OpenFileDialog { Title = BackupText.ChooseFile, Filter = $"{BackupText.BackupFiles} (*.db)|*.db", CheckFileExists = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            vm.RestoreFromFileCommand.Execute(dialog.FileName);
    }
}
