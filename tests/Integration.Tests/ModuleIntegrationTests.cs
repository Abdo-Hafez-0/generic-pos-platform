using Catalog.Application.Commands;
using Catalog.Domain.Enums;
using Inventory.Application.Commands;
using Inventory.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using POS.Contracts.Interfaces;
using Reporting.Contracts.Interfaces;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 13: cross-boundary behavior between the modules, on the production-like offline desktop (licensed, signed in, every module,
/// real SQLite). Each test crosses one boundary through the contracts only and checks what the OTHER side did - or did not do.
/// The POS -> Sales -> Payments -> Inventory boundary is VerticalSliceOwnershipTests; Users/Authorization is SecurityIntegrationTests;
/// licensing is LicensingIntegrationTests; updates are UpdateIntegrationTests; the cloud is the Stage 12 outage suites.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ModuleIntegrationTests
{
    private static async Task<T> InScope<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> action)
    {
        using var scope = services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    private static async Task<Dictionary<string, string>> TablesAsync(IntegrationHost host, string prefix)
        => (await VerticalSliceOwnershipTests.TableContentsAsync(host)).Where(t => t.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary();

    [Fact]
    public async Task CatalogToInventory_StockCanOnlyBeHeldForAProductCatalogKnows()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var inventoryBefore = await TablesAsync(desktop.Host, "inv_");

        var unknown = await InScope(desktop.Services, sp => sp.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(Guid.NewGuid(), shop.WarehouseId, 5m)));

        Assert.Equal("Inventory.AddStock.ProductNotFound", unknown.Error.Code);
        Assert.Equal(inventoryBefore, await TablesAsync(desktop.Host, "inv_"));
    }

    [Fact]
    public async Task CatalogToPos_ABarcodeAssignedInCatalogSellsInPos_AndADeactivatedProductCannotBeAdded_WhileTheCartKeepsItsSnapshot()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var barcode = await InScope(desktop.Services, sp => sp.GetRequiredService<AssignBarcodeCommandHandler>().HandleAsync(new AssignBarcodeCommand(shop.ProductId, "5901234123457", BarcodeFormat.EAN13)));
        Assert.True(barcode.IsSuccess, barcode.IsFailure ? barcode.Error.ToString() : null);

        // one DI scope per user action (Stage 12 decision 3), as a hosted screen does it
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m, sku: "5901234123457");   // resolved by Catalog's IProductBarcodeResolver

        Assert.True((await InScope(desktop.Services, sp => sp.GetRequiredService<DeactivateProductCommandHandler>().HandleAsync(new DeactivateProductCommand(shop.ProductId)))).IsSuccess);

        var refused = await InScope(desktop.Services, sp => sp.GetRequiredService<IPOSService>().AddProductAsync(cartId, shop.Sku, 1m));
        Assert.Equal("POS.AddProduct.ProductInactive", refused.ErrorCode);
        var line = Assert.Single((await InScope(desktop.Services, sp => sp.GetRequiredService<IPOSReader>().GetCartAsync(cartId)))!.Items);
        Assert.Equal((shop.Sku, 2.5m, 1m), (line.ProductSku, line.UnitPrice, line.Quantity));   // POS's own snapshot, unchanged by Catalog
    }

    [Fact]
    public async Task SalesAndInventory_ACheckoutForStockThatIsNoLongerThere_IsRefusedAndNoModuleKeepsAnything()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, stock: 3m);
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 3m);

        // meanwhile the stock is corrected down (damaged goods) through Inventory's own command
        var stockItem = await ScalarAsync(desktop.Host, $"SELECT Id FROM inv_StockItems");
        var adjusted = await InScope(desktop.Services, sp => sp.GetRequiredService<AdjustStockCommandHandler>()
            .HandleAsync(new AdjustStockCommand(Guid.Parse((string)stockItem!), -2m, AdjustmentReason.DamageWrite)));
        Assert.True(adjusted.IsSuccess, adjusted.IsFailure ? adjusted.Error.ToString() : null);
        var before = await VerticalSliceOwnershipTests.TableContentsAsync(desktop.Host);

        var checkout = await CheckoutAsync(desktop.Services, cartId);

        Assert.False(checkout.IsSuccess);
        Assert.Equal(before, await VerticalSliceOwnershipTests.TableContentsAsync(desktop.Host));   // no sale, no payment, no movement, cart still open
        Assert.Equal(1m, await OnHandAsync(desktop.Host, shop.ProductId));
    }

    [Fact]
    public async Task LocalReporting_ReadsTheOtherModulesThroughTheirContracts_Offline()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 2m);
        Assert.True((await CheckoutAsync(desktop.Services, cartId)).IsSuccess);

        var overview = await InScope(desktop.Services, sp => sp.GetRequiredService<IReportProvider>().GetBusinessOverviewAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));

        Assert.True(overview.Sales.IsSuccess, overview.Sales.ErrorMessage);
        Assert.Equal((1, 5.0m), (overview.Sales.Data!.SaleCount, overview.Sales.Data.GrandTotal));
        Assert.True(overview.Inventory.IsSuccess, overview.Inventory.ErrorMessage);
        Assert.True(overview.Customers.IsSuccess && overview.Suppliers.IsSuccess && overview.Purchasing.IsSuccess);
        Assert.Equal(0, desktop.Network.Requests);
    }
}
