using Microsoft.Extensions.DependencyInjection;
using Sales.Application.Commands;
using Sales.Application.Queries;
using Sales.Domain.Enums;

namespace Sales.Tests.Application;

/// <summary>FIX-01c: the sales history behind the sales screen - a date range, newest first, takings counted from completed sales only.</summary>
public sealed class SalesHistoryTests
{
    private static readonly Guid Product = Guid.NewGuid();

    private static async Task<SalesTestDatabase> StartAsync()
    {
        var lookup = new StubProductLookup();
        lookup.Register(Product, sku: "SKU-A", name: "Product A", salePrice: 10m);
        return await SalesTestDatabase.CreateAsync(lookup);
    }

    private static async Task<Guid> SaleAsync(SalesTestDatabase db, string reference, decimal quantity, SaleStatus finalStatus)
    {
        using var scope = db.CreateScope();
        var sp = scope.ServiceProvider;
        var id = (await sp.GetRequiredService<CreateSaleCommandHandler>().HandleAsync(new CreateSaleCommand(reference, null))).Value;
        Assert.True((await sp.GetRequiredService<AddSaleItemCommandHandler>().HandleAsync(new AddSaleItemCommand(id, Product, quantity, 10m, 0m, 0m, null))).IsSuccess);
        if (finalStatus is SaleStatus.Draft) return id;

        Assert.True((await sp.GetRequiredService<ConfirmSaleCommandHandler>().HandleAsync(new ConfirmSaleCommand(id))).IsSuccess);
        if (finalStatus is SaleStatus.Completed)
            Assert.True((await sp.GetRequiredService<CompleteSaleCommandHandler>().HandleAsync(new CompleteSaleCommand(id))).IsSuccess);
        if (finalStatus is SaleStatus.Cancelled)
            Assert.True((await sp.GetRequiredService<CancelSaleCommandHandler>().HandleAsync(new CancelSaleCommand(id, "customer left"))).IsSuccess);
        return id;
    }

    private static async Task<SalesHistory> HistoryAsync(SalesTestDatabase db, DateTime from, DateTime to)
    {
        using var scope = db.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<GetSalesHistoryQueryHandler>().HandleAsync(new GetSalesHistoryQuery(from, to));
    }

    [Fact]
    public async Task The_range_lists_every_sale_newest_first_and_counts_only_completed_takings()
    {
        await using var db = await StartAsync();
        await SaleAsync(db, "S-1", 1m, SaleStatus.Completed);
        await SaleAsync(db, "S-2", 2m, SaleStatus.Cancelled);
        await SaleAsync(db, "S-3", 3m, SaleStatus.Draft);
        await SaleAsync(db, "S-4", 4m, SaleStatus.Completed);

        var history = await HistoryAsync(db, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

        Assert.Equal(["S-4", "S-3", "S-2", "S-1"], history.Sales.Select(s => s.Reference));
        Assert.Equal((2, 50m), (history.CompletedCount, history.CompletedTotal));
        Assert.Single(history.Sales[0].Items);
        Assert.False(history.IsTruncated);
    }

    [Fact]
    public async Task Sales_outside_the_range_are_left_out_and_an_empty_or_reversed_range_is_empty()
    {
        await using var db = await StartAsync();
        await SaleAsync(db, "S-1", 1m, SaleStatus.Completed);

        Assert.Empty((await HistoryAsync(db, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(-1))).Sales);
        Assert.Empty((await HistoryAsync(db, DateTime.UtcNow.AddDays(1), DateTime.UtcNow.AddDays(2))).Sales);
        Assert.Empty((await HistoryAsync(db, DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(-1))).Sales);
    }

    // ------------------------------------------------------------------ FIX-12: the ranged read for reports

    [Fact]
    public async Task Reports_read_every_completed_sale_of_a_range_even_beyond_2000()
    {
        await using var db = await StartAsync();
        var before = DateTime.UtcNow.AddSeconds(-1);
        for (var i = 0; i < 2050; i++) await SaleAsync(db, $"S-{i}", 1m, SaleStatus.Completed);
        await SaleAsync(db, "draft", 1m, SaleStatus.Draft);
        await SaleAsync(db, "cancelled", 1m, SaleStatus.Cancelled);
        var after = DateTime.UtcNow.AddSeconds(1);

        using var scope = db.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<Sales.Contracts.Interfaces.ISalesReader>();
        var all = await reader.GetCompletedBetweenAsync(before, after);
        var none = await reader.GetCompletedBetweenAsync(after, after.AddDays(1));

        Assert.Equal(2050, all.Count);                                  // no 2000 cap; drafts and cancelled sales are not included
        Assert.All(all, s => Assert.Equal((10m, DateTimeKind.Utc), (s.GrandTotal, s.CompletedAt.Kind)));
        Assert.True(all.Zip(all.Skip(1)).All(p => p.First.CompletedAt <= p.Second.CompletedAt));   // oldest first
        Assert.Empty(none);
    }

    [Fact]
    public async Task The_range_is_inclusive_at_both_ends()
    {
        await using var db = await StartAsync();
        var id = await SaleAsync(db, "S-1", 1m, SaleStatus.Completed);
        using var scope = db.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<Sales.Contracts.Interfaces.ISalesReader>();
        var at = (await reader.GetCompletedBetweenAsync(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1))).Single().CompletedAt;

        Assert.Equal(id, Assert.Single(await reader.GetCompletedBetweenAsync(at, at)).SaleId);
        Assert.Empty(await reader.GetCompletedBetweenAsync(at.AddTicks(1), at.AddMinutes(1)));
    }
}
