using System.Windows.Input;
using Client.Desktop;
using Client.Desktop.Shell;
using Client.Hardware;
using Client.Hardware.Scanner;
using Client.Host.Hosting;
using Integration.Tests;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Hardware;
using Platform.Presentation.Actions;
using POS.Contracts.Interfaces;
using POS.UI.ViewModels;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Pos;

/// <summary>
/// FIX-02 on the production-like offline desktop: a keyboard-wedge scanner (the real decoder) behind the shell's key forwarding (the real
/// composition-root bridge) and the cashier screen (the real POS scanner input). Keys go in where the shell window would hand them over.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class ScannerOnRealDesktopTests
{
    /// <summary>What HardwareHostingModule registers for <c>Hardware:Scanner:Type = KeyboardWedge</c>.</summary>
    private sealed class KeyboardWedgeModule : IHostingModule
    {
        public void RegisterServices(HostBuilderContext context, IServiceCollection services)
        {
            var wedge = new KeyboardWedgeBarcodeScanner();
            services.AddSingleton<IBarcodeScanner>(wedge);
            services.AddSingleton<IKeyboardInputSink>(wedge);
        }
    }

    private static PosViewModel Screen(IServiceProvider services) => new(
        new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance),
        services.GetRequiredService<ICurrentUser>(),
        services.GetRequiredService<IPOSBarcodeInput>());

    private static async Task Run(PosViewModel vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_scan_outside_the_barcode_box_is_sold_once_and_its_enter_presses_no_button()
    {
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new KeyboardWedgeModule(), new ScannerKeyboardHostingModule()]);
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 10m);
        var keyboard = desktop.Services.GetRequiredService<ScannerKeyboard>();
        var vm = Screen(desktop.Services);

        await vm.OnNavigatedToAsync();
        await Run(vm, vm.OpenSessionCommand);
        Assert.True(vm.ScannerReady);

        // focus on the cart grid or a button: the shell hands the keys to the decoder, which adds the product once
        keyboard.OnText(shop.Sku, intoTextInput: false);
        Assert.True(keyboard.OnEnter(intoTextInput: false));
        await Eventually(() => vm.Items.Count == 1);
        Assert.Null(vm.ErrorMessage);

        // focus in a text box: the keys stay there (the box's own Enter adds), the decoder adds nothing
        keyboard.OnText(shop.Sku, intoTextInput: true);
        Assert.False(keyboard.OnEnter(intoTextInput: true));

        await Run(vm, vm.CheckoutCommand);
        Assert.Null(vm.ErrorMessage);

        using var scope = desktop.Services.CreateScope();
        var level = await scope.ServiceProvider.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId);
        Assert.Equal(9m, level!.OnHand);
        Assert.Equal(0, desktop.Network.Requests);
    }

    [Fact]
    public async Task After_leaving_the_cashier_screen_a_scan_is_not_a_scan_and_its_enter_works_normally()
    {
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new KeyboardWedgeModule(), new ScannerKeyboardHostingModule()]);
        var shop = await CreateShopAsync(desktop.Services);
        var keyboard = desktop.Services.GetRequiredService<ScannerKeyboard>();
        var vm = Screen(desktop.Services);
        await vm.OnNavigatedToAsync();
        await Run(vm, vm.OpenSessionCommand);

        await vm.OnNavigatedFromAsync();
        keyboard.OnText(shop.Sku, intoTextInput: false);

        Assert.False(keyboard.OnEnter(intoTextInput: false));
        await Task.Delay(100);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task The_real_composition_without_scanner_configuration_ignores_every_key()
    {
        // the real hardware registration with no "Hardware" configuration: every device is "None"
        await using var desktop = await OfflineDesktop.StartAsync(extra: [new HardwareHostingModule(), new ScannerKeyboardHostingModule()]);
        var keyboard = desktop.Services.GetRequiredService<ScannerKeyboard>();
        var vm = Screen(desktop.Services);
        await CreateShopAsync(desktop.Services);
        await vm.OnNavigatedToAsync();

        keyboard.OnText("6001234567890", intoTextInput: false);

        Assert.False(keyboard.OnEnter(intoTextInput: false));
        Assert.False(vm.ScannerReady);
    }
}
