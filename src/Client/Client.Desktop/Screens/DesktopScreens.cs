using Client.Desktop.Resources;
using Client.Desktop.Screens.Licensing;
using Client.Licensing.Application;
using Platform.Presentation.Screens;

namespace Client.Desktop.Screens;

/// <summary>
/// Screens of the desktop's own client components (not of a business module). The license screen needs licensing.manage, which is
/// available in every license state, so an unlicensed or expired installation can always be activated or renewed.
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
    ];
}
