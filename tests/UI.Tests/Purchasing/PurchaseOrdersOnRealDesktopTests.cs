using System.Globalization;
using System.Windows.Input;
using Integration.Tests;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Purchasing.UI.Resources;
using Purchasing.UI.ViewModels;
using Suppliers.Application.Commands;
using UI.Tests.Pos;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Purchasing;

/// <summary>FIX-01d: the purchase orders screen on the production-like offline desktop - an order placed and received here adds stock.</summary>
[Collection(nameof(RealDesktop))]
public sealed class PurchaseOrdersOnRealDesktopTests
{
    private static async Task Wait(ViewModelBase vm)
    {
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static async Task Run(ViewModelBase vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        await Wait(vm);
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    private static async Task<(IServiceProvider Services, Shop Shop, PurchaseOrdersViewModel Vm)> ReadyAsync(OfflineDesktop desktop)
    {
        var shop = await CreateShopAsync(desktop.Services, stock: 10m);
        using (var scope = desktop.Services.CreateScope())
            Assert.True((await scope.ServiceProvider.GetRequiredService<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand("S-001", "Fresh Water Co"))).IsSuccess);

        var vm = new PurchaseOrdersViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await vm.OnNavigatedToAsync();
        return (desktop.Services, shop, vm);
    }

    private static async Task CreateOrderAsync(PurchaseOrdersViewModel vm)
    {
        vm.SupplierText = "fresh";
        await Run(vm, vm.FindSupplierCommand);
        Assert.Equal("Fresh Water Co", vm.Supplier?.Name);   // the only match is preselected
        vm.NewReference = "PO-REF-1";
        await Run(vm, vm.CreateCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsDraft);
    }

    [Fact]
    public async Task An_order_is_created_filled_placed_and_received_and_the_stock_goes_up()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var (services, shop, vm) = await ReadyAsync(desktop);
        Assert.Equal(shop.WarehouseId, vm.Warehouse?.WarehouseId);   // the only warehouse is preselected
        await CreateOrderAsync(vm);
        Assert.False(vm.SubmitCommand.CanExecute(null));   // an order needs lines

        (vm.LineCode, vm.LineQuantity, vm.LineCost) = (shop.Sku.ToLowerInvariant(), Number(12m), Number(1.1m));
        await Run(vm, vm.AddLineCommand);
        Assert.Null(vm.ErrorMessage);
        var line = Assert.Single(vm.Lines);
        Assert.Equal((12m, 1.1m, 13.2m), (line.Quantity, line.UnitCost, line.LineTotal));

        (vm.LineCode, vm.LineQuantity) = ("NO-SUCH", Number(1m));
        await Run(vm, vm.AddLineCommand);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Single(vm.Lines);

        await Run(vm, vm.SubmitCommand);
        Assert.True(vm.IsSubmitted);
        Assert.Equal(PurchasingText.Submitted, vm.Orders.Single().StatusText);

        await Run(vm, vm.ReceiveCommand);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderReceived, vm.Order!.Number, 1), vm.StatusMessage);
        Assert.Equal(PurchasingText.Received, vm.Orders.Single().StatusText);
        Assert.True(vm.Lines.Single().IsReceived);

        using var scope = services.CreateScope();
        var level = await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId);
        Assert.Equal(22m, level!.OnHand);
    }

    [Fact]
    public async Task A_part_delivery_adds_only_what_arrived_and_the_order_is_then_closed_short()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var (services, shop, vm) = await ReadyAsync(desktop);
        await CreateOrderAsync(vm);
        (vm.LineCode, vm.LineQuantity, vm.LineCost) = (shop.Sku, Number(12m), Number(1m));
        await Run(vm, vm.AddLineCommand);
        await Run(vm, vm.SubmitCommand);
        var number = vm.Order!.Number;

        var row = Assert.Single(vm.Lines);
        Assert.Equal(Number(12m), row.ReceiveNow);   // the delivery starts at what is still expected
        row.ReceiveNow = Number(5m);
        await Run(vm, vm.ReceiveCommand);

        Assert.Null(vm.ErrorMessage);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderPartlyReceived, number, 1), vm.StatusMessage);
        Assert.True(vm.IsPartiallyReceived && vm.IsAwaitingGoods && vm.CanEnd);
        Assert.Equal(PurchasingText.PartiallyReceived, vm.Orders.Single().StatusText);
        row = vm.Lines.Single();
        Assert.Equal((5m, 7m, Number(7m)), (row.ReceivedQuantity, row.OutstandingQuantity, row.ReceiveNow));
        Assert.Equal(15m, await OnHandAsync(services, shop));

        row.ReceiveNow = Number(8m);   // more than is still expected: refused, nothing moves
        await Run(vm, vm.ReceiveCommand);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Equal(15m, await OnHandAsync(services, shop));

        vm.Lines.Single().ReceiveNow = "two";
        await Run(vm, vm.ReceiveCommand);
        Assert.Equal(PurchasingText.ReceiveQuantityInvalid, vm.ErrorMessage);

        vm.Lines.Single().ReceiveNow = Number(2m);
        await Run(vm, vm.ReceiveCommand);
        Assert.Equal(17m, await OnHandAsync(services, shop));
        Assert.True(vm.IsPartiallyReceived);

        Assert.False(vm.CloseShortCommand.CanExecute(null));   // a reason is required
        vm.CancelReason = "supplier ran out";
        Assert.False(vm.CancelOrderCommand.CanExecute(null));   // something arrived: close short, not cancel
        await Run(vm, vm.CloseShortCommand);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderClosedShort, number), vm.StatusMessage);
        Assert.Equal(PurchasingText.ClosedShort, vm.Orders.Single().StatusText);
        Assert.False(vm.IsAwaitingGoods || vm.CanEnd);
        Assert.Equal((7m, 0m, false), (vm.Lines.Single().ReceivedQuantity, vm.Lines.Single().OutstandingQuantity, vm.Lines.Single().CanReceive));
        Assert.Equal("supplier ran out", vm.Order!.ClosingReason);
        Assert.Equal(17m, await OnHandAsync(services, shop));
    }

    private static async Task<decimal> OnHandAsync(IServiceProvider services, Shop shop)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId))!.OnHand;
    }

    [Fact]
    public async Task An_order_is_cancelled_with_a_reason_and_selecting_it_in_the_list_shows_it_again()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var (_, shop, vm) = await ReadyAsync(desktop);
        await CreateOrderAsync(vm);
        (vm.LineCode, vm.LineQuantity) = (shop.Sku, Number(3m));
        await Run(vm, vm.AddLineCommand);
        var number = vm.Order!.Number;

        Assert.False(vm.CancelOrderCommand.CanExecute(null));   // a reason is required
        vm.CancelReason = "ordered twice by mistake";
        await Run(vm, vm.CancelOrderCommand);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderCancelled, number), vm.StatusMessage);
        Assert.False(vm.IsDraft || vm.IsSubmitted);

        // a fresh screen (as after signing in again) finds the order in the list and loads it when selected
        var again = new PurchaseOrdersViewModel(new UiActionRunner(desktop.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance));
        await again.OnNavigatedToAsync();
        again.Filter = again.Filters.Single(f => f.Name == PurchasingText.Cancelled);
        await Run(again, again.ShowCommand);
        again.SelectedRow = Assert.Single(again.Orders);
        await Wait(again);
        Assert.Equal(number, again.Order?.Number);
        Assert.Single(again.Lines);
    }
}
