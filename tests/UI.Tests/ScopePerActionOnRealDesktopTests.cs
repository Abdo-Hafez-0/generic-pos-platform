using System.Windows.Input;
using Catalog.Application.Commands;
using Catalog.Application.Queries;
using Catalog.Contracts.Interfaces;
using Catalog.UI.ViewModels;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using UI.Tests.Pos;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests;

/// <summary>
/// FIX-07 (Stage 13 "one DI scope per user action"): module read contracts TRACK what they read, so a scope that outlives one action reads
/// stale data. The shell runs every action in a fresh scope; on the real offline desktop a screen therefore sees a change made elsewhere
/// at its next action. The contrast test shows the hazard is real, i.e. the rule is what keeps the screens correct.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class ScopePerActionOnRealDesktopTests
{
    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    /// <summary>Another user (another terminal, another screen) changes the product's sale price in its own action.</summary>
    private static async Task ChangeSalePriceElsewhereAsync(IServiceProvider services, Guid productId, decimal price)
    {
        using var scope = services.CreateScope();
        var p = scope.ServiceProvider;
        var product = (await p.GetRequiredService<GetProductByIdQueryHandler>().HandleAsync(new GetProductByIdQuery(productId))).Value;
        var changed = await p.GetRequiredService<UpdateProductCommandHandler>().HandleAsync(
            new UpdateProductCommand(product.Id, product.Name, product.CategoryId, product.UnitId, price, product.CostPrice));
        Assert.True(changed.IsSuccess);
    }

    [Fact]
    public async Task A_screen_sees_a_change_made_elsewhere_at_its_next_action()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m);
        var products = new ProductsViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await products.OnNavigatedToAsync();
        Assert.Equal(2.5m, products.Products.Single().Product.SalePrice);

        await ChangeSalePriceElsewhereAsync(desktop.Services, shop.ProductId, 2.75m);
        await Run(products, products.SearchCommand);

        Assert.Equal(2.75m, products.Products.Single().Product.SalePrice);
    }

    [Fact]
    public async Task Contrast_a_scope_kept_across_actions_would_read_the_old_price()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m);

        using var keptAllDay = desktop.Services.CreateScope();     // what a screen holding one scope would do
        var lookup = keptAllDay.ServiceProvider.GetRequiredService<IProductLookup>();
        Assert.Equal(2.5m, (await lookup.FindBySkuAsync(shop.Sku))!.SalePrice);

        await ChangeSalePriceElsewhereAsync(desktop.Services, shop.ProductId, 2.75m);

        Assert.Equal(2.5m, (await lookup.FindBySkuAsync(shop.Sku))!.SalePrice);                     // stale: the context tracked the product
        using var fresh = desktop.Services.CreateScope();
        Assert.Equal(2.75m, (await fresh.ServiceProvider.GetRequiredService<IProductLookup>().FindBySkuAsync(shop.Sku))!.SalePrice);
    }
}
