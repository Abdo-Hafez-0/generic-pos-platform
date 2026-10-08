using CashManagement.Contracts.Interfaces;
using CashManagement.Contracts.Models;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// FIX-06 on the real host and the real SQLite file: the contract services other modules write through (Inventory stock issue and receipt,
/// Payments record and void, the cash drawer recorder) turn an UNEXPECTED database failure into a failed result with a plain sentence - like
/// POSService - instead of throwing database text into the calling module; and nothing is written.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class ContractFailureTests
{
    private static void AssertPlain(bool isSuccess, string? code, string? message, string expectedCode)
    {
        Assert.False(isSuccess);
        Assert.Equal(expectedCode, code);
        Assert.Contains("Try again", message);
        foreach (var technical in new[] { "SQLite", "constraint", "inv_", "pay_", "cash_", "Exception", "   at " })
            Assert.DoesNotContain(technical, message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task Stock_issue_and_receipt_report_a_database_failure_as_a_plain_result_and_change_nothing()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, stock: 10m);
        var movements = await CountAsync(host, "inv_StockMovements");

        await using (await FailInsertsAsync(host, "inv_StockMovements"))
        {
            var issued = await InScopeAsync(host.Services, p => p.GetRequiredService<IStockIssueService>().IssueStockAsync(shop.ProductId, shop.WarehouseId, 2m, "test"));
            AssertPlain(issued.IsSuccess, issued.ErrorCode, issued.ErrorMessage, "Inventory.OperationFailed");

            var received = await InScopeAsync(host.Services, p => p.GetRequiredService<IStockReceiptService>().ReceiveStockAsync(shop.ProductId, shop.WarehouseId, 5m, "test"));
            AssertPlain(received.IsSuccess, received.ErrorCode, received.ErrorMessage, "Inventory.OperationFailed");
        }

        Assert.Equal(10m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(movements, await CountAsync(host, "inv_StockMovements"));

        // the database works again: the same calls succeed
        Assert.True((await InScopeAsync(host.Services, p => p.GetRequiredService<IStockIssueService>().IssueStockAsync(shop.ProductId, shop.WarehouseId, 2m, "test"))).IsSuccess);
        Assert.Equal(8m, await OnHandAsync(host, shop.ProductId));
    }

    [Fact]
    public async Task Recording_and_voiding_a_payment_report_a_database_failure_as_a_plain_result_and_change_nothing()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var reference = Guid.NewGuid();
        var request = new RecordPaymentRequest("sale", reference, 5m, PaymentMethodContract.Card);

        await using (await FailInsertsAsync(host, "pay_Payments"))
        {
            var recorded = await InScopeAsync(host.Services, p => p.GetRequiredService<IPaymentService>().RecordPaymentAsync(request));
            AssertPlain(recorded.IsSuccess, recorded.ErrorCode, recorded.ErrorMessage, "Payments.OperationFailed");
        }

        Assert.Equal(0, await CountAsync(host, "pay_Payments"));
        var payment = await InScopeAsync(host.Services, p => p.GetRequiredService<IPaymentService>().RecordPaymentAsync(request));
        Assert.True(payment.IsSuccess, payment.ErrorMessage);

        await using (await FailUpdatesAsync(host, "pay_Payments"))
        {
            var voided = await InScopeAsync(host.Services, p => p.GetRequiredService<IPaymentService>().VoidPaymentAsync(payment.PaymentId, "keyed twice"));
            AssertPlain(voided.IsSuccess, voided.ErrorCode, voided.ErrorMessage, "Payments.OperationFailed");
        }

        Assert.Equal(1, await CountAsync(host, "pay_Payments", "VoidedAt IS NULL"));
    }

    [Fact]
    public async Task Recording_cash_in_the_drawer_reports_a_database_failure_as_a_plain_result_and_changes_nothing()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        await OpenCashDrawerAsync(host.Services, openingFloat: 50m);
        var shift = (await InScopeAsync(host.Services, p => p.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN")))!;

        await using (await FailInsertsAsync(host, "cash_Movements"))
        {
            var recorded = await InScopeAsync(host.Services, p => p.GetRequiredService<ICashMovementRecorder>().RecordMovementAsync(
                new RecordCashMovementRequest(shift.SessionId, CashMovementKindContract.CashSale, 5m, ReferenceType: "sale", ReferenceId: Guid.NewGuid())));
            AssertPlain(recorded.IsSuccess, recorded.ErrorCode, recorded.ErrorMessage, "CashManagement.OperationFailed");
        }

        Assert.Equal(0, await CountAsync(host, "cash_Movements"));
        Assert.Equal(50m, (await InScopeAsync(host.Services, p => p.GetRequiredService<ICashSessionReader>().GetSessionAsync(shift.SessionId)))!.Balance);
    }

    [Fact]
    public async Task Inside_a_checkout_the_inventory_failure_reaches_the_cashier_in_plain_words_and_nothing_is_kept()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);
        var before = await BusinessState.ReadAsync(host);

        await using (await FailInsertsAsync(host, "inv_StockMovements"))
        {
            var sale = await CheckoutAsync(host.Services, cartId);
            Assert.False(sale.IsSuccess);
            Assert.Equal("POS.Checkout.StockIssueFailed", sale.ErrorCode);
            Assert.Contains("Nothing was saved", sale.ErrorMessage);
            Assert.DoesNotContain("SQLite", sale.ErrorMessage);
        }

        Assert.Equal(before, await BusinessState.ReadAsync(host));
        Assert.True((await CheckoutAsync(host.Services, cartId)).IsSuccess);
    }
}
