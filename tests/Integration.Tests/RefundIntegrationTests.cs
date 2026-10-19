using CashManagement.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using POS.Application.Commands;
using POS.Application.Security;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Sales.Contracts.Interfaces;
using Sales.Contracts.Models;
using Users.Application.Commands;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// MISS-10 on the real host and SQLite file: a return is recorded against its sale, the items go back into stock (or are written off as
/// damaged), the money goes back the way the cashier chose (cash out of the drawer, or card), all in one transaction; never more than was
/// sold; a sale past the shop's limit needs the late-returns permission.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class RefundIntegrationTests
{
    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private static Task<ReturnableSaleResult?> ReturnableAsync(IServiceProvider services, Guid saleId)
        => InScopeAsync(services, sp => sp.GetRequiredService<ISalesReader>().GetReturnableAsync(saleId));

    private static Task<Platform.Core.Results.Result<RefundOutcome>> RefundAsync(IServiceProvider services, Guid saleId, decimal quantity,
        POSPaymentMethod method = POSPaymentMethod.Cash, bool backToStock = true, string reason = "Changed mind")
        => InScopeAsync(services, async sp =>
        {
            var line = (await sp.GetRequiredService<ISalesReader>().GetReturnableAsync(saleId))!.Lines[0];
            return await sp.GetRequiredService<RefundSaleCommandHandler>().HandleAsync(
                new RefundSaleCommand(saleId, [new RefundLine(line.SaleItemId, quantity, backToStock)], reason, method));
        });

    private static async Task<decimal> DrawerBalanceAsync(IServiceProvider services)
        => (await InScopeAsync(services, sp => sp.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN")))!.Balance;

    private static async Task<Guid> SellAsync(IntegrationHost host, Shop shop, decimal quantity, decimal? lineDiscount = null)
    {
        var (_, cartId) = await OpenCartAsync(host.Services, shop, quantity);
        if (lineDiscount is { } discount)
        {
            var discounted = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSService>()
                .SetLineDiscountAsync(cartId, shop.ProductId, POSDiscountKind.Amount, discount));
            Assert.True(discounted.IsSuccess, discounted.ErrorMessage);
        }

        var sale = await CheckoutAsync(host.Services, cartId, tendered: 50m);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        return sale.SaleId;
    }

    [Fact]
    public async Task A_cash_return_gives_the_money_from_the_drawer_puts_the_items_back_and_never_returns_more_than_was_sold()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);   // the drawer opens with 50
        var saleId = await SellAsync(host, shop, 3m);                                    // 7.50 in cash
        Assert.Equal(7m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(57.5m, await DrawerBalanceAsync(host.Services));

        var first = await RefundAsync(host.Services, saleId, 1m);
        Assert.True(first.IsSuccess, first.IsFailure ? first.Error.ToString() : null);
        Assert.Equal(2.5m, first.Value.RefundTotal);
        Assert.Equal(8m, await OnHandAsync(host, shop.ProductId));                       // back on the shelf
        Assert.Equal(55m, await DrawerBalanceAsync(host.Services));                     // out of the drawer
        Assert.Equal(1, await CountAsync(host, "pay_Payments", "Direction = 2 AND ReferenceType = 'return'"));
        Assert.Equal(1, await CountAsync(host, "cash_Movements", "Kind = 4"));

        var rest = await RefundAsync(host.Services, saleId, 2m);
        Assert.True(rest.IsSuccess);
        var returnable = await ReturnableAsync(host.Services, saleId);
        Assert.Equal((3m, 0m, 7.5m), (returnable!.Lines[0].ReturnedQuantity, returnable.Lines[0].RemainingQuantity, returnable.RefundedTotal));
        Assert.False(returnable.HasAnythingLeft);

        var again = await RefundAsync(host.Services, saleId, 1m);
        Assert.Equal("Sales.Return.TooMany", again.Error.Code);
        Assert.Equal(2, await CountAsync(host, "sal_Returns"));
    }

    [Fact]
    public async Task After_a_discount_each_return_gives_back_its_share_and_the_cents_add_up_to_what_was_paid()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var saleId = await SellAsync(host, shop, 3m, lineDiscount: 1m);                  // 3 x 2.50 - 1.00 = 6.50

        var refunds = new List<decimal>();
        foreach (var _ in Enumerable.Range(0, 3))
            refunds.Add((await RefundAsync(host.Services, saleId, 1m)).Value.RefundTotal);

        Assert.Equal([2.17m, 2.17m, 2.16m], refunds);   // 6.50 / 3 rounded, the last takes what is left
        Assert.Equal(6.5m, refunds.Sum());
    }

    [Fact]
    public async Task Damaged_items_are_written_off_and_a_card_refund_leaves_the_drawer_alone()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var saleId = await SellAsync(host, shop, 2m);
        var drawer = await DrawerBalanceAsync(host.Services);

        var refund = await RefundAsync(host.Services, saleId, 1m, POSPaymentMethod.Card, backToStock: false, reason: "Broken seal");

        Assert.True(refund.IsSuccess, refund.IsFailure ? refund.Error.ToString() : null);
        Assert.Equal(8m, await OnHandAsync(host, shop.ProductId));                       // not back on the shelf
        Assert.Equal(drawer, await DrawerBalanceAsync(host.Services));                  // no cash moved
        Assert.Equal(1, await CountAsync(host, "pay_Payments", "Direction = 2 AND Method = 2"));
        Assert.Equal(1, await CountAsync(host, "sal_ReturnItems", "BackToStock = 0"));
    }

    [Fact]
    public async Task A_cash_refund_needs_the_cash_in_the_drawer_and_a_refused_refund_saves_nothing()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var saleId = await SellAsync(host, shop, 2m);
        await CloseCashDrawerAsync(host.Services);
        await OpenCashDrawerAsync(host.Services, openingFloat: 1m);                     // a new shift holding only 1.00

        var refused = await RefundAsync(host.Services, saleId, 1m);

        Assert.Equal("POS.Refund.NotEnoughCash", refused.Error.Code);
        Assert.Equal(0, await CountAsync(host, "sal_Returns"));                          // the transaction rolled everything back
        Assert.Equal(8m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(0, await CountAsync(host, "pay_Payments", "Direction = 2"));
    }

    [Fact]
    public async Task When_a_step_fails_the_return_the_stock_and_the_refund_are_not_saved()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var saleId = await SellAsync(host, shop, 2m);

        await using (await FailInsertsAsync(host, "cash_Movements"))
        {
            var failed = await RefundAsync(host.Services, saleId, 1m);
            Assert.True(failed.IsFailure);
        }

        Assert.Equal(0, await CountAsync(host, "sal_Returns"));
        Assert.Equal(0, await CountAsync(host, "sal_ReturnItems"));
        Assert.Equal(8m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(0, await CountAsync(host, "pay_Payments", "Direction = 2"));

        Assert.True((await RefundAsync(host.Services, saleId, 1m)).IsSuccess);          // works again once the database does
    }

    [Fact]
    public async Task A_sale_past_the_shops_limit_needs_the_late_returns_permission()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var saleId = await SellAsync(host, shop, 2m);
        await using (var connection = await host.OpenDatabaseAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sal_Sales SET CompletedAt = $at WHERE Id = $id";
            command.Parameters.AddWithValue("$at", DateTime.UtcNow.AddDays(-20).ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF"));
            command.Parameters.AddWithValue("$id", saleId.ToString().ToUpperInvariant());
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        // a cashier who may sell, but not accept late returns
        const string password = "till drawer passphrase 42";
        var cashier = await InScopeAsync(host.Services, sp => sp.GetRequiredService<CreateUserCommandHandler>().HandleAsync(new CreateUserCommand("cashier", "Cashier")));
        var role = await InScopeAsync(host.Services, sp => sp.GetRequiredService<CreateRoleCommandHandler>().HandleAsync(new CreateRoleCommand("Cashier")));
        foreach (var capability in new[] { POSCapabilities.ManageSession, POSCapabilities.CreateSale })
            await InScopeAsync(host.Services, sp => sp.GetRequiredService<GrantPermissionCommandHandler>().HandleAsync(new GrantPermissionCommand(role.Value, capability)));
        await InScopeAsync(host.Services, sp => sp.GetRequiredService<AssignRoleCommandHandler>().HandleAsync(new AssignRoleCommand(cashier.Value, role.Value)));
        await InScopeAsync(host.Services, sp => sp.GetRequiredService<SetUserPasswordCommandHandler>().HandleAsync(new SetUserPasswordCommand(cashier.Value, password, false)));
        await InScopeAsync(host.Services, sp => sp.GetRequiredService<SignInCommandHandler>().HandleAsync(new SignInCommand("cashier", password)));

        var refused = await RefundAsync(host.Services, saleId, 1m);
        Assert.Equal("POS.Refund.TooLate", refused.Error.Code);

        await host.SignInAdministratorAsync();                                            // the administrator holds every permission
        Assert.True((await RefundAsync(host.Services, saleId, 1m)).IsSuccess);
    }

    [Fact]
    public async Task Through_the_POS_contracts_the_till_finds_the_sale_by_its_receipt_and_its_preview_is_what_is_refunded()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var saleId = await SellAsync(host, shop, 3m, lineDiscount: 1m);                  // 6.50

        var found = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSReader>().FindReturnableSaleAsync(saleId.ToString("N")[..8]));
        Assert.True(found.IsSuccess, found.ErrorMessage);
        var sale = found.Sale!;
        Assert.Equal((saleId, "S-" + saleId.ToString("N")[..8].ToUpperInvariant(), false, 14), (sale.SaleId, sale.ReceiptNumber, sale.IsLate, sale.DaysAllowed));
        var line = Assert.Single(sale.Lines);
        var preview = line.RefundFor(1m);

        var refund = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSService>().RefundSaleAsync(saleId,
            [new POSRefundLineRequest(line.SaleItemId, 1m)], "Changed mind", POSPaymentMethod.Cash));
        Assert.True(refund.IsSuccess, refund.ErrorMessage);
        Assert.Equal((2.17m, 2.17m), (preview, refund.RefundTotal));

        var after = (await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSReader>().GetReturnableSaleAsync(saleId)))!.Lines[0];
        Assert.Equal((2m, 4.33m), (after.RemainingQuantity, after.RefundFor(2m)));     // what is left of 6.50
        Assert.Equal("POS.Return.ReceiptNotFound",
            (await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSReader>().FindReturnableSaleAsync("S-FFFFFFFF"))).ErrorCode);

        var noReason = await InScopeAsync(host.Services, sp => sp.GetRequiredService<IPOSService>().RefundSaleAsync(saleId,
            [new POSRefundLineRequest(line.SaleItemId, 1m)], " ", POSPaymentMethod.Cash));
        Assert.Equal("POS.Refund.ReasonRequired", noReason.ErrorCode);
    }

    [Fact]
    public async Task The_receipt_number_finds_the_sale_and_a_return_needs_a_reason()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services, salePrice: 2.5m, stock: 10m);
        var saleId = await SellAsync(host, shop, 1m);
        var printed = "S-" + saleId.ToString("N")[..8].ToUpperInvariant();

        var found = await InScopeAsync(host.Services, sp => sp.GetRequiredService<ISalesReader>().FindCompletedByReceiptNumberAsync(printed.ToLowerInvariant()));
        Assert.Equal([saleId], found);
        Assert.Empty(await InScopeAsync(host.Services, sp => sp.GetRequiredService<ISalesReader>().FindCompletedByReceiptNumberAsync("S-123")));

        var noReason = await RefundAsync(host.Services, saleId, 1m, reason: "  ");
        Assert.Equal("POS.Refund.ReasonRequired", noReason.Error.Code);
    }
}
