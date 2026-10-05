using Pricing.Domain.Entities;
using Pricing.Domain.Enums;
using Pricing.Domain.Services;
using Pricing.Domain.ValueObjects;

namespace Pricing.Tests.Domain;

public sealed class PricingDomainTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Product = Guid.NewGuid();
    private static readonly PriceListId List = PriceListId.New();

    private static Price NewPrice(decimal amount = 10m, decimal minQty = 1m, DateTime? from = null, DateTime? to = null, Guid? product = null, PriceListId? list = null)
    {
        var r = Price.Create(list ?? List, product ?? Product, amount, minQty, from ?? Jan1, to);
        Assert.True(r.IsSuccess, r.IsFailure ? r.Error.ToString() : null);
        return r.Value;
    }

    // --- price list ---

    [Fact]
    public void PriceList_Create_NormalisesCode_AndStartsActive()
    {
        var l = PriceList.Create(" retail ", " Retail prices ", true).Value;

        Assert.Equal("RETAIL", l.Code);
        Assert.Equal("Retail prices", l.Name);
        Assert.True(l.IsDefault);
        Assert.Equal(PriceListStatus.Active, l.Status);
    }

    [Theory]
    [InlineData("", "N", "Pricing.PriceList.CodeRequired")]
    [InlineData("C", " ", "Pricing.PriceList.NameRequired")]
    public void PriceList_Create_Validates(string code, string name, string expected)
        => Assert.Equal(expected, PriceList.Create(code, name, false).Error.Code);

    [Fact]
    public void PriceList_Create_RejectsTooLongValues()
    {
        Assert.Equal("Pricing.PriceList.CodeTooLong", PriceList.Create(new string('x', 31), "N", false).Error.Code);
        Assert.Equal("Pricing.PriceList.NameTooLong", PriceList.Create("C", new string('x', 201), false).Error.Code);
    }

    [Fact]
    public void PriceList_DefaultCannotBeDeactivated_ButAnotherListCan()
    {
        var def = PriceList.Create("A", "A", true).Value;
        var other = PriceList.Create("B", "B", false).Value;

        Assert.Equal("Pricing.PriceList.DefaultCannotBeDeactivated", def.Deactivate().Error.Code);
        Assert.True(other.Deactivate().IsSuccess);
        Assert.Equal("Pricing.PriceList.AlreadyInactive", other.Deactivate().Error.Code);
        Assert.Equal("Pricing.PriceList.Inactive", other.MakeDefault().Error.Code);
    }

    // --- price ---

    [Fact]
    public void Price_Create_RoundsAmount_AndStartsActive()
    {
        var p = NewPrice(12.34567m);

        Assert.Equal(12.3457m, p.Amount.Amount);
        Assert.Equal(PriceStatus.Active, p.Status);
        Assert.Equal(Product, p.ProductId);
        Assert.Equal(List, p.PriceListId);
    }

    [Fact]
    public void Price_Create_Validates()
    {
        Assert.Equal("Pricing.Price.PriceListRequired", Price.Create(PriceListId.Empty, Product, 1m, 1m, Jan1, null).Error.Code);
        Assert.Equal("Pricing.Price.ProductRequired", Price.Create(List, Guid.Empty, 1m, 1m, Jan1, null).Error.Code);
        Assert.Equal("Pricing.Price.NegativeAmount", Price.Create(List, Product, -1m, 1m, Jan1, null).Error.Code);
        Assert.Equal("Pricing.Price.InvalidMinimumQuantity", Price.Create(List, Product, 1m, 0m, Jan1, null).Error.Code);
        Assert.Equal("Pricing.Price.InvalidPeriod", Price.Create(List, Product, 1m, 1m, Jan1, Jan1).Error.Code);
        Assert.Equal("Pricing.Price.InvalidPeriod", Price.Create(List, Product, 1m, 1m, Jan1, Jan1.AddDays(-1)).Error.Code);
        Assert.True(Price.Create(List, Product, 0m, 1m, Jan1, null).IsSuccess);   // a free price is legal
    }

    [Fact]
    public void Money_RejectsNegative()
        => Assert.Equal("Pricing.Price.NegativeAmount", Money.Create(-0.01m).Error.Code);

    [Fact]
    public void Price_Update_ChangesValues_AndRevalidates()
    {
        var p = NewPrice(10m);

        Assert.True(p.Update(15m, 5m, Jan1.AddDays(10), Jan1.AddDays(20)).IsSuccess);
        Assert.Equal(15m, p.Amount.Amount);
        Assert.Equal(5m, p.MinimumQuantity);
        Assert.Equal(Jan1.AddDays(20), p.EffectiveTo);

        Assert.Equal("Pricing.Price.NegativeAmount", p.Update(-1m, 5m, Jan1, null).Error.Code);
        Assert.Equal(15m, p.Amount.Amount);
    }

    [Fact]
    public void Price_Deactivate_Reactivate_AndInactivePricesCannotBeEdited()
    {
        var p = NewPrice();

        Assert.True(p.Deactivate().IsSuccess);
        Assert.Equal("Pricing.Price.AlreadyInactive", p.Deactivate().Error.Code);
        Assert.Equal("Pricing.Price.Inactive", p.Update(1m, 1m, Jan1, null).Error.Code);
        Assert.True(p.Reactivate().IsSuccess);
        Assert.Equal("Pricing.Price.AlreadyActive", p.Reactivate().Error.Code);
    }

    [Fact]
    public void AppliesAt_RespectsStatusPeriodAndQuantity_WithHalfOpenPeriod()
    {
        var p = NewPrice(10m, 5m, Jan1, Jan1.AddDays(10));

        Assert.True(p.AppliesAt(Jan1, 5m));                       // From inclusive, exactly min quantity
        Assert.True(p.AppliesAt(Jan1.AddDays(9), 100m));
        Assert.False(p.AppliesAt(Jan1.AddDays(10), 5m));          // To exclusive
        Assert.False(p.AppliesAt(Jan1.AddTicks(-1), 5m));         // before From
        Assert.False(p.AppliesAt(Jan1, 4.99m));                   // below the quantity break
        p.Deactivate();
        Assert.False(p.AppliesAt(Jan1, 5m));
    }

    [Fact]
    public void OpenEndedPrice_AppliesForever()
        => Assert.True(NewPrice(1m, 1m, Jan1, null).AppliesAt(Jan1.AddYears(50), 1m));

    [Fact]
    public void Overlap_IsDetected_OnlyForTheSameProductListAndQuantityBreak()
    {
        var a = NewPrice(1m, 1m, Jan1, Jan1.AddDays(10));

        Assert.True(a.OverlapsWith(NewPrice(2m, 1m, Jan1.AddDays(5), Jan1.AddDays(15))));
        Assert.True(a.OverlapsWith(NewPrice(2m, 1m, Jan1.AddDays(-5), null)));
        Assert.False(a.OverlapsWith(NewPrice(2m, 1m, Jan1.AddDays(10), Jan1.AddDays(20))));   // touching, not overlapping
        Assert.False(a.OverlapsWith(NewPrice(2m, 5m, Jan1, Jan1.AddDays(10))));               // other quantity break
        Assert.False(a.OverlapsWith(NewPrice(2m, 1m, Jan1, Jan1.AddDays(10), product: Guid.NewGuid())));
        Assert.False(a.OverlapsWith(NewPrice(2m, 1m, Jan1, Jan1.AddDays(10), list: PriceListId.New())));
        Assert.False(a.OverlapsWith(a));
    }

    // --- selection ---

    [Fact]
    public void Selection_PicksTheBestQuantityBreak_ThenTheLatestStart()
    {
        var basePrice = NewPrice(10m, 1m, Jan1, null);
        var bulk = NewPrice(8m, 10m, Jan1, null);
        var bulkNewer = NewPrice(7m, 10m, Jan1.AddDays(30), null);   // (overlaps, but selection must still be deterministic)
        var all = new[] { basePrice, bulk, bulkNewer };

        Assert.Equal(10m, PriceSelection.Select(all, Jan1.AddDays(5), 1m)!.Amount.Amount);
        Assert.Equal(10m, PriceSelection.Select(all, Jan1.AddDays(5), 9m)!.Amount.Amount);
        Assert.Equal(8m, PriceSelection.Select(all, Jan1.AddDays(5), 10m)!.Amount.Amount);
        Assert.Equal(7m, PriceSelection.Select(all, Jan1.AddDays(40), 50m)!.Amount.Amount);
    }

    [Fact]
    public void Selection_ReturnsNull_WhenNothingApplies()
    {
        var scheduled = NewPrice(10m, 1m, Jan1.AddDays(30), null);

        Assert.Null(PriceSelection.Select([scheduled], Jan1, 1m));
        Assert.Null(PriceSelection.Select([], Jan1, 1m));
    }

    [Fact]
    public void Ids_AreUnique()
    {
        Assert.NotEqual(PriceId.New(), PriceId.New());
        Assert.NotEqual(PriceListId.New(), PriceListId.New());
        Assert.Equal(Guid.Empty, PriceId.Empty.Value);
    }
}
