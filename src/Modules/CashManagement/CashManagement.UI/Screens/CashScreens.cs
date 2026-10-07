using CashManagement.Application.Security;
using CashManagement.UI.Resources;
using CashManagement.UI.ViewModels;
using CashManagement.UI.Views;
using Platform.Presentation.Screens;

namespace CashManagement.UI.Screens;

/// <summary>The screens the CashManagement module offers to the desktop shell (FIX-01d).</summary>
public sealed class CashScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("cash.drawer", CashManagementCapabilities.Module, ScreenGroups.Finance, () => CashText.ScreenTitle,
            typeof(CashDrawerView), typeof(CashDrawerViewModel), CashManagementCapabilities.ManageSessions, Order: 0),
    ];
}
