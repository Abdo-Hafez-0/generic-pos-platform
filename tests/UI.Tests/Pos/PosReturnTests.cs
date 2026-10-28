using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using POS.UI.Resources;
using POS.UI.ViewModels;

namespace UI.Tests.Pos;

/// <summary>
/// MISS-10b: the till's return mode (owner's choices 2026-10-19) against an in-memory till: the sale found by its receipt number, from the
/// recent sales or asked for by Sales history; − / + and Shelf | Damaged per line; the refund method, the reason, and the sale in progress kept.
/// </summary>
public sealed class PosReturnTests
{
    private readonly FakeTill _till = new();
    private readonly FakeScannerInput _scanner;
    private readonly PosViewModel _vm;

    public PosReturnTests()
    {
        _scanner = new FakeScannerInput(_till);
        var services = new ServiceCollection();
        services.AddSingleton(_till);
        services.AddScoped<FakePosService>();
        services.AddScoped<IPOSService>(sp => sp.GetRequiredService<FakePosService>());
        services.AddScoped<IPOSReader>(sp => sp.GetRequiredService<FakePosService>());
        var runner = new UiActionRunner(services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }).GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        _vm = new PosViewModel(runner, new FakeCurrentUser(), _scanner);
    }

    private async Task Run(ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command was not executable");
        command.Execute(parameter);
        for (var i = 0; i < 200 && _vm.IsBusy; i++) await Task.Delay(5);
        Assert.False(_vm.IsBusy);
    }

    private async Task OpenTillAsync()
    {
        _till.AddWarehouse("Main shop");
        _till.Products["COLA-1"] = (Guid.NewGuid(), "Cola", 2.5m);
        await _vm.OnNavigatedToAsync();
        await Run(_vm.OpenSessionCommand);
    }

    private async Task<POSReturnableSale> FindAsync(POSReturnableSale sale)
    {
        await Run(_vm.StartReturnCommand);
        _vm.ReceiptNumberText = sale.ReceiptNumber.ToLowerInvariant()[2..];   // as typed: without "S-", in lower case
        await Run(_vm.FindReturnCommand);
        Assert.Null(_vm.ErrorMessage);
        return sale;
    }

    [Fact]
    public async Task Return_shows_the_receipt_box_instead_of_the_sale_and_keeps_the_cart_for_after()
    {
        await OpenTillAsync();
        (_vm.ProductCode, _vm.QuantityText) = ("COLA-1", "2");
        await Run(_vm.AddCommand);

        await Run(_vm.StartReturnCommand);

        Assert.True(_vm.IsReturnMode);
        Assert.True(_vm.IsFindingReturn);
        Assert.False(_vm.IsSelling);
        Assert.False(_vm.HasReturnSale);
        Assert.False(_vm.StartReturnCommand.CanExecute(null));
        Assert.False(_vm.FindReturnCommand.CanExecute(null));                // nothing typed yet
        Assert.Equal(PosText.ReceiptNumberHint, _vm.StatusMessage);

        await Run(_vm.BackToSellingCommand);
        Assert.True(_vm.IsSelling);
        Assert.Single(_vm.Items);                                            // the sale in progress is still there
        Assert.Equal(5m, _vm.Total);
    }

    [Fact]
    public async Task The_receipt_number_finds_the_sale_with_nothing_chosen_and_the_refund_follows_the_steppers()
    {
        await OpenTillAsync();
        var sale = await FindAsync(_till.AddReturnableSale(("Milk", 3m, 0m, 6.5m, 0m), ("Bread", 2m, 0m, 4m, 0m)));

        Assert.True(_vm.HasReturnSale);
        Assert.False(_vm.IsFindingReturn);
        Assert.Contains(sale.ReceiptNumber, _vm.ReturnTitle);
        Assert.Equal(PosText.ReturnChooseItems, _vm.StatusMessage);
        Assert.All(_vm.ReturnLines, r => Assert.Equal(0m, r.Quantity));
        Assert.Equal(0m, _vm.RefundTotal);
        var milk = _vm.ReturnLines[0];
        Assert.False(milk.FewerCommand.CanExecute(null));                    // never below 0

        foreach (var _ in Enumerable.Range(0, 3)) await RunRow(milk.MoreCommand);
        Assert.False(milk.MoreCommand.CanExecute(null));                     // never above what is left
        Assert.Equal(6.5m, _vm.RefundTotal);
        await RunRow(milk.FewerCommand);
        Assert.Equal((2m, 4.33m), (milk.Quantity, _vm.RefundTotal));         // 6.50 x 2 / 3, rounded like Sales does

        await Run(_vm.ReturnEverythingCommand);
        Assert.Equal(10.5m, _vm.RefundTotal);
        Assert.False(_vm.ReturnEverythingCommand.CanExecute(null));
    }

    private static async Task RunRow(ICommand command)
    {
        Assert.True(command.CanExecute(null));
        command.Execute(null);
        await Task.Yield();
    }

    [Fact]
    public async Task The_refund_needs_a_reason_and_sends_the_lines_the_shelf_choice_and_the_method()
    {
        await OpenTillAsync();
        var sale = await FindAsync(_till.AddReturnableSale(("Milk", 3m, 0m, 6.5m, 0m), ("Bread", 2m, 0m, 4m, 0m)));
        _vm.ReturnLines[0].QuantityText = "1";
        _vm.ReturnLines[1].Quantity = 2m;
        _vm.ReturnLines[1].IsDamaged = true;
        Assert.False(_vm.ReturnLines[1].GoesToShelf);
        _vm.RefundMethod = _vm.PaymentMethods.Single(m => m.Method == POSPaymentMethod.Card);
        Assert.False(_vm.GiveRefundCommand.CanExecute(null));                // no reason yet

        _vm.ReturnReason = " Broken seal ";
        await Run(_vm.GiveRefundCommand);

        var refund = Assert.Single(_till.Refunds);
        Assert.Equal((sale.SaleId, "Broken seal", POSPaymentMethod.Card, (string?)null), (refund.SaleId, refund.Reason, refund.Method, refund.Detail));
        Assert.Equal([(sale.Lines[0].SaleItemId, 1m, true), (sale.Lines[1].SaleItemId, 2m, false)],
            refund.Lines.Select(l => (l.SaleItemId, l.Quantity, l.BackToStock)));
        Assert.False(_vm.IsReturnMode);                                       // back to selling
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.RefundGiven, 6.17m, PosText.PayCard), _vm.StatusMessage);   // 2.17 + 4.00
    }

    [Fact]
    public async Task An_other_refund_needs_to_say_how_and_a_typed_quantity_above_what_is_left_blocks_the_refund()
    {
        await OpenTillAsync();
        await FindAsync(_till.AddReturnableSale(("Milk", 3m, 1m, 6.5m, 2.17m)));
        var milk = _vm.ReturnLines[0];
        _vm.ReturnReason = "Changed mind";

        milk.QuantityText = "3";                                              // only 2 are left
        Assert.False(milk.IsValid);
        Assert.False(_vm.GiveRefundCommand.CanExecute(null));
        milk.QuantityText = "2";
        Assert.Equal(4.33m, _vm.RefundTotal);                                 // what is left of the line
        Assert.True(_vm.GiveRefundCommand.CanExecute(null));

        _vm.RefundMethod = _vm.PaymentMethods.Single(m => m.Method == POSPaymentMethod.Other);
        Assert.True(_vm.RefundNeedsNote);
        Assert.False(_vm.GiveRefundCommand.CanExecute(null));
        _vm.RefundNote = "Bank transfer";
        await Run(_vm.GiveRefundCommand);
        Assert.Equal("Bank transfer", Assert.Single(_till.Refunds).Detail);
    }

    [Fact]
    public async Task A_printer_problem_after_the_refund_is_said_and_the_refund_stands()
    {
        // MISS-10c
        await OpenTillAsync();
        await FindAsync(_till.AddReturnableSale(("Milk", 1m, 0m, 2.5m, 0m)));
        _vm.ReturnLines[0].Quantity = 1m;
        _vm.ReturnReason = "Changed mind";
        _till.RefundHardwareNotices = [new POSHardwareNotice("receipt printer", "Hardware.Failed", "The refund was given and saved, but the return receipt could not be printed: out of paper")];

        await Run(_vm.GiveRefundCommand);

        Assert.False(_vm.IsReturnMode);
        Assert.Null(_vm.ErrorMessage);
        Assert.Contains("out of paper", _vm.HardwareMessage);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.RefundGiven, 2.5m, PosText.PayCash), _vm.StatusMessage);
    }

    [Fact]
    public async Task A_refused_refund_keeps_the_return_on_the_screen_with_the_reason()
    {
        await OpenTillAsync();
        await FindAsync(_till.AddReturnableSale(("Milk", 1m, 0m, 2.5m, 0m)));
        _vm.ReturnLines[0].Quantity = 1m;
        _vm.ReturnReason = "Changed mind";
        _till.RefundRefusal = "The drawer holds 1.00, less than the refund of 2.50. Nothing was saved.";

        await Run(_vm.GiveRefundCommand);

        Assert.True(_vm.IsReturnMode);
        Assert.True(_vm.HasReturnSale);
        Assert.Equal(_till.RefundRefusal, _vm.ErrorMessage);
        Assert.Equal(1m, _vm.ReturnLines[0].Quantity);
    }

    [Fact]
    public async Task An_unknown_or_ambiguous_receipt_number_is_said_in_plain_words()
    {
        await OpenTillAsync();
        await Run(_vm.StartReturnCommand);
        _vm.ReceiptNumberText = "S-NOPE";
        await Run(_vm.FindReturnCommand);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.ReturnReceiptNotFound, "‎S-NOPE‎"), _vm.ErrorMessage);
        Assert.True(_vm.IsFindingReturn);

        var first = _till.AddReturnableSale(("Milk", 1m, 0m, 2.5m, 0m));
        var twin = first with { SaleId = Guid.NewGuid() };
        _till.ReturnableSales[twin.SaleId] = twin;
        _vm.ReceiptNumberText = first.ReceiptNumber;
        await Run(_vm.FindReturnCommand);
        Assert.Equal(PosText.ReturnReceiptAmbiguous, _vm.ErrorMessage);
    }

    [Fact]
    public async Task A_line_returned_in_full_shows_returned_and_a_sale_with_nothing_left_says_so()
    {
        await OpenTillAsync();
        await FindAsync(_till.AddReturnableSale(("Milk", 2m, 2m, 5m, 5m)));

        Assert.True(_vm.ReturnLines[0].IsFullyReturned);
        Assert.True(_vm.ReturnNothingLeft);
        Assert.False(_vm.ReturnEverythingCommand.CanExecute(null));
        Assert.Null(_vm.StatusMessage);
    }

    [Fact]
    public async Task A_late_sale_says_who_can_give_the_refund()
    {
        await OpenTillAsync();
        var sale = _till.AddReturnableSale(("Milk", 1m, 0m, 2.5m, 0m));
        _till.ReturnableSales[sale.SaleId] = sale with { IsLate = true };

        await FindAsync(sale);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.ReturnLate, 14), _vm.ReturnLateText);
    }

    [Fact]
    public async Task A_large_return_without_a_sale_receipt_asks_for_the_buyer_as_a_sale_does()
    {
        // MISS-11e (owner's decision): Egypt - a sale made before e-receipts were on; its return names the buyer at or above 150,000
        await OpenTillAsync();
        var added = _till.AddReturnableSale(("TV", 2m, 0m, 300_000m, 0m));
        var sale = _till.ReturnableSales[added.SaleId] = added with { Fiscal = new POSFiscalReturnRule(new POSFiscalBuyerRule(150_000m, 14), 540) };
        await FindAsync(sale);
        _vm.ReturnReason = "Changed mind";

        _vm.ReturnLines[0].Quantity = 1m;                                     // 150,000: at the threshold
        Assert.True(_vm.ReturnNeedsBuyer);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.BuyerNeeded, 150_000m), _vm.ReturnBuyerNotice);
        Assert.Null(_vm.ReturnFiscalLimitText);

        _vm.ReturnBuyerName = "Ahmed Ali";
        _vm.ReturnBuyerNationalId = "2990101";                                // too short
        await Run(_vm.GiveRefundCommand);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.BuyerMissing, 14), _vm.ErrorMessage);
        Assert.Empty(_till.Refunds);

        _vm.ReturnBuyerNationalId = "29901011234567";
        await Run(_vm.GiveRefundCommand);
        Assert.Equal(new POSBuyer("Ahmed Ali", "29901011234567"), Assert.Single(_till.Refunds).Buyer);
    }

    [Fact]
    public async Task A_small_return_needs_no_buyer_and_a_sale_past_the_tax_authoritys_days_is_warned()
    {
        // MISS-11e (owner's decision): past ETA's 540 days the till warns that the return gets no electronic receipt
        await OpenTillAsync();
        var added = _till.AddReturnableSale(("Milk", 1m, 0m, 2.5m, 0m));
        var sale = _till.ReturnableSales[added.SaleId] = added with
        {
            CompletedAtUtc = DateTime.UtcNow.AddDays(-541), IsLate = true, Fiscal = new POSFiscalReturnRule(new POSFiscalBuyerRule(150_000m, 14), 540),
        };
        await FindAsync(sale);
        _vm.ReturnLines[0].Quantity = 1m;
        _vm.ReturnReason = "Changed mind";

        Assert.False(_vm.ReturnNeedsBuyer);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.ReturnPastFiscalLimit, 540), _vm.ReturnFiscalLimitText);
        await Run(_vm.GiveRefundCommand);
        Assert.Null(Assert.Single(_till.Refunds).Buyer);
    }

    [Fact]
    public async Task Sales_history_asks_for_a_return_and_the_till_shows_that_sale_even_without_an_open_till()
    {
        _till.AddWarehouse("Main shop");
        await _vm.OnNavigatedToAsync();
        Assert.True(_vm.ShowsWarehouseChoice);
        var sale = _till.AddReturnableSale(("Milk", 1m, 0m, 2.5m, 0m));

        await _vm.OnRequestAsync(new ScreenRequest(ScreenRequests.ReturnSale, sale.SaleId));

        Assert.True(_vm.IsReturnMode);
        Assert.Equal(sale.SaleId, _vm.ReturnSale?.SaleId);
        Assert.False(_vm.ShowsWarehouseChoice);
        await Run(_vm.BackToSellingCommand);
        Assert.True(_vm.ShowsWarehouseChoice);

        await _vm.OnRequestAsync(new ScreenRequest(ScreenRequests.ReturnSale, Guid.NewGuid()));   // not a completed sale
        Assert.Equal(PosText.ReturnSaleNotFound, _vm.ErrorMessage);
        await _vm.OnRequestAsync(new ScreenRequest("product.open", sale.SaleId));                 // not a request the till handles
        Assert.Equal(PosText.ReturnSaleNotFound, _vm.ErrorMessage);
    }

    [Fact]
    public async Task During_a_return_scans_do_not_reach_the_sale()
    {
        await OpenTillAsync();
        var cart = _vm.CartId;
        Assert.Equal(cart, _scanner.BoundCart);

        await Run(_vm.StartReturnCommand);
        Assert.Null(_scanner.BoundCart);
        _scanner.Scan("COLA-1");
        Assert.Empty(_till.Carts[cart!.Value]);
        Assert.Equal(PosText.ReturnScanIgnored, _vm.ErrorMessage);

        await Run(_vm.BackToSellingCommand);
        Assert.Equal(cart, _scanner.BoundCart);
    }

    [Fact]
    public async Task A_recent_sale_can_be_returned_from_the_list()
    {
        await OpenTillAsync();
        var sale = _till.AddReturnableSale(("Milk", 1m, 0m, 2.5m, 0m));
        _vm.SelectedRecent = new RecentReceiptRow(new POSRecentReceipt(Guid.NewGuid(), sale.SaleId, DateTime.UtcNow, sale.ReceiptNumber, 2.5m, 1, "cashier1"), "now", "2.50");

        await Run(_vm.ReturnRecentCommand);

        Assert.True(_vm.IsReturnMode);
        Assert.Equal(sale.SaleId, _vm.ReturnSale?.SaleId);
        Assert.False(_vm.IsRecentShown);
    }

    [Fact]
    public async Task The_till_says_when_electronic_receipts_have_waited_too_long_and_stops_when_they_are_through()
    {
        // MISS-11a-3 (owner's decision: a short line on the till after 12 hours)
        _till.FiscalNotice = new POSFiscalNotice(3, DateTime.UtcNow.AddHours(-13), 12);
        await OpenTillAsync();
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, PosText.FiscalOverdue, 3, 12d), _vm.FiscalNotice);

        _till.FiscalNotice = null;
        (_vm.ProductCode, _vm.QuantityText) = ("COLA-1", "1");
        await Run(_vm.AddCommand);
        await Run(_vm.CheckoutCommand);                                                   // asked again after each sale
        Assert.Null(_vm.FiscalNotice);
    }
}
