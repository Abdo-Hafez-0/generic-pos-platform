using Client.Backup.Application;
using Client.Desktop.Resources;
using Client.Desktop.Screens.Backup;
using Client.Desktop.Screens.CloudBackup;
using CloudBackup.Application;
using Client.Desktop.Screens.Licensing;
using Client.Licensing.Application;
using Platform.Presentation.Screens;

namespace Client.Desktop.Screens;

/// <summary>
/// Screens of the desktop's own client components (not of a business module). The license screen needs licensing.manage, which is
/// available in every license state, so an unlicensed or expired installation can always be activated or renewed. The backup screen
/// (MISS-04d) needs backup.create and no license either: a shop can always back up and restore its own data (rule 10). The cloud backup
/// screen (MISS-04e3) opens with backup.cloud.restore, which needs no license either; setting cloud backup up inside it needs the license.
/// </summary>
public sealed class DesktopScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor(
            Id: "licensing.license",
            Module: LicensingCapabilities.Module,
            Group: ScreenGroups.Administration,
            Title: () => LicenseText.ScreenTitle,
            ViewType: typeof(LicenseView),
            ViewModelType: typeof(LicenseViewModel),
            RequiredCapability: LicensingCapabilities.Manage,
            Order: 90),
        new ScreenDescriptor(
            Id: "backup.backups",
            Module: BackupCapabilities.Module,
            Group: ScreenGroups.Administration,
            Title: () => BackupText.ScreenTitle,
            ViewType: typeof(BackupView),
            ViewModelType: typeof(BackupViewModel),
            RequiredCapability: BackupCapabilities.Create,
            Order: 80),
        new ScreenDescriptor(
            Id: "backup.cloud",
            Module: CloudBackupCapabilities.Module,
            Group: ScreenGroups.Administration,
            Title: () => CloudBackupText.ScreenTitle,
            ViewType: typeof(CloudBackupView),
            ViewModelType: typeof(CloudBackupViewModel),
            RequiredCapability: CloudBackupCapabilities.Restore,
            Order: 81),
    ];
}
