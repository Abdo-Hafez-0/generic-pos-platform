using Catalog.Application.Security;
using Catalog.UI.Resources;
using Catalog.UI.ViewModels;
using Catalog.UI.Views;
using Platform.Presentation.Screens;

namespace Catalog.UI.Screens;

/// <summary>The screens the Catalog module offers to the desktop shell (FIX-01c).</summary>
public sealed class CatalogScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("catalog.products", CatalogCapabilities.Module, ScreenGroups.Inventory, () => CatalogText.ProductsTitle,
            typeof(ProductsView), typeof(ProductsViewModel), CatalogCapabilities.EditProduct, Order: 0),
        new ScreenDescriptor("catalog.categories", CatalogCapabilities.Module, ScreenGroups.Inventory, () => CatalogText.CategoriesTitle,
            typeof(CategoriesView), typeof(CategoriesViewModel), CatalogCapabilities.ManageCategories, Order: 30),
    ];
}
