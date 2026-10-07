using System.Globalization;
using System.Windows.Input;
using Catalog.UI.ViewModels;
using Integration.Tests;
using Inventory.Domain.Enums;
using Inventory.UI.Resources;
using Inventory.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using POS.UI.ViewModels;
using UI.Tests.Pos;

namespace UI.Tests.Inventory;

/// <summary>FIX-01c: the warehouses and stock screens on the production-like offline desktop, and the whole back office done through screens only.</summary>
[Collection(nameof(RealDesktop))]
public sealed class InventoryScreensOnRealDesktopTests
{
    private static UiActionRunner Runner(IServiceProvider services)
        => new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

    private static async Task Run(ViewModelBase vm, ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    private static async Task<WarehousesViewModel> AddWarehouseAsync(IServiceProvider services, string name, string code)
    {
        var vm = new WarehousesViewModel(Runner(services));
        await vm.OnNavigatedToAsync();
        (vm.NewName, vm.NewCode) = (name, code);
        await Run(vm, vm.AddCommand);
        Assert.Null(vm.ErrorMessage);
        return vm;
    }

    private static async Task CreateProductAsync(IServiceProvider services, string sku, string name, decimal price, string? barcode = null)
    {
        var categories = new CategoriesViewModel(Runner(services));
        await categories.OnNavigatedToAsync();
        if (categories.Categories.Count == 0)
        {
            categories.NewCategoryName = "Drinks";
            await Run(categories, categories.AddCategoryCommand);
            (categories.NewUnitName, categories.NewUnitAbbreviation) = ("Piece", "pc");
            await Run(categories, categories.AddUnitCommand);
        }

        var products = new ProductsViewModel(Runner(services));
        await products.OnNavigatedToAsync();
        await Run(products, products.NewCommand);
        (products.EditSku, products.EditName, products.EditSalePrice, products.EditBarcode) = (sku, name, Number(price), barcode ?? string.Empty);
        await Run(products, products.SaveCommand);
        Assert.Null(products.ErrorMessage);
    }

    [Fact]
    public async Task Warehouses_are_added_and_listed_with_their_status()
    {
        await using var desktop = await OfflineDesktop.StartAsync();

        var vm = await AddWarehouseAsync(desktop.Services, "Main shop", "MAIN");

        var row = Assert.Single(vm.Warehouses);
        Assert.Equal(("Main shop", InventoryText.Active), (row.Warehouse.Name, row.StatusText));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, InventoryText.WarehouseAdded, "Main shop"), vm.StatusMessage);
        Assert.False(vm.AddCommand.CanExecute(null));   // the form was cleared
    }

    [Fact]
    public async Task Without_a_warehouse_the_stock_screen_says_where_to_add_one()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new StockViewModel(Runner(desktop.Services));

        await vm.OnNavigatedToAsync();

        Assert.True(vm.HasNoWarehouses);
        Assert.Equal(InventoryText.NoStock, vm.ResultInfo);
    }

    [Fact]
    public async Task Stock_is_received_by_sku_or_barcode_corrected_with_a_reason_and_filtered_by_warehouse()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        await CreateProductAsync(desktop.Services, "COLA-1", "Cola", 2.5m, barcode: "4006381333931");
        await AddWarehouseAsync(desktop.Services, "Main shop", "MAIN");
        await AddWarehouseAsync(desktop.Services, "Kiosk", "KIOSK");
        var vm = new StockViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        Assert.Null(vm.ReceiveWarehouse);   // two warehouses: the user chooses

        vm.ReceiveWarehouse = vm.Warehouses.Single(w => w.Name == "Main shop");
        (vm.ReceiveCode, vm.ReceiveQuantity, vm.ReceiveReference) = ("cola-1", Number(10m), "DN-1");
        await Run(vm, vm.ReceiveCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(string.Empty, vm.ReceiveCode);

        vm.ReceiveWarehouse = vm.Warehouses.Single(w => w.Name == "Kiosk");
        (vm.ReceiveCode, vm.ReceiveQuantity) = ("4006381333931", Number(3m));   // scanned barcode
        await Run(vm, vm.ReceiveCommand);
        Assert.Equal([("Cola", "Kiosk", 3m), ("Cola", "Main shop", 10m)], vm.Stock.Select(s => (s.ProductName, s.WarehouseName, s.OnHand)));

        vm.Selected = vm.Stock.Single(s => s.WarehouseName == "Main shop");
        (vm.AdjustBy, vm.AdjustReason) = (Number(-2m), vm.Reasons.Single(r => r.Reason == AdjustmentReason.DamageWrite));
        await Run(vm, vm.AdjustCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(8m, vm.Stock.Single(s => s.WarehouseName == "Main shop").OnHand);

        vm.FilterWarehouse = vm.FilterWarehouses.Single(w => w.Name == "Kiosk");
        await Run(vm, vm.SearchCommand);
        Assert.Equal(["Kiosk"], vm.Stock.Select(s => s.WarehouseName));
    }

    [Fact]
    public async Task Unknown_codes_and_bad_quantities_are_refused_in_plain_words()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        await CreateProductAsync(desktop.Services, "COLA-1", "Cola", 2.5m);
        await AddWarehouseAsync(desktop.Services, "Main shop", "MAIN");
        var vm = new StockViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();

        (vm.ReceiveCode, vm.ReceiveQuantity) = ("NOPE", Number(1m));
        await Run(vm, vm.ReceiveCommand);
        Assert.Contains("NOPE", vm.ErrorMessage);

        (vm.ReceiveCode, vm.ReceiveQuantity) = ("COLA-1", "0");
        await Run(vm, vm.ReceiveCommand);
        Assert.Equal(InventoryText.QuantityInvalid, vm.ErrorMessage);

        vm.ReceiveQuantity = Number(1m);
        await Run(vm, vm.ReceiveCommand);
        vm.Selected = vm.Stock[0];
        vm.AdjustBy = "0";
        await Run(vm, vm.AdjustCommand);
        Assert.Equal(InventoryText.AdjustInvalid, vm.ErrorMessage);
        Assert.Equal(1m, vm.Stock[0].OnHand);
    }

    [Fact]
    public async Task The_whole_back_office_through_screens_only_then_a_sale_lowers_the_stock_shown()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;

        // Products and stock, as a shop owner would set them up
        await CreateProductAsync(services, "WATER-1", "Water", 1.25m, barcode: "5000112637922");
        await AddWarehouseAsync(services, "Main shop", "MAIN");
        var stock = new StockViewModel(Runner(services));
        await stock.OnNavigatedToAsync();
        (stock.ReceiveCode, stock.ReceiveQuantity) = ("WATER-1", Number(12m));
        await Run(stock, stock.ReceiveCommand);
        Assert.Null(stock.ErrorMessage);

        // Sell three at the till by scanning the barcode
        var pos = new PosViewModel(Runner(services), services.GetRequiredService<ICurrentUser>());
        await pos.OnNavigatedToAsync();
        await Run(pos, pos.OpenSessionCommand);
        (pos.ProductCode, pos.QuantityText) = ("5000112637922", Number(3m));
        await Run(pos, pos.AddCommand);
        await Run(pos, pos.CheckoutCommand);
        Assert.Null(pos.ErrorMessage);

        // Back on the stock screen (shown again): 9 left
        await stock.OnNavigatedToAsync();
        Assert.Equal(9m, Assert.Single(stock.Stock).OnHand);
        Assert.Equal(0, desktop.Network.Requests);
    }
}
