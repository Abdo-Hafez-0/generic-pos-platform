using Payments.Domain.Entities;
using Payments.Domain.Enums;
using Payments.Domain.ValueObjects;

namespace Payments.Tests.Domain;

public sealed class PaymentDomainTests
{
    private static readonly Guid Ref = Guid.NewGuid();

    private static Payment Cash(decimal amount = 25m, decimal? tendered = null)
    {
        var r = Payment.Record("Sale", Ref, amount, PaymentMethod.Cash, null, tendered, "cashier-1");
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    [Fact]
    public void Record_NormalisesReferenceType_AndStartsRecorded()
    {
        var p = Cash();

        Assert.Equal("sale", p.ReferenceType);
        Assert.Equal(Ref, p.ReferenceId);
        Assert.Equal(25m, p.Amount.Amount);
        Assert.Equal(PaymentStatus.Recorded, p.Status);
        Assert.Equal("cashier-1", p.RecordedBy);
        Assert.NotEqual(PaymentId.Empty, p.Id);
        Assert.Null(p.VoidedAt);
    }

    [Fact]
    public void Record_RoundsAmountToFourDecimals()
        => Assert.Equal(1.2346m, Cash(1.23456m).Amount.Amount);

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Record_AmountMustBePositive(double amount)
        => Assert.Equal("Payments.Payment.InvalidAmount", Payment.Record("sale", Ref, (decimal)amount, PaymentMethod.Cash).Error.Code);

    [Fact]
    public void Record_ValidatesReference_AndMethod()
    {
        Assert.Equal("Payments.Payment.ReferenceTypeRequired", Payment.Record(" ", Ref, 1m, PaymentMethod.Cash).Error.Code);
        Assert.Equal("Payments.Payment.ReferenceTypeTooLong", Payment.Record(new string('x', 51), Ref, 1m, PaymentMethod.Cash).Error.Code);
        Assert.Equal("Payments.Payment.ReferenceRequired", Payment.Record("sale", Guid.Empty, 1m, PaymentMethod.Cash).Error.Code);
        Assert.Equal("Payments.Payment.MethodInvalid", Payment.Record("sale", Ref, 1m, (PaymentMethod)99).Error.Code);
    }

    [Fact]
    public void OtherMethod_RequiresADescription()
    {
        Assert.Equal("Payments.Payment.MethodDetailRequired", Payment.Record("sale", Ref, 1m, PaymentMethod.Other).Error.Code);
        Assert.Equal("Payments.Payment.MethodDetailRequired", Payment.Record("sale", Ref, 1m, PaymentMethod.Other, "  ").Error.Code);
        var ok = Payment.Record("sale", Ref, 1m, PaymentMethod.Other, " Bank transfer ");
        Assert.True(ok.IsSuccess);
        Assert.Equal("Bank transfer", ok.Value.MethodDetail);
    }

    [Fact]
    public void Card_AndCash_DoNotNeedADetail_ButItIsLengthLimited()
    {
        Assert.True(Payment.Record("sale", Ref, 1m, PaymentMethod.Card).IsSuccess);
        Assert.True(Payment.Record("sale", Ref, 1m, PaymentMethod.Card, "Visa ****1234").IsSuccess);
        Assert.Equal("Payments.Payment.MethodDetailTooLong", Payment.Record("sale", Ref, 1m, PaymentMethod.Card, new string('x', 101)).Error.Code);
        Assert.Equal("Payments.Payment.RecordedByTooLong", Payment.Record("sale", Ref, 1m, PaymentMethod.Cash, null, null, new string('x', 101)).Error.Code);
    }

    [Fact]
    public void CashTendered_MustCoverTheAmount_AndComputesChange()
    {
        var exact = Cash(25m, 25m);
        var over = Cash(25m, 40m);

        Assert.Equal(0m, exact.ChangeDue);
        Assert.Equal(15m, over.ChangeDue);
        Assert.Equal(40m, over.TenderedAmount);
        Assert.Equal("Payments.Payment.TenderedTooLow", Payment.Record("sale", Ref, 25m, PaymentMethod.Cash, null, 24.99m).Error.Code);
    }

    [Fact]
    public void Tendered_OnlyApplies_ToCash()
        => Assert.Equal("Payments.Payment.TenderedOnlyForCash", Payment.Record("sale", Ref, 10m, PaymentMethod.Card, null, 10m).Error.Code);

    [Fact]
    public void NoTendered_MeansNoChange()
        => Assert.Equal(0m, Cash(25m, null).ChangeDue);

    [Fact]
    public void Void_RequiresAReason_IsTerminal_AndKeepsTheRecord()
    {
        var p = Cash();

        Assert.Equal("Payments.Payment.VoidReasonRequired", p.Void(" ").Error.Code);
        Assert.Equal("Payments.Payment.VoidReasonTooLong", p.Void(new string('x', 501)).Error.Code);
        Assert.Equal(PaymentStatus.Recorded, p.Status);

        Assert.True(p.Void("  customer cancelled ").IsSuccess);
        Assert.Equal(PaymentStatus.Voided, p.Status);
        Assert.Equal("customer cancelled", p.VoidReason);
        Assert.NotNull(p.VoidedAt);
        Assert.Equal(25m, p.Amount.Amount);                     // the original amount is kept for history
        Assert.Equal("Payments.Payment.AlreadyVoided", p.Void("again").Error.Code);
    }

    [Fact]
    public void Money_CreatePositive()
    {
        Assert.Equal("Payments.Payment.InvalidAmount", Money.CreatePositive(0m).Error.Code);
        Assert.Equal(2m, Money.CreatePositive(2m).Value.Amount);
        Assert.NotEqual(PaymentId.New(), PaymentId.New());
    }
}
