using Microsoft.Extensions.DependencyInjection;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace POS.Tests.Application;

/// <summary>
/// POS consumes Payments ONLY through Payments.Contracts and only OPTIONALLY: a checkout without a payment request never needs the module.
/// </summary>
public sealed class PosPaymentsIntegrationTests
{
    private sealed class StubPayments : IPaymentService
    {
        public List<RecordPaymentRequest> Recorded { get; } = [];
        public List<(Guid Id, string Reason)> Voided { get; } = [];
        public bool FailRecord { get; set; }
        public Guid LastId { get; private set; }

        public Task<RecordPaymentResult> RecordPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (FailRecord) return Task.FromResult(RecordPaymentResult.Failure("Payments.Test", "boom"));
            Recorded.Add(request);
            LastId = Guid.NewGuid();
            return Task.FromResult(RecordPaymentResult.Success(LastId, request.TenderedAmount is { } t ? t - request.Amount : 0m));
        }

        public Task<PaymentOperationResult> VoidPaymentAsync(Guid paymentId, string reason, CancellationToken cancellationToken = default)
        {
            Voided.Add((paymentId, reason));
            return Task.FromResult(PaymentOperationResult.Success());
        }
    }

    private static async Task<Guid> CartWithOneItem(PosTestDatabase db, decimal price = 10m, decimal qty = 2m, bool failIssue = false)
    {
        var product = db.Catalog.Register("SKU-PAY", "Pay product", price);
        db.Inventory.SetStock(product, 100m);
        if (failIssue) db.Inventory.FailIssueFor(product);
        using var scope = db.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await service.OpenSessionAsync("cashier-7", Guid.NewGuid());
        var cart = await service.StartCartAsync(session.SessionId);
        Assert.True((await service.AddProductAsync(cart.CartId, "SKU-PAY", qty)).IsSuccess);
        return cart.CartId;
    }

    private static async Task<POSCheckoutResult> Checkout(PosTestDatabase db, Guid cart, POSPaymentRequest? payment)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cart, null, payment);
    }

    [Fact]
    public async Task CheckoutWithoutPayment_DoesNotNeedThePaymentsModule()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var cart = await CartWithOneItem(db);

        var result = await Checkout(db, cart, null);

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Null(result.PaymentId);
        Assert.Equal(0m, result.ChangeDue);
    }

    [Fact]
    public async Task PaymentRequested_ButModuleMissing_FailsBeforeTouchingAnything()
    {
        await using var db = await PosTestDatabase.CreateAsync();
        var cart = await CartWithOneItem(db);

        var result = await Checkout(db, cart, new POSPaymentRequest(POSPaymentMethod.Card));

        Assert.Equal("POS.Checkout.PaymentsUnavailable", result.ErrorCode);
        Assert.Empty(db.Sales.Calls);
        Assert.Empty(db.Inventory.Issued);
    }

    [Fact]
    public async Task CashPayment_RecordsTheCartTotal_AgainstTheSale_AndReturnsChange()
    {
        var payments = new StubPayments();
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, 10m, 2m);

        var result = await Checkout(db, cart, new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 50m));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        var recorded = Assert.Single(payments.Recorded);
        Assert.Equal(20m, recorded.Amount);
        Assert.Equal("sale", recorded.ReferenceType);
        Assert.Equal(result.SaleId, recorded.ReferenceId);
        Assert.Equal(PaymentMethodContract.Cash, recorded.Method);
        Assert.Equal("cashier-7", recorded.RecordedBy);
        Assert.Equal(payments.LastId, result.PaymentId);
        Assert.Equal(30m, result.ChangeDue);
        Assert.Empty(payments.Voided);
    }

    [Fact]
    public async Task InsufficientTender_FailsEarly_NothingRecorded_NothingSold()
    {
        var payments = new StubPayments();
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, 10m, 2m);

        var result = await Checkout(db, cart, new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 19m));

        Assert.Equal("POS.Checkout.TenderInsufficient", result.ErrorCode);
        Assert.Empty(payments.Recorded);
        Assert.Empty(db.Sales.Calls);
    }

    [Fact]
    public async Task PaymentFailure_CancelsTheSale_AndIssuesNoStock()
    {
        var payments = new StubPayments { FailRecord = true };
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db);

        var result = await Checkout(db, cart, new POSPaymentRequest(POSPaymentMethod.Card));

        Assert.Equal("POS.Checkout.PaymentFailed", result.ErrorCode);
        Assert.Contains("cancel", db.Sales.Calls);
        Assert.DoesNotContain("complete", db.Sales.Calls);
        Assert.Empty(db.Inventory.Issued);
    }

    [Fact]
    public async Task StockIssueFailure_VoidsTheRecordedPayment()
    {
        var payments = new StubPayments();
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, failIssue: true);

        var result = await Checkout(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, MethodDetail: "Visa"));

        Assert.Equal("POS.Checkout.StockIssueFailed", result.ErrorCode);
        var voided = Assert.Single(payments.Voided);
        Assert.Equal(payments.LastId, voided.Id);
        Assert.Contains("cancel", db.Sales.Calls);
    }
}
