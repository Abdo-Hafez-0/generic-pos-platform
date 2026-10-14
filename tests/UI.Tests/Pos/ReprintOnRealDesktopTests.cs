using System.Windows.Input;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using POS.UI.Resources;
using POS.UI.ViewModels;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Pos;

/// <summary>
/// MISS-07c: on the real desktop composition, the cashier prints the last sale's receipt again, and a sale chosen from today's and
/// yesterday's list; each print is a copy and goes to the audit log.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class ReprintOnRealDesktopTests
{
    private static async Task Run(PosViewModel vm, ICommand command)
    {
        Assert.True(command.CanExecute(null), "the command was not executable");
        command.Execute(null);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task The_cashier_prints_the_last_receipt_and_a_recent_one_again_as_copies()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        var shop = await CreateShopAsync(services, salePrice: 2.5m, stock: 10m);
        var vm = new PosViewModel(new UiActionRunner(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance), services.GetRequiredService<ICurrentUser>());
        await vm.OnNavigatedToAsync();
        Assert.True(vm.CanReprint);                       // the administrator holds pos.receipt.reprint
        Assert.False(vm.ReprintLastCommand.CanExecute(null));   // nothing sold yet on this screen
        await Run(vm, vm.OpenSessionCommand);

        foreach (var quantity in new[] { "1", "3" })
        {
            (vm.ProductCode, vm.QuantityText) = (shop.Sku, quantity);
            await Run(vm, vm.AddCommand);
            await Run(vm, vm.CheckoutCommand);
            Assert.Null(vm.ErrorMessage);
        }
        Assert.Equal(2, desktop.Printer.Printed.Count);
        Assert.All(desktop.Printer.Printed, r => Assert.False(r.IsCopy));

        await Run(vm, vm.ReprintLastCommand);
        Assert.Equal(PosText.ReceiptReprinted, vm.StatusMessage);
        var last = desktop.Printer.Printed[^1];
        Assert.True(last.IsCopy);
        Assert.Equal(7.5m, last.Total);

        await Run(vm, vm.ShowRecentCommand);
        Assert.True(vm.IsRecentShown);
        Assert.False(vm.HasNoRecent);
        Assert.Equal([7.5m, 2.5m], vm.RecentReceipts.Select(r => r.Receipt.Total));   // newest first
        vm.SelectedRecent = vm.RecentReceipts[1];
        await Run(vm, vm.ReprintSelectedCommand);
        var older = desktop.Printer.Printed[^1];
        Assert.True(older.IsCopy);
        Assert.Equal((2.5m, vm.RecentReceipts[1].Receipt.Reference), (older.Total, older.Reference));
        await Run(vm, vm.HideRecentCommand);
        Assert.False(vm.IsRecentShown);

        using var read = services.CreateScope();
        var audit = read.ServiceProvider.GetRequiredService<Audit.Contracts.Interfaces.IAuditReader>();
        for (var i = 0; i < 200 && (await audit.QueryAsync(new Audit.Contracts.Models.AuditEntryFilter(Module: "pos"))).Items.Count(e => e.Action == "receipt.reprinted") < 2; i++) await Task.Delay(25);
        Assert.Equal(2, (await audit.QueryAsync(new Audit.Contracts.Models.AuditEntryFilter(Module: "pos"))).Items.Count(e => e.Action == "receipt.reprinted"));
    }
}
