using Reporting.Domain.Calculations;
using Reporting.Domain.ValueObjects;

namespace Reporting.Tests.Domain;

public sealed class ReportingDomainTests
{
    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DateRange Range(int fromDay, int toDay)
        => DateRange.Create(Jan1.AddDays(fromDay - 1), Jan1.AddDays(toDay).AddTicks(-1)).Value;

    // ------------------------------------------------------------------ DateRange

    [Fact]
    public void DateRange_RequiresEndNotBeforeStart()
    {
        Assert.Equal("Reporting.Range.Invalid", DateRange.Create(Jan1.AddDays(1), Jan1).Error.Code);
        Assert.True(DateRange.Create(Jan1, Jan1).IsSuccess);
    }

    [Fact]
    public void DateRange_IsLimitedTo366Days()
    {
        Assert.True(DateRange.Create(Jan1, Jan1.AddDays(366)).IsSuccess);
        Assert.Equal("Reporting.Range.TooLong", DateRange.Create(Jan1, Jan1.AddDays(367)).Error.Code);
    }

    [Fact]
    public void DateRange_NormalisesToUtc_AndTreatsUnspecifiedAsUtc()
    {
        var local = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified);

        var range = DateRange.Create(local, local.AddHours(1)).Value;
        var plain = DateRange.Create(unspecified, unspecified.AddHours(1)).Value;

        Assert.Equal(local.ToUniversalTime(), range.From);
        Assert.Equal(DateTimeKind.Utc, range.To.Kind);
        Assert.Equal(DateTimeKind.Utc, plain.From.Kind);
        Assert.Equal(12, plain.From.Hour);
    }

    [Fact]
    public void DateRange_ContainsIsInclusiveAtBothEnds()
    {
        var range = DateRange.Create(Jan1, Jan1.AddDays(1)).Value;

        Assert.True(range.Contains(Jan1));
        Assert.True(range.Contains(Jan1.AddDays(1)));
        Assert.False(range.Contains(Jan1.AddTicks(-1)));
        Assert.False(range.Contains(Jan1.AddDays(1).AddTicks(1)));
    }

    // ------------------------------------------------------------------ SalesCalculator

    [Fact]
    public void Sales_TotalsCountAndAverage_IgnoringSalesOutsideTheRange()
    {
        var facts = new[]
        {
            new SaleFact(Jan1.AddHours(1), 10m),
            new SaleFact(Jan1.AddDays(1).AddHours(1), 20m),
            new SaleFact(Jan1.AddDays(1).AddHours(2), 5m),
            new SaleFact(Jan1.AddDays(10), 999m),          // outside
            new SaleFact(Jan1.AddTicks(-1), 999m)          // just before
        };

        var f = SalesCalculator.Calculate(facts, Range(1, 3));

        Assert.Equal(3, f.SaleCount);
        Assert.Equal(35m, f.GrandTotal);
        Assert.Equal(11.6667m, f.AverageSale);
    }

    [Fact]
    public void Sales_ListsEveryDayOfTheRange_IncludingEmptyOnes()
    {
        var facts = new[] { new SaleFact(Jan1.AddDays(1).AddHours(3), 8m) };

        var f = SalesCalculator.Calculate(facts, Range(1, 3));

        Assert.Equal(3, f.Days.Count);
        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3)], f.Days.Select(d => d.Date).ToArray());
        Assert.Equal([0, 1, 0], f.Days.Select(d => d.SaleCount).ToArray());
        Assert.Equal([0m, 8m, 0m], f.Days.Select(d => d.Total).ToArray());
    }

    [Fact]
    public void Sales_NoSales_GivesZeros_NotADivisionError()
    {
        var f = SalesCalculator.Calculate([], Range(1, 2));

        Assert.Equal(0, f.SaleCount);
        Assert.Equal(0m, f.GrandTotal);
        Assert.Equal(0m, f.AverageSale);
        Assert.Equal(2, f.Days.Count);
    }

    [Fact]
    public void Sales_AssignsADayInUtc_ForLocalTimestamps()
    {
        var localLate = new DateTime(2026, 1, 1, 23, 0, 0, DateTimeKind.Utc).ToLocalTime();

        var f = SalesCalculator.Calculate([new SaleFact(localLate, 4m)], Range(1, 1));

        Assert.Equal(1, f.SaleCount);
        Assert.Equal(1, f.Days.Single().SaleCount);
    }

    // ------------------------------------------------------------------ StockCalculator

    [Fact]
    public void Stock_CountsItemsProductsAndTotals()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var f = StockCalculator.Calculate([new StockFact(a, 5m), new StockFact(a, 3m), new StockFact(b, 2m)]);

        Assert.Equal(3, f.StockItemCount);
        Assert.Equal(2, f.ProductCount);
        Assert.Equal(10m, f.TotalOnHand);
        Assert.Equal(0, f.OutOfStockCount);
    }

    [Fact]
    public void Stock_AProductIsOutWhenItsTotalAcrossWarehousesIsZeroOrLess()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var f = StockCalculator.Calculate([
            new StockFact(a, 0m),                                  // out
            new StockFact(b, 5m), new StockFact(b, -5m),           // nets to zero: out
            new StockFact(c, 4m), new StockFact(c, 0m)             // one warehouse empty but the product is in stock
        ]);

        Assert.Equal(2, f.OutOfStockCount);
        Assert.Equal(3, f.ProductCount);
    }

    [Fact]
    public void Stock_Empty_IsAllZeros()
    {
        var f = StockCalculator.Calculate([]);

        Assert.Equal(new StockFigures(0, 0, 0m, 0), f);
    }
}
