using System.Globalization;
using System.Windows.Input;
using CashManagement.Contracts.Interfaces;
using Integration.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using POS.Contracts.Models;
using POS.UI.Resources;
using POS.UI.ViewModels;
using Sales.UI.ViewModels;
using static Integration.Tests.FailureTestKit;

namespace UI.Tests.Pos;

/// <summary>
/// MISS-10b on the production-like offline desktop: a sale made on the till is returned from Sales history (through the shell's request, as
/// the menu does it) and by its receipt number; the refund screen's amount is what Sales records; cash leaves the drawer, the items go back on
/// the shelf or are written off.
/// </summary>
[Collection(nameof(RealDesktop))]
public sealed class ReturnsOnRealDesktopTests
{
    /// <summary>The shell as far as Sales history sees it: the request goes to the till's view model (the shell's own rules: ShellViewModelTests).</summary>
    private sealed class TillShell(PosViewModel till) : IShellNavigation
    {
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public bool CanOpenFor(string requestKind) => requestKind == ScreenRequests.ReturnSale;

        public async Task<bool> OpenForAsync(ScreenRequest request, CancellationToken cancellationToken = default)
        {
            await till.OnRequestAsync(request, cancellationToken);
            return true;
        }
    }

    private static UiActionRunner Runner(IServiceProvider services) => new(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);

