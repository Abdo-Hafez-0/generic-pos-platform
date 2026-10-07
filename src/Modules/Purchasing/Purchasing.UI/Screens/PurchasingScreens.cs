using Platform.Presentation.Screens;
using Purchasing.Application.Security;
using Purchasing.UI.Resources;
using Purchasing.UI.ViewModels;
using Purchasing.UI.Views;

namespace Purchasing.UI.Screens;

/// <summary>The screens the Purchasing module offers to the desktop shell (FIX-01d).</summary>
public sealed class PurchasingScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("purchasing.orders", PurchasingCapabilities.Module, ScreenGroups.Purchasing, () => PurchasingText.ScreenTitle,
            typeof(PurchaseOrdersView), typeof(PurchaseOrdersViewModel), PurchasingCapabilities.EditOrder, Order: 0),
    ];
}
