using Fiscal.Application.Security;
using Fiscal.UI.Resources;
using Fiscal.UI.ViewModels;
using Fiscal.UI.Views;
using Platform.Presentation.Screens;

namespace Fiscal.UI.Screens;

/// <summary>The screens the Fiscal module offers to the desktop shell (MISS-11a).</summary>
public sealed class FiscalScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("fiscal.receipts", FiscalCapabilities.Module, ScreenGroups.Administration, () => FiscalText.ScreenTitle,
            typeof(FiscalView), typeof(FiscalViewModel), FiscalCapabilities.Manage, Order: 70),
    ];
}
