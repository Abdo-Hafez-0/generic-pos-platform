using Microsoft.Extensions.DependencyInjection;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Platform.Application.Abstractions.Auditing;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Hardware;
using POS.Application.Security;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Tests.Common.Hardware;
using Tests.Common.Security;

namespace POS.Tests.Application;

/// <summary>
/// MISS-07c: a receipt printed again is marked as a copy, shows the payments as they were recorded, and goes to the audit log
/// (a failed one too); the till lists its recent sales to choose from; both need pos.receipt.reprint.
/// </summary>
public sealed class PosReprintTests
{
    private sealed class Events : IBusinessEventSink
    {
        public List<BusinessEvent> Recorded { get; } = [];

        public Task RecordAsync(BusinessEvent businessEvent, CancellationToken cancellationToken = default)
        {
            Recorded.Add(businessEvent);
            return Task.CompletedTask;
        }
    }

    /// <summary>Payments as the Payments module would read them back for a sale.</summary>
    private sealed class PaymentsReader : IPaymentReader
    {
        public List<PaymentResult> Payments { get; } = [];

        public Task<IReadOnlyList<PaymentResult>> GetPaymentsForReferenceAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PaymentResult>>(Payments.Where(p => p.ReferenceType == referenceType && p.ReferenceId == referenceId).ToList());

        public Task<decimal> GetTotalPaidAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
            => Task.FromResult(Payments.Where(p => p.ReferenceId == referenceId && p.Status == PaymentStatusContract.Recorded).Sum(p => p.Amount));
    }

    private sealed record Kit(PosTestDatabase Db, FakeReceiptPrinter Printer, PaymentsReader Payments, Events Events) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static async Task<Kit> StartAsync(IAuthorizationService? authorization = null)
    {
        var printer = new FakeReceiptPrinter();
        var payments = new PaymentsReader();
        var events = new Events();
        var db = await PosTestDatabase.CreateAsync(configureServices: s =>
        {
            s.AddSingleton<IReceiptPrinter>(printer);
            s.AddSingleton<IPaymentReader>(payments);
            s.AddSingleton<IBusinessEventSink>(events);
            if (authorization is not null) s.AddSingleton(authorization);
        });
        return new Kit(db, printer, payments, events);
    }

    private static async Task<(Guid CartId, Guid SaleId)> SellAsync(PosTestDatabase db, string sku, decimal qty, decimal price, string cashier = "cashier-7")
    {
        var product = db.Catalog.Register(sku, "Item " + sku, price, null);
        db.Inventory.SetStock(product, 100m);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await service.OpenSessionAsync(cashier, Guid.NewGuid());
        var cart = await service.StartCartAsync(session.SessionId);
        Assert.True((await service.AddProductAsync(cart.CartId, sku, qty)).IsSuccess);
        var sale = await service.CheckoutAsync(cart.CartId, null, null);
        Assert.True(sale.IsSuccess, sale.ErrorMessage);
        await service.CloseSessionAsync(session.SessionId);
        return (cart.CartId, sale.SaleId);
    }

    private static PaymentResult Paid(Guid saleId, decimal amount, PaymentMethodContract method, decimal? tendered = null, PaymentStatusContract status = PaymentStatusContract.Recorded, int minute = 0)
        => new(Guid.NewGuid(), "sale", saleId, amount, method, null, tendered, status, new DateTime(2026, 10, 14, 10, minute, 0, DateTimeKind.Utc));

