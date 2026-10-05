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

    public sealed record DailySales(DateOnly Date, int SaleCount, decimal Total);

    public sealed record SalesFigures(int SaleCount, decimal GrandTotal, decimal AverageSale, IReadOnlyList<DailySales> Days);

    /// <summary>Pure sales arithmetic: no I/O, no other module. Sales outside the range are ignored; every day of the range is listed.</summary>
    public static class SalesCalculator
    {
        public static SalesFigures Calculate(IEnumerable<SaleFact> facts, DateRange range)
        {
            var inRange = facts.Where(f => range.Contains(f.CompletedAt)).ToList();

            var byDay = inRange
                .GroupBy(f => DateOnly.FromDateTime(ToUtc(f.CompletedAt)))
                .ToDictionary(g => g.Key, g => (Count: g.Count(), Total: g.Sum(f => f.GrandTotal)));

            var days = new List<DailySales>();
            for (var day = DateOnly.FromDateTime(range.From); day <= DateOnly.FromDateTime(range.To); day = day.AddDays(1))
            {
                byDay.TryGetValue(day, out var entry);
                days.Add(new DailySales(day, entry.Count, entry.Total));
            }

            var total = inRange.Sum(f => f.GrandTotal);
            var average = inRange.Count == 0 ? 0m : decimal.Round(total / inRange.Count, 4, MidpointRounding.AwayFromZero);
            return new SalesFigures(inRange.Count, total, average, days);
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
