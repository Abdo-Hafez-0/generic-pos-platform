using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Payments.Application.Commands;
using Payments.Application.Queries;
using Payments.Contracts.Interfaces;
using Payments.Contracts.Models;
using Payments.Domain.Enums;
using Payments.Infrastructure.DependencyInjection;
using Payments.Infrastructure.Persistence;
using Tests.Common;

namespace Payments.Tests.Application;

public sealed class PaymentsApplicationTests
{
    private static readonly Guid Sale = Guid.NewGuid();

    private static Task<TestModuleDatabase<PaymentsDbContext>> NewDb()
        => TestModuleDatabase<PaymentsDbContext>.CreateAsync(s => s.AddPaymentsCore());

    private static Task<Platform.Core.Results.Result<RecordedPayment>> Record(
        TestModuleDatabase<PaymentsDbContext> db, decimal amount, PaymentMethod method = PaymentMethod.Cash, Guid? reference = null, decimal? tendered = null, string? detail = null)
        => db.InScopeAsync(sp => sp.GetRequiredService<RecordPaymentCommandHandler>()
            .HandleAsync(new RecordPaymentCommand("sale", reference ?? Sale, amount, method, detail, tendered, "cashier")));

    // ------------------------------------------------------------------ handlers

    [Fact]
    public async Task Record_PersistsThePayment_AndReturnsChange()
    {
        await using var db = await NewDb();

        var r = await Record(db, 25m, tendered: 40m);

        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        Assert.Equal(15m, r.Value.ChangeDue);
        var dto = await db.InScopeAsync(sp => sp.GetRequiredService<GetPaymentQueryHandler>().HandleAsync(new GetPaymentQuery(r.Value.PaymentId)));
        Assert.Equal(25m, dto!.Amount);
        Assert.Equal(PaymentStatus.Recorded, dto.Status);
        Assert.Equal(15m, dto.ChangeDue);
        Assert.Equal("sale", dto.ReferenceType);
    }

    [Fact]
    public async Task Record_InvalidInput_Fails_AndPersistsNothing()
    {
        await using var db = await NewDb();

        var r = await Record(db, 0m);

        Assert.Equal("Payments.Payment.InvalidAmount", r.Error.Code);
        Assert.Equal(0, await db.InScopeAsync(sp => sp.GetRequiredService<PaymentsDbContext>().Payments.CountAsync()));
    }

    [Fact]
    public async Task Void_MarksThePayment_AndFailuresAreReported()
    {
        await using var db = await NewDb();
        var id = (await Record(db, 10m)).Value.PaymentId;

        var ok = await db.InScopeAsync(sp => sp.GetRequiredService<VoidPaymentCommandHandler>().HandleAsync(new VoidPaymentCommand(id, "mistake")));
        var again = await db.InScopeAsync(sp => sp.GetRequiredService<VoidPaymentCommandHandler>().HandleAsync(new VoidPaymentCommand(id, "again")));
        var missing = await db.InScopeAsync(sp => sp.GetRequiredService<VoidPaymentCommandHandler>().HandleAsync(new VoidPaymentCommand(Guid.NewGuid(), "x")));

        Assert.True(ok.IsSuccess);
        Assert.Equal("Payments.Payment.AlreadyVoided", again.Error.Code);
        Assert.Equal("Payments.VoidPayment.PaymentNotFound", missing.Error.Code);
        var dto = await db.InScopeAsync(sp => sp.GetRequiredService<GetPaymentQueryHandler>().HandleAsync(new GetPaymentQuery(id)));
        Assert.Equal(PaymentStatus.Voided, dto!.Status);
        Assert.Equal("mistake", dto.VoidReason);
    }

    [Fact]
    public async Task GetPaymentsForReference_ReturnsOnlyThatReference_InOrder_AndHandlesBadInput()
    {
        await using var db = await NewDb();
        var other = Guid.NewGuid();
        await Record(db, 10m, PaymentMethod.Cash);
        await Task.Delay(5);
        await Record(db, 5m, PaymentMethod.Card);
        await Record(db, 99m, reference: other);

        var list = await db.InScopeAsync(sp => sp.GetRequiredService<GetPaymentsForReferenceQueryHandler>().HandleAsync(new GetPaymentsForReferenceQuery("SALE", Sale)));

        Assert.Equal([10m, 5m], list.Select(p => p.Amount).ToArray());
        Assert.Empty(await db.InScopeAsync(sp => sp.GetRequiredService<GetPaymentsForReferenceQueryHandler>().HandleAsync(new GetPaymentsForReferenceQuery("", Sale))));
        Assert.Empty(await db.InScopeAsync(sp => sp.GetRequiredService<GetPaymentsForReferenceQueryHandler>().HandleAsync(new GetPaymentsForReferenceQuery("sale", Guid.Empty))));
        Assert.Empty(await db.InScopeAsync(sp => sp.GetRequiredService<GetPaymentsForReferenceQueryHandler>().HandleAsync(new GetPaymentsForReferenceQuery("invoice", Sale))));
    }

