using System.Globalization;
using System.Windows.Input;
using CashManagement.UI.ViewModels;
using Catalog.UI.ViewModels;
using Customers.UI.ViewModels;
using Integration.Tests;
using Inventory.Domain.Enums;
using Inventory.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Pricing.UI.ViewModels;
using Purchasing.UI.ViewModels;
using Suppliers.Application.Commands;
using Suppliers.UI.ViewModels;
using UI.Tests.Pos;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests;

/// <summary>
/// FIX-06 through the screens of every hosted module that writes: when the database refuses the save (a failure injected INSIDE SQLite, as a
/// full disk, a lock or a corrupt page would), the screen shows a plain sentence - no exception text, no SQL, no table names - and nothing
/// was written. (POS: PosScreenOnRealDesktopTests; Payments has no screen: ContractFailureTests.)
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class HostedScreensFailureTests
{
    private static UiActionRunner Runner(IServiceProvider services)
        => new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

    private static string Number(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static async Task<T> ShownAsync<T>(T vm) where T : ViewModelBase
    {
        if (vm is INavigationAware aware) await aware.OnNavigatedToAsync();
        return vm;
    }

    private static void AssertPlain(ViewModelBase vm)
    {
        Assert.False(string.IsNullOrWhiteSpace(vm.ErrorMessage), "the screen said nothing");
        foreach (var technical in new[] { "SQLite", "constraint", "Exception", "   at ", "_" })
            Assert.DoesNotContain(technical, vm.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SavingFailsPlainlyAndWritesNothingAsync(OfflineDesktop desktop, string table, string operation, ViewModelBase vm, ICommand command)
    {
        var before = await CountAsync(desktop.Host, table);
        await using (operation == "INSERT" ? await FailInsertsAsync(desktop.Host, table) : await FailUpdatesAsync(desktop.Host, table))
            await Run(vm, command);

        AssertPlain(vm);
        Assert.Equal(before, await CountAsync(desktop.Host, table));
    }

    [Fact]
    public async Task Catalog_a_new_product_and_a_new_category()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        await CreateShopAsync(desktop.Services);   // a category and a unit to choose

        var products = await ShownAsync(new ProductsViewModel(Runner(desktop.Services)));
        await Run(products, products.NewCommand);
        (products.EditSku, products.EditName, products.EditSalePrice) = ("WATER-1", "Water", Number(1.25m));
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "cat_Products", "INSERT", products, products.SaveCommand);
        Assert.True(products.IsEditorOpen);        // what was typed is still there to save again

        var categories = await ShownAsync(new CategoriesViewModel(Runner(desktop.Services)));
        categories.NewCategoryName = "Snacks";
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "cat_Categories", "INSERT", categories, categories.AddCategoryCommand);
    }

    [Fact]
    public async Task Inventory_a_new_warehouse_a_stock_receipt_and_a_stock_correction()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, stock: 10m);

        var warehouses = await ShownAsync(new WarehousesViewModel(Runner(desktop.Services)));
        (warehouses.NewName, warehouses.NewCode) = ("Kiosk", "KIOSK");
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "inv_Warehouses", "INSERT", warehouses, warehouses.AddCommand);

        var stock = await ShownAsync(new StockViewModel(Runner(desktop.Services)));
        (stock.ReceiveCode, stock.ReceiveQuantity) = (shop.Sku, Number(5m));
        stock.ReceiveWarehouse ??= stock.Warehouses.Single();
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "inv_StockMovements", "INSERT", stock, stock.ReceiveCommand);

        await stock.OnNavigatedToAsync();
        stock.Selected = stock.Stock.Single();
        (stock.AdjustBy, stock.AdjustReason) = (Number(-1m), stock.Reasons.Single(r => r.Reason == AdjustmentReason.DamageWrite));
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "inv_StockAdjustments", "INSERT", stock, stock.AdjustCommand);
        Assert.Equal(10m, await OnHandAsync(desktop.Host, shop.ProductId));
    }

    [Fact]
    public async Task Customers_and_suppliers_a_new_record()
    {
        await using var desktop = await OfflineDesktop.StartAsync();

        var customers = await ShownAsync(new CustomersViewModel(Runner(desktop.Services)));
        await Run(customers, customers.NewCommand);
        (customers.EditCode, customers.EditName) = ("C-001", "Corner Cafe");
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "cus_Customers", "INSERT", customers, customers.SaveCommand);

        var suppliers = await ShownAsync(new SuppliersViewModel(Runner(desktop.Services)));
        await Run(suppliers, suppliers.NewCommand);
        (suppliers.EditCode, suppliers.EditName) = ("S-001", "Fresh Water Co");
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "sup_Suppliers", "INSERT", suppliers, suppliers.SaveCommand);
    }

    [Fact]
    public async Task Pricing_a_new_price_list()
    {
        await using var desktop = await OfflineDesktop.StartAsync();

        var prices = await ShownAsync(new PricesViewModel(Runner(desktop.Services)));
        (prices.NewListCode, prices.NewListName, prices.NewListDefault) = ("RETAIL", "Retail", true);
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "pri_PriceLists", "INSERT", prices, prices.AddListCommand);
    }

    [Fact]
    public async Task Purchasing_a_new_order_and_a_receipt_whose_stock_cannot_be_saved()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, stock: 10m);
        using (var scope = desktop.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("S-001", "Fresh Water Co"))).IsSuccess);

        var orders = await ShownAsync(new PurchaseOrdersViewModel(Runner(desktop.Services)));
        orders.SupplierText = "fresh";
        await Run(orders, orders.FindSupplierCommand);
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "pur_PurchaseOrders", "INSERT", orders, orders.CreateCommand);

        // the database works again: create, add a line, submit; then the receipt's stock cannot be saved
        await Run(orders, orders.CreateCommand);
        Assert.Null(orders.ErrorMessage);
        (orders.LineCode, orders.LineQuantity) = (shop.Sku, Number(12m));
        await Run(orders, orders.AddLineCommand);
        await Run(orders, orders.SubmitCommand);
        Assert.Null(orders.ErrorMessage);

        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "inv_StockMovements", "INSERT", orders, orders.ReceiveCommand);
        Assert.Equal(10m, await OnHandAsync(desktop.Host, shop.ProductId));
    }

    [Fact]
    public async Task Cash_drawer_opening_a_shift_and_a_pay_out()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var drawer = await ShownAsync(new CashDrawerViewModel(Runner(desktop.Services), desktop.Services.GetRequiredService<ICurrentUser>()));

        drawer.OpeningFloat = Number(100m);
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "cash_Sessions", "INSERT", drawer, drawer.OpenCommand);
        Assert.True(drawer.HasNoOpenShift);

        await Run(drawer, drawer.OpenCommand);
        (drawer.Amount, drawer.Reason) = (Number(20m), "milk");
        await SavingFailsPlainlyAndWritesNothingAsync(desktop, "cash_Movements", "INSERT", drawer, drawer.RecordCommand);
        Assert.Equal(100m, drawer.Session!.Balance);
    }
}
