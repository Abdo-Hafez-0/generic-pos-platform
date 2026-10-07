using Suppliers.Application.Security;
using Suppliers.UI.Resources;
using Suppliers.UI.ViewModels;
using Suppliers.UI.Views;
using Platform.Presentation.Screens;

namespace Suppliers.UI.Screens;

/// <summary>
/// The screens the Suppliers module offers to the desktop shell (FIX-01d). Shown to holders of suppliers.supplier.manage (the only Suppliers
/// capability); the handlers authorize every change.
/// </summary>
public sealed class SuppliersScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("suppliers.list", SuppliersCapabilities.Module, ScreenGroups.People, () => SuppliersText.ScreenTitle,
            typeof(SuppliersView), typeof(SuppliersViewModel), SuppliersCapabilities.ManageSuppliers, Order: 10),
    ];
}
