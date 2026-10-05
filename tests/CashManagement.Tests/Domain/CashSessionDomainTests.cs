using CashManagement.Domain.Entities;
using CashManagement.Domain.Enums;
using CashManagement.Domain.ValueObjects;

namespace CashManagement.Tests.Domain;

public sealed class CashSessionDomainTests
{
    private static CashSession Open(decimal floatAmount = 100m, string drawer = "main")
    {
        var r = CashSession.Open(drawer, "ann", floatAmount);
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    [Fact]
    public void Open_NormalisesDrawer_AndStartsOpenWithTheFloatAsBalance()
    {
        var s = CashSession.Open(" main ", " ann ", 100.123456m, "  start  ").Value;

        Assert.Equal("MAIN", s.DrawerCode);
        Assert.Equal("ann", s.OpenedBy);
        Assert.Equal(100.1235m, s.OpeningFloat.Value);
        Assert.Equal(100.1235m, s.Balance);
        Assert.Equal(CashSessionStatus.Open, s.Status);
        Assert.Equal("start", s.Notes);
        Assert.Null(s.ClosedAt);
        Assert.Null(s.Variance);
        Assert.NotEqual(CashSessionId.Empty, s.Id);
    }

    [Fact]
    public void Open_ValidatesInput()
    {
        Assert.Equal("CashManagement.Session.DrawerRequired", CashSession.Open(" ", "a", 0m).Error.Code);
        Assert.Equal("CashManagement.Session.DrawerTooLong", CashSession.Open(new string('x', 31), "a", 0m).Error.Code);
        Assert.Equal("CashManagement.Session.OpenedByRequired", CashSession.Open("m", "", 0m).Error.Code);
        Assert.Equal("CashManagement.Session.OpenedByTooLong", CashSession.Open("m", new string('x', 101), 0m).Error.Code);
        Assert.Equal("CashManagement.Session.FloatNegative", CashSession.Open("m", "a", -1m).Error.Code);
        Assert.Equal("CashManagement.Session.NotesTooLong", CashSession.Open("m", "a", 0m, new string('x', 501)).Error.Code);
        Assert.True(CashSession.Open("m", "a", 0m).IsSuccess);   // a zero float is fine
    }

    [Fact]
    public void Movements_ChangeTheBalanceByTheirDirection()
    {
        var s = Open(100m);

        Assert.True(s.RecordMovement(CashMovementKind.PayIn, 50m, "more change").IsSuccess);
        Assert.True(s.RecordMovement(CashMovementKind.CashSale, 20m).IsSuccess);
        Assert.True(s.RecordMovement(CashMovementKind.PayOut, 30m, "supplier").IsSuccess);
        Assert.True(s.RecordMovement(CashMovementKind.CashRefund, 5m).IsSuccess);

        Assert.Equal(135m, s.Balance);
        Assert.Equal(4, s.Movements.Count);
        Assert.Equal(-30m, s.Movements.Single(m => m.Kind == CashMovementKind.PayOut).SignedAmount);
        Assert.Equal(20m, s.Movements.Single(m => m.Kind == CashMovementKind.CashSale).SignedAmount);
    }

    [Fact]
    public void RecordMovement_ValidatesAmountKindAndReason()
    {
        var s = Open();

        Assert.Equal("CashManagement.Movement.InvalidAmount", s.RecordMovement(CashMovementKind.CashSale, 0m).Error.Code);
        Assert.Equal("CashManagement.Movement.InvalidAmount", s.RecordMovement(CashMovementKind.CashSale, -1m).Error.Code);
        Assert.Equal("CashManagement.Movement.KindInvalid", s.RecordMovement((CashMovementKind)99, 1m).Error.Code);
        Assert.Equal("CashManagement.Movement.ReasonRequired", s.RecordMovement(CashMovementKind.PayIn, 1m).Error.Code);
        Assert.Equal("CashManagement.Movement.ReasonRequired", s.RecordMovement(CashMovementKind.PayOut, 1m, " ").Error.Code);
        Assert.Equal("CashManagement.Movement.ReasonTooLong", s.RecordMovement(CashMovementKind.PayIn, 1m, new string('x', 201)).Error.Code);
        Assert.Equal("CashManagement.Movement.RecordedByTooLong", s.RecordMovement(CashMovementKind.CashSale, 1m, recordedBy: new string('x', 101)).Error.Code);
        Assert.Empty(s.Movements);
    }

    [Fact]
    public void RecordMovement_RoundsTheAmount()
    {
        var s = Open(0m);

        s.RecordMovement(CashMovementKind.CashSale, 1.23456m);

        Assert.Equal(1.2346m, s.Balance);
    }

    [Fact]
    public void Outflows_CannotTakeTheDrawerBelowZero()
    {
        var s = Open(10m);

        var tooMuch = s.RecordMovement(CashMovementKind.PayOut, 10.01m, "x");
        var refund = s.RecordMovement(CashMovementKind.CashRefund, 11m);
        var exact = s.RecordMovement(CashMovementKind.PayOut, 10m, "x");

        Assert.Equal("CashManagement.Movement.InsufficientCash", tooMuch.Error.Code);
        Assert.Equal("CashManagement.Movement.InsufficientCash", refund.Error.Code);
        Assert.True(exact.IsSuccess);
        Assert.Equal(0m, s.Balance);
        Assert.Single(s.Movements);
    }

    [Fact]
    public void ReferenceNeedsBothTypeAndId_AndIsNormalised()
    {
        var s = Open();
        var id = Guid.NewGuid();

        Assert.Equal("CashManagement.Movement.ReferenceIncomplete", s.RecordMovement(CashMovementKind.CashSale, 1m, referenceType: "sale").Error.Code);
        Assert.Equal("CashManagement.Movement.ReferenceIncomplete", s.RecordMovement(CashMovementKind.CashSale, 1m, referenceId: id).Error.Code);
        Assert.Equal("CashManagement.Movement.ReferenceIncomplete", s.RecordMovement(CashMovementKind.CashSale, 1m, referenceType: "sale", referenceId: Guid.Empty).Error.Code);
        Assert.Equal("CashManagement.Movement.ReferenceTypeTooLong", s.RecordMovement(CashMovementKind.CashSale, 1m, referenceType: new string('x', 51), referenceId: id).Error.Code);

        var ok = s.RecordMovement(CashMovementKind.CashSale, 1m, referenceType: " Sale ", referenceId: id).Value;
        Assert.Equal("sale", ok.ReferenceType);
        Assert.Equal(id, ok.ReferenceId);
    }

    [Fact]
    public void ARepeatedReference_IsRejectedPerKind_SoRetriesNeverDoubleCount()
    {
        var s = Open();
        var id = Guid.NewGuid();
        s.RecordMovement(CashMovementKind.CashSale, 10m, referenceType: "sale", referenceId: id);

        var again = s.RecordMovement(CashMovementKind.CashSale, 10m, referenceType: "SALE", referenceId: id);
        var refund = s.RecordMovement(CashMovementKind.CashRefund, 10m, referenceType: "sale", referenceId: id);   // a different kind is fine

        Assert.Equal("CashManagement.Movement.DuplicateReference", again.Error.Code);
        Assert.True(refund.IsSuccess);
        Assert.Equal(2, s.Movements.Count);
    }

    [Fact]
    public void Close_StoresExpectedCountedAndVariance()
    {
        var s = Open(100m);
        s.RecordMovement(CashMovementKind.CashSale, 50m);

        Assert.True(s.Close(145m, " bob ", "short 5").IsSuccess);

        Assert.Equal(CashSessionStatus.Closed, s.Status);
        Assert.Equal(150m, s.ExpectedAmount!.Value.Value);
        Assert.Equal(145m, s.CountedAmount!.Value.Value);
        Assert.Equal(-5m, s.Variance);
        Assert.Equal("bob", s.ClosedBy);
        Assert.NotNull(s.ClosedAt);
        Assert.Equal("short 5", s.Notes);
    }

    [Fact]
    public void Close_AppendsToExistingNotes_AndAnOverageIsPositive()
    {
        var s = CashSession.Open("m", "a", 10m, "opening note").Value;

        s.Close(12m, "b", "closing note");

        Assert.Equal("opening note\nclosing note", s.Notes);
        Assert.Equal(2m, s.Variance);
    }

    [Fact]
    public void Close_ValidatesInput_AndLeavesTheSessionOpenOnFailure()
    {
        var s = Open();

        Assert.Equal("CashManagement.Session.CountedNegative", s.Close(-1m, "b").Error.Code);
        Assert.Equal("CashManagement.Session.ClosedByRequired", s.Close(1m, " ").Error.Code);
        Assert.Equal("CashManagement.Session.ClosedByTooLong", s.Close(1m, new string('x', 101)).Error.Code);
        Assert.Equal("CashManagement.Session.NotesTooLong", s.Close(1m, "b", new string('x', 501)).Error.Code);
        Assert.Equal(CashSessionStatus.Open, s.Status);
        Assert.Null(s.Variance);
    }

    [Fact]
    public void AClosedSession_RejectsMovementsAndASecondClose()
    {
        var s = Open();
        s.Close(100m, "b");

        Assert.Equal("CashManagement.Session.NotOpen", s.RecordMovement(CashMovementKind.CashSale, 1m).Error.Code);
        Assert.Equal("CashManagement.Session.NotOpen", s.Close(100m, "b").Error.Code);
    }

    [Fact]
    public void Ids_AreUnique()
    {
        Assert.NotEqual(CashSessionId.New(), CashSessionId.New());
        Assert.NotEqual(CashMovementId.New(), CashMovementId.New());
        Assert.Equal(0m, CashAmount.Zero.Value);
    }
}