    [Fact]
    public async Task SplitPayments_AreSupported_ForOneReference()
    {
        await using var db = await NewDb();
        await Record(db, 60m, PaymentMethod.Cash);
        await Record(db, 40m, PaymentMethod.Card, detail: "Visa");

        var total = await db.InScopeAsync(sp => sp.GetRequiredService<IPaymentReader>().GetTotalPaidAsync("sale", Sale));

        Assert.Equal(100m, total);
    }

    // ------------------------------------------------------------------ contracts

    [Fact]
    public async Task Contract_RecordAndVoid_TranslateResults()
    {
        await using var db = await NewDb();

        var ok = await db.InScopeAsync(sp => sp.GetRequiredService<IPaymentService>()
            .RecordPaymentAsync(new RecordPaymentRequest("sale", Sale, 30m, PaymentMethodContract.Cash, null, 50m, "c1")));
        var bad = await db.InScopeAsync(sp => sp.GetRequiredService<IPaymentService>()
            .RecordPaymentAsync(new RecordPaymentRequest("sale", Sale, -1m, PaymentMethodContract.Card)));
        var voided = await db.InScopeAsync(sp => sp.GetRequiredService<IPaymentService>().VoidPaymentAsync(ok.PaymentId, "oops"));
        var voidMissing = await db.InScopeAsync(sp => sp.GetRequiredService<IPaymentService>().VoidPaymentAsync(Guid.NewGuid(), "oops"));

        Assert.True(ok.IsSuccess);
        Assert.Equal(20m, ok.ChangeDue);
        Assert.NotEqual(Guid.Empty, ok.PaymentId);
        Assert.False(bad.IsSuccess);
        Assert.Equal(Guid.Empty, bad.PaymentId);
        Assert.Equal("Payments.Payment.InvalidAmount", bad.ErrorCode);
        Assert.True(voided.IsSuccess);
        Assert.False(voidMissing.IsSuccess);
        Assert.Equal("Payments.VoidPayment.PaymentNotFound", voidMissing.ErrorCode);
    }

    [Fact]
    public async Task Contract_Reader_ExcludesVoidedPaymentsFromTheTotal_ButStillListsThem()
    {
        await using var db = await NewDb();
        var a = (await Record(db, 70m)).Value.PaymentId;
        await Record(db, 30m, PaymentMethod.Card);
        await db.InScopeAsync(sp => sp.GetRequiredService<IPaymentService>().VoidPaymentAsync(a, "refunded"));

        var reader = await db.InScopeAsync(async sp =>
            (await sp.GetRequiredService<IPaymentReader>().GetPaymentsForReferenceAsync("sale", Sale),
             await sp.GetRequiredService<IPaymentReader>().GetTotalPaidAsync("sale", Sale)));

        Assert.Equal(2, reader.Item1.Count);
        Assert.Contains(reader.Item1, p => p.Status == PaymentStatusContract.Voided && p.Amount == 70m);
        Assert.Equal(30m, reader.Item2);
    }

    [Fact]
    public async Task Contract_MethodEnumsMatchTheDomain_AndNoDomainTypesLeak()
    {
        foreach (var name in Enum.GetNames<PaymentMethodContract>())
            Assert.Equal((int)Enum.Parse<PaymentMethod>(name), (int)Enum.Parse<PaymentMethodContract>(name));
        foreach (var name in Enum.GetNames<PaymentStatusContract>())
            Assert.Equal((int)Enum.Parse<PaymentStatus>(name), (int)Enum.Parse<PaymentStatusContract>(name));

        Assert.DoesNotContain(typeof(IPaymentService).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Payments.Domain", StringComparison.Ordinal));
        Assert.True(PaymentOperationResult.Success().IsSuccess);
        Assert.False(RecordPaymentResult.Failure("c", "m").IsSuccess);
        await Task.CompletedTask;
    }
}
