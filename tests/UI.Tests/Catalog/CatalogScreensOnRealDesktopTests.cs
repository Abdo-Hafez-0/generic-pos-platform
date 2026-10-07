using System.Globalization;
using System.Windows.Input;
using Catalog.Domain.Enums;
using Catalog.UI.Resources;
using Catalog.UI.ViewModels;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using UI.Tests.Pos;

namespace UI.Tests.Catalog;

/// <summary>FIX-01c: the products and the categories-and-units screens on the production-like offline desktop (real Catalog handlers and SQLite).</summary>
[Collection(nameof(RealDesktop))]
public sealed class CatalogScreensOnRealDesktopTests
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

    private static string Money(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    private static async Task<ProductsViewModel> ReadyProductsScreenAsync(IServiceProvider services)
    {
        var categories = new CategoriesViewModel(Runner(services));
        await categories.OnNavigatedToAsync();
        categories.NewCategoryName = "Drinks";
        await Run(categories, categories.AddCategoryCommand);
        categories.NewUnitName = "Piece";
        categories.NewUnitAbbreviation = "pc";
        await Run(categories, categories.AddUnitCommand);
        Assert.Null(categories.ErrorMessage);

        var products = new ProductsViewModel(Runner(services));
        await products.OnNavigatedToAsync();
        return products;
    }

    [Fact]
    public async Task Categories_and_units_are_added_and_listed()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new CategoriesViewModel(Runner(desktop.Services));
        await vm.OnNavigatedToAsync();
        Assert.False(vm.AddCategoryCommand.CanExecute(null));

        vm.NewCategoryName = "Drinks";
        await Run(vm, vm.AddCategoryCommand);
        Assert.Equal(["Drinks"], vm.Categories.Select(c => c.Name));
        Assert.Equal(string.Empty, vm.NewCategoryName);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, CatalogText.CategoryAdded, "Drinks"), vm.StatusMessage);

        vm.NewUnitName = "Kilogram";
        vm.NewUnitAbbreviation = "kg";
        await Run(vm, vm.AddUnitCommand);
        Assert.Equal(["Kilogram"], vm.Units.Select(u => u.Name));
        Assert.Equal(string.Empty, vm.NewUnitAbbreviation);
    }

    [Fact]
    public async Task Without_categories_and_units_the_products_screen_says_where_to_add_them()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = new ProductsViewModel(Runner(desktop.Services));

        await vm.OnNavigatedToAsync();

        Assert.True(vm.NeedsCategoriesAndUnits);
        Assert.Equal(CatalogText.NoProducts, vm.ResultInfo);
    }

    [Fact]
    public async Task A_product_is_created_with_a_barcode_found_by_it_edited_and_deactivated()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = await ReadyProductsScreenAsync(desktop.Services);
        Assert.False(vm.NeedsCategoriesAndUnits);
        Assert.True(vm.CanSeeCost);   // the administrator holds catalog.cost.view

        // create
        await Run(vm, vm.NewCommand);
        Assert.True(vm.IsNewProduct);
        Assert.Equal("Drinks", vm.EditCategory?.Name);   // the only category and unit are preselected
        vm.EditSku = "cola-1";
        vm.EditName = "Cola 330ml";
        vm.EditSalePrice = Money(2.5m);
        vm.EditCostPrice = Money(1.1m);
        vm.EditBarcode = "4006381333931";
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.False(vm.IsEditorOpen);
        var row = Assert.Single(vm.Products);
        Assert.Equal(("COLA-1", 2.5m, 1.1m, "4006381333931", CatalogText.StatusActive), (row.Product.Sku, row.Product.SalePrice, row.Product.CostPrice, row.BarcodesText, row.StatusText));
        Assert.Equal(BarcodeFormat.EAN13, row.Product.Barcodes[0].Format);

        // find it by its barcode
        vm.SearchText = "4006381333931";
        await Run(vm, vm.SearchCommand);
        Assert.Single(vm.Products);

        // edit the price
        vm.Selected = vm.Products[0];
        await Run(vm, vm.EditCommand);
        Assert.False(vm.IsNewProduct);
        vm.EditSalePrice = Money(2.75m);
        await Run(vm, vm.SaveCommand);
        Assert.Equal(2.75m, vm.Products[0].Product.SalePrice);
        Assert.Equal(1.1m, vm.Products[0].Product.CostPrice);

        // deactivate: gone from the active list, still there with "show inactive"
        vm.SearchText = string.Empty;
        await Run(vm, vm.SearchCommand);
        vm.Selected = vm.Products[0];
        await Run(vm, vm.DeactivateCommand);
        Assert.Empty(vm.Products);
        vm.IncludeInactive = true;
        await Run(vm, vm.SearchCommand);
        Assert.Equal(CatalogText.StatusInactive, Assert.Single(vm.Products).StatusText);
    }

    [Fact]
    public async Task Bad_input_and_a_duplicate_sku_are_refused_in_plain_words_and_the_editor_stays_open()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = await ReadyProductsScreenAsync(desktop.Services);
        await Run(vm, vm.NewCommand);
        vm.EditSku = "COLA-1";
        vm.EditName = "Cola";
        vm.EditSalePrice = "two";
        await Run(vm, vm.SaveCommand);
        Assert.Equal(CatalogText.PriceInvalid, vm.ErrorMessage);
        Assert.True(vm.IsEditorOpen);

        vm.EditSalePrice = Money(2m);
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);

        await Run(vm, vm.NewCommand);
        vm.EditSku = "COLA-1";
        vm.EditName = "Another cola";
        vm.EditSalePrice = Money(2m);
        await Run(vm, vm.SaveCommand);
        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain("Exception", vm.ErrorMessage);
        Assert.True(vm.IsEditorOpen);
        Assert.Single(vm.Products);
    }

    [Fact]
    public async Task A_barcode_that_belongs_to_another_product_is_refused_and_names_that_product()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = await ReadyProductsScreenAsync(desktop.Services);
        await Run(vm, vm.NewCommand);
        (vm.EditSku, vm.EditName, vm.EditSalePrice, vm.EditBarcode) = ("COLA-1", "Cola", Money(2m), "123456");
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);

        await Run(vm, vm.NewCommand);
        (vm.EditSku, vm.EditName, vm.EditSalePrice, vm.EditBarcode) = ("WATER-1", "Water", Money(1m), "123456");
        await Run(vm, vm.SaveCommand);

        Assert.Contains("COLA-1", vm.ErrorMessage);
        Assert.True(vm.IsEditorOpen);
        Assert.False(vm.IsNewProduct);   // the product itself was created: saving again edits it instead of creating a duplicate SKU
        vm.EditBarcode = "654321";
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(2, vm.Products.Count);
    }

    [Fact]
    public async Task A_product_created_here_is_sold_on_the_POS_screen()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var vm = await ReadyProductsScreenAsync(desktop.Services);
        await Run(vm, vm.NewCommand);
        vm.EditSku = "WATER-1";
        vm.EditName = "Water";
        vm.EditSalePrice = Money(1.25m);
        vm.EditBarcode = "WATER-BC";
        await Run(vm, vm.SaveCommand);
        Assert.Null(vm.ErrorMessage);
        var product = vm.Products[0].Product;

        // stock and a warehouse come from Inventory (its screens are tested in their own suite); here through the contracts directly
        var shop = await FailureTestKit.CreateShopAsync(desktop.Services, sku: "OTHER-1", name: "Other");
        using (var scope = desktop.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<Inventory.Contracts.Interfaces.IStockReceiptService>()
                .ReceiveStockAsync(product.Id, shop.WarehouseId, 5m, "test")).IsSuccess);

        var pos = new POS.UI.ViewModels.PosViewModel(Runner(desktop.Services), desktop.Services.GetRequiredService<Platform.Application.Abstractions.Authorization.ICurrentUser>());
        await pos.OnNavigatedToAsync();
        await Run(pos, pos.OpenSessionCommand);
        pos.ProductCode = "WATER-BC";   // by the barcode added on the products screen
        await Run(pos, pos.AddCommand);
        Assert.Null(pos.ErrorMessage);
        Assert.Equal(1.25m, pos.Total);
    }

    [Theory]
    [InlineData("4006381333931", BarcodeFormat.EAN13)]
    [InlineData("96385074", BarcodeFormat.EAN8)]
    [InlineData("036000291452", BarcodeFormat.UPC)]
    [InlineData("WATER-BC", BarcodeFormat.Code128)]
    [InlineData("12345", BarcodeFormat.Code128)]
    public void The_barcode_format_follows_the_shape_of_the_value(string value, BarcodeFormat expected)
        => Assert.Equal(expected, ProductsViewModel.GuessFormat(value));
}
