using Platform.Core.Results;

namespace Reporting.Domain.ValueObjects
{
    /// <summary>An inclusive UTC time range for a report, at most <see cref="MaxDays"/> days long.</summary>
    public readonly record struct DateRange
    {
        public const int MaxDays = 366;

        private DateRange(DateTime from, DateTime to)
        {
            From = from;
            To = to;
        }

        public DateTime From { get; }
        public DateTime To { get; }

        public static Result<DateRange> Create(DateTime from, DateTime to)
        {
            var start = ToUtc(from);
            var end = ToUtc(to);

            if (end < start)
                return Result.Failure<DateRange>(Error.Validation("Reporting.Range.Invalid", "The end of the range cannot be before its start."));
            if ((end - start).TotalDays > MaxDays)
                return Result.Failure<DateRange>(Error.Validation("Reporting.Range.TooLong", $"A report range cannot exceed {MaxDays} days."));

            return Result.Success(new DateRange(start, end));
        }

        public bool Contains(DateTime value) => ToUtc(value) is var t && t >= From && t <= To;

        private static DateTime ToUtc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}

namespace Reporting.Domain.Calculations
{
    using Reporting.Domain.ValueObjects;

    /// <summary>One completed sale, reduced to what the sales report needs.</summary>
    public readonly record struct SaleFact(DateTime CompletedAt, decimal GrandTotal);

    /// <summary>MISS-10c: one refund given, reduced to what the sales report needs (it counts on the day it was given).</summary>
    public readonly record struct RefundFact(DateTime GivenAt, decimal Amount);

    /// <summary>One day: its completed sales and their total; MISS-10c: the refunds given that day and what was kept (Net).</summary>
    public sealed record DailySales(DateOnly Date, int SaleCount, decimal Total, decimal Refunds = 0m)
    {
        public decimal Net => Total - Refunds;
    }

    /// <summary>The period's figures; MISS-10c: <see cref="RefundTotal"/> the refunds given in the period, <see cref="NetTotal"/> the takings kept.</summary>
    public sealed record SalesFigures(int SaleCount, decimal GrandTotal, decimal AverageSale, IReadOnlyList<DailySales> Days, decimal RefundTotal = 0m)
    {
        public decimal NetTotal => GrandTotal - RefundTotal;
    }

    /// <summary>
    /// Pure sales arithmetic: no I/O, no other module. Sales outside the range are ignored; every day of the range is listed.
    /// FIX-12: days are the SHOP's calendar days in <paramref name="zone"/> (daylight saving included), not UTC dates: a sale at 00:30 local
    /// time belongs to that local day even when it is still the previous day in UTC. Without a zone, UTC days (the earlier behaviour).
    /// </summary>
    public static class SalesCalculator
    {
        public static SalesFigures Calculate(IEnumerable<SaleFact> facts, DateRange range, TimeZoneInfo? zone = null, IEnumerable<RefundFact>? refunds = null)
        {
            zone ??= TimeZoneInfo.Utc;
            var inRange = facts.Where(f => range.Contains(f.CompletedAt)).ToList();
            var refundsInRange = (refunds ?? []).Where(r => range.Contains(r.GivenAt)).ToList();

            DateOnly LocalDay(DateTime instant) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(ToUtc(instant), zone));

            var byDay = inRange
                .GroupBy(f => LocalDay(f.CompletedAt))
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Total: g.Sum(f => f.GrandTotal)));

            var refundedByDay = refundsInRange.GroupBy(r => LocalDay(r.GivenAt)).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));

            var days = new List<DailySales>();
            for (var day = LocalDay(range.From); day <= LocalDay(range.To); day = day.AddDays(1))
            {
                byDay.TryGetValue(day, out var entry);
                days.Add(new DailySales(day, entry.Count, entry.Total, refundedByDay.GetValueOrDefault(day)));
            }

            var total = inRange.Sum(f => f.GrandTotal);
            var average = inRange.Count == 0 ? 0m : decimal.Round(total / inRange.Count, 4, MidpointRounding.AwayFromZero);
            return new SalesFigures(inRange.Count, total, average, days, refundsInRange.Sum(r => r.Amount));
        }

        private static DateTime ToUtc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    /// <summary>One stock level row (a product in a warehouse/location).</summary>
    public readonly record struct StockFact(Guid ProductId, decimal OnHand);

    public sealed record StockFigures(int StockItemCount, int ProductCount, decimal TotalOnHand, int OutOfStockCount);

    /// <summary>Pure stock arithmetic. A product is out of stock when its on-hand quantity summed over all warehouses is zero or less.</summary>
    public static class StockCalculator
    {
        public static StockFigures Calculate(IEnumerable<StockFact> facts)
        {
            var list = facts.ToList();
            var perProduct = list.GroupBy(f => f.ProductId).Select(g => g.Sum(f => f.OnHand)).ToList();

            return new StockFigures(list.Count, perProduct.Count, list.Sum(f => f.OnHand), perProduct.Count(q => q <= 0m));
        }
    }
}
