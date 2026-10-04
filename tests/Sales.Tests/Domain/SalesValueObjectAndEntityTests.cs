using Sales.Domain.Entities;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;

namespace Sales.Tests.Domain;

public sealed class SalesValueObjectAndEntityTests
{
    // --- Money ---

    [Fact]
    public void Money_Create_RoundsToFourDecimals()
    {
        var result = Money.Create(1.23456m);

        Assert.True(result.IsSuccess);
        Assert.Equal(1.2346m, result.Value.Amount);
    }

    [Fact]
    public void Money_Create_Negative_Fails()
    {
        var result = Money.Create(-0.01m);

        Assert.True(result.IsFailure);
        Assert.Equal("Sales.Money.NegativeAmount", result.Error.Code);
    }

    [Fact]
    public void Money_Zero_AndArithmetic()
    {
        var a = new Money(10m);
        var b = new Money(4m);

        Assert.Equal(0m, Money.Zero.Amount);
        Assert.Equal(14m, (a + b).Amount);
        Assert.Equal(6m, (a - b).Amount);
        Assert.Equal(25m, (a * 2.5m).Amount);
        Assert.True(a > b);
        Assert.True(b < a);
        Assert.True(a >= new Money(10m));
        Assert.True(b <= new Money(4m));
    }

    // --- SaleQuantity ---

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SaleQuantity_NonPositive_Fails(double value)
    {
        var result = SaleQuantity.Create((decimal)value);

        Assert.True(result.IsFailure);
        Assert.Equal("Sales.SaleQuantity.MustBePositive", result.Error.Code);
    }

    [Fact]
    public void SaleQuantity_Positive_AllowsFractions()
    {
        var result = SaleQuantity.Create(0.25m);

        Assert.True(result.IsSuccess);
        Assert.Equal(0.25m, result.Value.Value);
        Assert.Equal(1m, SaleQuantity.One.Value);
    }

    // --- Ids ---

    [Fact]
    public void Ids_New_AreUnique_AndEmptyIsEmpty()
    {
        Assert.NotEqual(SaleId.New(), SaleId.New());
        Assert.Equal(Guid.Empty, SaleId.Empty.Value);
        Assert.Equal(Guid.Empty, SaleItemId.Empty.Value);
        Assert.Equal(Guid.Empty, ReturnId.Empty.Value);
        Assert.Equal(Guid.Empty, ReturnItemId.Empty.Value);
        Assert.NotEqual(ReturnId.New(), ReturnId.New());
    }

    // --- SalesTransaction ---

    [Fact]
    public void SalesTransaction_Record_CapturesTotals()
    {
        var saleId = SaleId.New();

        var result = SalesTransaction.Record(saleId, new Money(110m), new Money(10m), "  TX-1 ");

        Assert.True(result.IsSuccess);
        Assert.Equal(saleId, result.Value.SaleId);
        Assert.Equal(110m, result.Value.GrandTotal.Amount);
        Assert.Equal(10m, result.Value.TaxTotal.Amount);
        Assert.Equal("TX-1", result.Value.Reference);
        Assert.NotEqual(Guid.Empty, result.Value.Id);
    }

    [Fact]
    public void SalesTransaction_Record_EmptySale_Fails()
    {
        var result = SalesTransaction.Record(SaleId.Empty, new Money(1m), Money.Zero);

        Assert.Equal("Sales.SalesTransaction.SaleRequired", result.Error.Code);
    }

    [Fact]
    public void SalesTransaction_Record_TaxExceedsTotal_Fails()
    {
        var result = SalesTransaction.Record(SaleId.New(), new Money(5m), new Money(6m));

        Assert.Equal("Sales.SalesTransaction.TaxExceedsTotal", result.Error.Code);
    }

    [Fact]
    public void SalesTransaction_Record_ReferenceTooLong_Fails()
    {
        var result = SalesTransaction.Record(SaleId.New(), new Money(5m), Money.Zero, new string('x', 101));

        Assert.Equal("Sales.SalesTransaction.ReferenceTooLong", result.Error.Code);
    }

    // --- Return / ReturnItem ---

    private static Return NewReturn()
    {
        var result = Return.Create(SaleId.New(), "notes");
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private static Platform.Core.Results.Result<ReturnItem> AddReturnItem(Return r, string name = "Widget", Guid? productId = null)
        => r.AddItem(SaleItemId.New(), productId ?? Guid.NewGuid(), name, new SaleQuantity(2m), new Money(5m), "damaged");

    [Fact]
    public void Return_Create_StartsPending()
    {
        var r = NewReturn();

        Assert.Equal(ReturnStatus.Pending, r.Status);
        Assert.Empty(r.Items);
        Assert.Equal(0m, r.TotalRefundAmount.Amount);
    }

    [Fact]
    public void Return_Create_EmptySale_Fails()
    {
        Assert.Equal("Sales.Return.SaleRequired", Return.Create(SaleId.Empty).Error.Code);
    }

    [Fact]
    public void Return_AddItem_ComputesRefund()
    {
        var r = NewReturn();

        Assert.True(AddReturnItem(r).IsSuccess);
        Assert.True(AddReturnItem(r, "Other").IsSuccess);

        Assert.Equal(2, r.Items.Count);
        Assert.Equal(20m, r.TotalRefundAmount.Amount);
    }

    [Fact]
    public void Return_AddItem_InvalidInput_Fails()
    {
        var r = NewReturn();

        Assert.Equal("Sales.ReturnItem.ProductRequired", AddReturnItem(r, productId: Guid.Empty).Error.Code);
        Assert.Equal("Sales.ReturnItem.ProductNameRequired", AddReturnItem(r, name: " ").Error.Code);
        Assert.Equal("Sales.ReturnItem.OriginalSaleItemRequired",
            r.AddItem(SaleItemId.Empty, Guid.NewGuid(), "x", new SaleQuantity(1m), new Money(1m)).Error.Code);
    }

    [Fact]
    public void Return_Process_RequiresItems_ThenIsTerminal()
    {
        var r = NewReturn();
        Assert.Equal("Sales.Return.NoItems", r.Process().Error.Code);

        AddReturnItem(r);
        Assert.True(r.Process().IsSuccess);
        Assert.Equal(ReturnStatus.Processed, r.Status);
        Assert.NotNull(r.ProcessedAt);

        Assert.Equal("Sales.Return.CannotProcess", r.Process().Error.Code);
        Assert.Equal("Sales.Return.CannotReject", r.Reject("x").Error.Code);
        Assert.Equal("Sales.Return.NotPending", AddReturnItem(r).Error.Code);
    }

    [Fact]
    public void Return_Reject_RequiresReason_AndIsTerminal()
    {
        var r = NewReturn();
        Assert.Equal("Sales.Return.RejectionReasonRequired", r.Reject(" ").Error.Code);

        Assert.True(r.Reject("not eligible").IsSuccess);
        Assert.Equal(ReturnStatus.Rejected, r.Status);
        Assert.Contains("Rejected: not eligible", r.Notes);
        Assert.Equal("Sales.Return.CannotProcess", r.Process().Error.Code);
    }
}
