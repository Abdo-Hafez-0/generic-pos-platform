using Platform.Presentation.Screens;
using Sales.UI.Resources;
using Sales.UI.ViewModels;
using Sales.UI.Views;

namespace Sales.UI.Screens;

/// <summary>
/// The screens the Sales module offers to the desktop shell (FIX-01c). The history is read-only: Sales declares no capability and reading
/// one's own sales stays possible in every license state (rule 10), so the screen is open to every signed-in user.
/// </summary>
public sealed class SalesScreens : IScreenProvider
{
    public const string Module = "sales";

    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("sales.history", Module, ScreenGroups.Sales, () => SalesText.ScreenTitle,
            typeof(SalesHistoryView), typeof(SalesHistoryViewModel), RequiredCapability: null, Order: 10),
    ];
}