    private static async Task Run(ViewModelBase vm, ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        for (var i = 0; i < 1000 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    private static async Task<decimal> DrawerAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN"))!.Balance;
    }

    [Fact]
    public async Task A_sale_is_returned_from_sales_history_in_cash_and_the_rest_by_receipt_number_as_damaged_by_card()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        var shop = await CreateShopAsync(services, salePrice: 2.5m, stock: 10m);
        var till = new PosViewModel(Runner(services), services.GetRequiredService<ICurrentUser>());
        await till.OnNavigatedToAsync();
        await Run(till, till.OpenSessionCommand);
        (till.ProductCode, till.QuantityText) = (shop.Sku, 3m.ToString(CultureInfo.CurrentCulture));
        await Run(till, till.AddCommand);
        await Run(till, till.CheckoutCommand);
        Assert.Null(till.ErrorMessage);
        var drawer = await DrawerAsync(services);
        var (printed, opened) = (desktop.Printer.Printed.Count, desktop.Drawer.Opened);

        // Sales history: the completed sale's "⋯ > Return…" opens the till's return of it
        var history = new SalesHistoryViewModel(Runner(services), new TillShell(till));
        await history.OnNavigatedToAsync();
        var row = Assert.Single(history.Sales);
        Assert.True(row.CanReturn);
        await Run(history, history.ReturnRowCommand, row);
        for (var i = 0; i < 1000 && till.IsBusy; i++) await Task.Delay(10);
        Assert.True(till.IsReturnMode);
        Assert.Equal(row.Sale.SaleId, till.ReturnSale?.SaleId);

        var line = Assert.Single(till.ReturnLines);
        line.Quantity = 2m;
        till.ReturnReason = "Changed mind";
        Assert.Equal(5m, till.RefundTotal);
        await Run(till, till.GiveRefundCommand);
        Assert.Null(till.ErrorMessage);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.RefundGiven, 5m, PosText.PayCash), till.StatusMessage);
        Assert.False(till.IsReturnMode);
        Assert.Equal(9m, await OnHandAsync(desktop.Host, shop.ProductId));            // 10 - 3 + 2 back on the shelf
        Assert.Equal(drawer - 5m, await DrawerAsync(services));

        // MISS-10c: the return receipt is printed and the drawer opens for the cash
        Assert.Equal(printed + 1, desktop.Printer.Printed.Count);
        var receipt = desktop.Printer.Printed[^1];
        Assert.Equal(("S-" + row.Sale.SaleId.ToString("N")[..8].ToUpperInvariant(), "Changed mind"), (receipt.Return?.OriginalReference, receipt.Return?.Reason));
        Assert.StartsWith("R-", receipt.Reference, StringComparison.Ordinal);
        Assert.Equal((5m, 2m, 2.5m, false), (receipt.Total, receipt.Lines.Single().Quantity, receipt.Lines.Single().UnitPrice, receipt.Lines.Single().WrittenOff));
        Assert.Equal(("Cash", 5m), (receipt.Payment?.Method, receipt.Payment?.Amount));
        Assert.Equal(opened + 1, desktop.Drawer.Opened);
        Assert.Null(till.HardwareMessage);

        // the last unit by the printed receipt number: damaged, by card
        await Run(till, till.StartReturnCommand);
        till.ReceiptNumberText = till.ReturnSale?.ReceiptNumber ?? "S-" + row.Sale.SaleId.ToString("N")[..8];
        await Run(till, till.FindReturnCommand);
        Assert.Null(till.ErrorMessage);
        line = Assert.Single(till.ReturnLines);
        Assert.Equal(1m, line.Line.RemainingQuantity);
        await Run(till, till.ReturnEverythingCommand);
        line.IsDamaged = true;
        (till.ReturnReason, till.RefundMethod) = ("Broken seal", till.PaymentMethods.Single(m => m.Method == POSPaymentMethod.Card));
        Assert.Equal(2.5m, till.RefundTotal);
        await Run(till, till.GiveRefundCommand);
        Assert.Null(till.ErrorMessage);
        Assert.Equal(9m, await OnHandAsync(desktop.Host, shop.ProductId));            // written off, not back on the shelf
        Assert.Equal(drawer - 5m, await DrawerAsync(services));                       // the card refund leaves the drawer alone
        Assert.Equal(2, await CountAsync(desktop.Host, "sal_Returns"));
        Assert.Equal(printed + 2, desktop.Printer.Printed.Count);                     // a return receipt every time
        Assert.True(desktop.Printer.Printed[^1].Lines.Single().WrittenOff);
        Assert.Equal(opened + 1, desktop.Drawer.Opened);                              // no cash, no drawer

        // MISS-10c: Sales history shows what was refunded of the sale and how many units came back
        await history.OnNavigatedToAsync();
        var refunded = history.Sales.Single();
        Assert.Equal(7.5m, refunded.Refunded);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, global::Sales.UI.Resources.SalesText.SummaryWithRefunds, 1, 7.5m, 7.5m, 0m), history.SummaryText);
        history.Selected = refunded;
        Assert.Equal(3m.ToString("0.###", CultureInfo.CurrentCulture), history.Lines.Single().ReturnedText);

        // MISS-10c: the report takes the refunds off, on the day they were given
        using (var scope = services.CreateScope())
        {
            var report = await scope.ServiceProvider.GetRequiredService<Reporting.Application.Queries.GetSalesReportQueryHandler>()
                .HandleAsync(new Reporting.Application.Queries.GetSalesReportQuery(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1)));
            Assert.True(report.IsSuccess, report.IsFailure ? report.Error.Description : null);
            Assert.Equal((7.5m, 7.5m, 0m), (report.Value.GrandTotal, report.Value.RefundTotal, report.Value.NetTotal));
        }

        // nothing is left to return
        await Run(history, history.ReturnRowCommand, history.Sales.Single());
        for (var i = 0; i < 1000 && till.IsBusy; i++) await Task.Delay(10);
        Assert.True(till.ReturnNothingLeft);
        Assert.True(till.ReturnLines.Single().IsFullyReturned);
    }

    [Fact]
    public async Task Without_a_screen_for_returns_sales_history_offers_no_return()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services, salePrice: 2.5m, stock: 10m);
        var till = new PosViewModel(Runner(desktop.Services), desktop.Services.GetRequiredService<ICurrentUser>());
        await till.OnNavigatedToAsync();
        await Run(till, till.OpenSessionCommand);
        (till.ProductCode, till.QuantityText) = (shop.Sku, "1");
        await Run(till, till.AddCommand);
        await Run(till, till.CheckoutCommand);

        var history = new SalesHistoryViewModel(Runner(desktop.Services));             // no shell: no screen handles returns
        await history.OnNavigatedToAsync();

        var row = Assert.Single(history.Sales);
        Assert.False(row.CanReturn);
        Assert.False(history.ReturnRowCommand.CanExecute(row));
    }
}
