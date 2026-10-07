using Inventory.Application.Security;
using Inventory.UI.Resources;
using Inventory.UI.ViewModels;
using Inventory.UI.Views;
using Platform.Presentation.Screens;

namespace Inventory.UI.Screens;

/// <summary>The screens the Inventory module offers to the desktop shell (FIX-01c).</summary>
public sealed class InventoryScreens : IScreenProvider
{
    public IReadOnlyCollection<ScreenDescriptor> GetScreens() =>
    [
        new ScreenDescriptor("inventory.stock", InventoryCapabilities.Module, ScreenGroups.Inventory, () => InventoryText.StockTitle,
            typeof(StockView), typeof(StockViewModel), InventoryCapabilities.ReceiveStock, Order: 10),
        new ScreenDescriptor("inventory.warehouses", InventoryCapabilities.Module, ScreenGroups.Inventory, () => InventoryText.WarehousesTitle,
            typeof(WarehousesView), typeof(WarehousesViewModel), InventoryCapabilities.ManageLocations, Order: 20),
    ];
}
