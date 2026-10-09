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
        public int? FailAfter { get; set; }
        public Guid LastId { get; private set; }
        public List<Guid> Ids { get; } = [];

        public Task<RecordPaymentResult> RecordPaymentAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
        {
            if (FailRecord || Recorded.Count == FailAfter) return Task.FromResult(RecordPaymentResult.Failure("Payments.Test", "boom"));
            Recorded.Add(request);
            LastId = Guid.NewGuid();
            Ids.Add(LastId);
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

    // ------------------------------------------------------------------ FIX-10: split payments

    private static async Task<POSCheckoutResult> Split(PosTestDatabase db, Guid cart, params POSPaymentRequest[] parts)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutWithPaymentsAsync(cart, parts);
    }

    [Fact]
    public async Task A_split_payment_records_every_part_against_the_sale_and_only_cash_gives_change()
    {
        var payments = new StubPayments();
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, 10m, 2m);   // total 20

        var result = await Split(db, cart,
            new POSPaymentRequest(POSPaymentMethod.Card, MethodDetail: "approval 4711", Amount: 12.50m),
            new POSPaymentRequest(POSPaymentMethod.Other, MethodDetail: "voucher", Amount: 2.50m),
            new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 10m, Amount: 5m));

        Assert.True(result.IsSuccess, result.ErrorMessage);
        Assert.Equal([(PaymentMethodContract.Card, 12.5m, (decimal?)null, "approval 4711"), (PaymentMethodContract.Other, 2.5m, null, "voucher"), (PaymentMethodContract.Cash, 5m, 10m, null)],
            payments.Recorded.Select(r => (r.Method, r.Amount, r.TenderedAmount, r.MethodDetail)).ToArray());
        Assert.All(payments.Recorded, r => Assert.Equal((result.SaleId, "sale"), (r.ReferenceId, r.ReferenceType)));
        Assert.Equal(payments.Ids, result.PaymentIds);
        Assert.Equal(payments.Ids[0], result.PaymentId);
        Assert.Equal(5m, result.ChangeDue);
        Assert.Contains("complete", db.Sales.Calls);
    }

    [Theory]
    [InlineData(10, 9.99, "is still due")]
    [InlineData(10, 10.01, "more than the total")]
    public async Task Parts_that_do_not_add_up_to_the_total_are_refused_before_anything_is_written(decimal card, decimal cash, string words)
    {
        var payments = new StubPayments();
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, 10m, 2m);

        var result = await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, Amount: card), new POSPaymentRequest(POSPaymentMethod.Cash, Amount: cash));

        Assert.Equal("POS.Checkout.PaymentsDoNotMatchTotal", result.ErrorCode);
        Assert.Contains(words, result.ErrorMessage);
        Assert.Empty(payments.Recorded);
        Assert.Empty(db.Sales.Calls);
        Assert.Empty(db.Inventory.Issued);
    }

    [Fact]
    public async Task Each_part_is_checked_in_plain_words()
    {
        var payments = new StubPayments();
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, 10m, 2m);

        Assert.Equal("POS.Checkout.TenderedOnlyForCash", (await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, TenderedAmount: 25m, Amount: 20m))).ErrorCode);
        Assert.Equal("POS.Checkout.MethodDetailRequired", (await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Other, Amount: 20m))).ErrorCode);
        Assert.Equal("POS.Checkout.PaymentAmountInvalid", (await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, Amount: 20m), new POSPaymentRequest(POSPaymentMethod.Cash, Amount: 0m))).ErrorCode);
        Assert.Equal("POS.Checkout.PaymentAmountInvalid", (await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, Amount: 19.995m), new POSPaymentRequest(POSPaymentMethod.Cash, Amount: 0.005m))).ErrorCode);
        Assert.Equal("POS.Checkout.PaymentAmountInvalid", (await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card))).ErrorCode);   // a part needs its amount
        var tooLittle = await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, Amount: 15m), new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: 4m, Amount: 5m));
        Assert.Equal("POS.Checkout.TenderInsufficient", tooLittle.ErrorCode);
        Assert.Contains("its part", tooLittle.ErrorMessage);
        Assert.Empty(payments.Recorded);
        Assert.Empty(db.Sales.Calls);
    }

    [Fact]
    public async Task Without_a_transaction_a_failing_part_voids_the_parts_already_recorded()
    {
        var payments = new StubPayments { FailAfter = 1 };
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, 10m, 2m);

        var result = await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, Amount: 15m), new POSPaymentRequest(POSPaymentMethod.Cash, Amount: 5m));

        Assert.Equal("POS.Checkout.PaymentFailed", result.ErrorCode);
        Assert.Equal(payments.Ids, payments.Voided.Select(v => v.Id).ToArray());
        Assert.Contains("cancel", db.Sales.Calls);
        Assert.Empty(db.Inventory.Issued);
    }

    [Fact]
    public async Task Without_a_transaction_a_failed_stock_issue_voids_every_part()
    {
        var payments = new StubPayments();
        await using var db = await PosTestDatabase.CreateAsync(paymentService: payments);
        var cart = await CartWithOneItem(db, failIssue: true);

        var result = await Split(db, cart, new POSPaymentRequest(POSPaymentMethod.Card, Amount: 15m), new POSPaymentRequest(POSPaymentMethod.Cash, Amount: 5m));

        Assert.Equal("POS.Checkout.StockIssueFailed", result.ErrorCode);
        Assert.Equal(2, payments.Voided.Count);
    }
}