    private static async Task<POSOperationResult> ReprintAsync(PosTestDatabase db, Guid cartId)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSDevices>().PrintReceiptAsync(cartId);
    }

    [Fact]
    public async Task AReprint_IsMarkedAsACopy_WithThePaymentsAsRecorded_AndIsAudited()
    {
        await using var kit = await StartAsync();
        var (cart, sale) = await SellAsync(kit.Db, "SKU-1", 3m, 10m);
        kit.Printer.Printed.Clear();   // the receipt printed at checkout is not a copy
        kit.Payments.Payments.Add(Paid(sale, 20m, PaymentMethodContract.Cash, tendered: 50m, minute: 1));
        kit.Payments.Payments.Add(Paid(sale, 10m, PaymentMethodContract.Card, minute: 2));
        kit.Payments.Payments.Add(Paid(sale, 99m, PaymentMethodContract.Card, status: PaymentStatusContract.Voided));   // voided: left out

        var reprint = await ReprintAsync(kit.Db, cart);

        Assert.True(reprint.IsSuccess, reprint.ErrorMessage);
        var copy = Assert.Single(kit.Printer.Printed);
        Assert.True(copy.IsCopy);
        Assert.Equal(30m, copy.Total);
        Assert.Equal("S-" + sale.ToString("N")[..8].ToUpperInvariant(), copy.Reference);
        var parts = copy.Payments!;
        Assert.Equal(2, parts.Count);
        Assert.Equal((20m, 50m, 30m), (parts[0].Amount, parts[0].Tendered, parts[0].Change));
        Assert.Equal(10m, parts[1].Amount);
        var audited = Assert.Single(kit.Events.Recorded, e => e.Action == "receipt.reprinted");
        Assert.Equal(POSCapabilities.Module, audited.Module);
        Assert.Equal(sale.ToString(), audited.EntityId);
        Assert.Contains("was printed", audited.Summary);
    }

    [Fact]
    public async Task AFailedReprint_IsAuditedToo()
    {
        await using var kit = await StartAsync();
        var (cart, _) = await SellAsync(kit.Db, "SKU-1", 1m, 5m);
        kit.Printer.Mode = FakeMode.Unavailable;

        var reprint = await ReprintAsync(kit.Db, cart);

        Assert.False(reprint.IsSuccess);
        Assert.Contains("could not be printed", Assert.Single(kit.Events.Recorded, e => e.Action == "receipt.reprinted").Summary);
    }

    [Fact]
    public async Task RecentSales_AreTheCheckedOutCartsSinceTheMoment_NewestFirst()
    {
        await using var kit = await StartAsync();
        var first = await SellAsync(kit.Db, "SKU-1", 2m, 10m, cashier: "amal");
        await Task.Delay(20);
        var second = await SellAsync(kit.Db, "SKU-2", 1m, 4.5m, cashier: "omar");
        using (var scope = kit.Db.CreateScope())   // an open cart is not a sale
        {
            var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
            var session = await service.OpenSessionAsync("amal", Guid.NewGuid());
            await service.StartCartAsync(session.SessionId);
        }

        using var read = kit.Db.CreateScope();
        var devices = read.ServiceProvider.GetRequiredService<IPOSDevices>();
        var recent = await devices.GetRecentReceiptsAsync(DateTime.UtcNow.AddHours(-1));

        Assert.True(recent.IsSuccess, recent.ErrorMessage);
        Assert.Equal([second.CartId, first.CartId], recent.Receipts.Select(r => r.CartId));
        var newest = recent.Receipts[0];
        Assert.Equal((second.SaleId, "S-" + second.SaleId.ToString("N")[..8].ToUpperInvariant(), 4.5m, 1, "omar"),
            (newest.SaleId, newest.Reference, newest.Total, newest.Lines, newest.Cashier));
        Assert.Equal("amal", recent.Receipts[1].Cashier);
        Assert.Empty((await devices.GetRecentReceiptsAsync(DateTime.UtcNow.AddMinutes(1))).Receipts);
    }

    [Fact]
    public async Task WithoutTheReprintPermission_NothingIsListedOrPrinted()
    {
        var authorization = new ScriptedAuthorizationService(POSCapabilities.ManageSession, POSCapabilities.CreateSale);   // sells, may not reprint
        await using var kit = await StartAsync(authorization);
        var (cart, _) = await SellAsync(kit.Db, "SKU-1", 1m, 5m);
        kit.Printer.Printed.Clear();
        kit.Events.Recorded.Clear();

        using var scope = kit.Db.CreateScope();
        var devices = scope.ServiceProvider.GetRequiredService<IPOSDevices>();
        var recent = await devices.GetRecentReceiptsAsync(DateTime.UtcNow.AddHours(-1));
        var reprint = await devices.PrintReceiptAsync(cart);

        Assert.False(recent.IsSuccess);
        Assert.Empty(recent.Receipts);
        Assert.False(reprint.IsSuccess);
        Assert.Empty(kit.Printer.Printed);
        Assert.DoesNotContain(kit.Events.Recorded, e => e.Action == "receipt.reprinted");
    }
}
