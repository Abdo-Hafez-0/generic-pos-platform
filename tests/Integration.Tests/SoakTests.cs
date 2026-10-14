using System.Diagnostics;
using CashManagement.Contracts.Interfaces;
using Client.Backup.Application;
using Inventory.Contracts.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;
using Reporting.Application.Queries;
using Sales.Contracts.Interfaces;
using Xunit.Abstractions;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>A test that runs only when GENERICPOS_SOAK=1 (it takes several minutes).</summary>
public sealed class SoakFactAttribute : FactAttribute
{
    public SoakFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GENERICPOS_SOAK") != "1")
            Skip = "Long soak test: set GENERICPOS_SOAK=1 to run it (MISS-06a).";
    }
}

/// <summary>
/// MISS-06a (user decisions 2026-10-14): load and soak on the desktop's real composition (OfflineDesktop: every module, licensing, local
/// backup, a real SQLite file, no network, fake peripherals), with no screen in between. A day of sales of 1-5 lines paid in cash, by card or
/// split, with a backup and a sales report in the middle of the day; in the full run also a burst at 120 sales per minute.
///
/// Hard checks (user decision 2026-10-14 - this PC has slow periods outside the code, so raw speed only REPORTS against the 250 ms goal):
/// every sale succeeds; after a 20-sale warm-up no sale takes 2 s or more, the very first one under 5 s; the checkout step is not slower
/// at the end of the day than at its start (the code-caused slowdown found here: a cash sale read the whole drawer shift); the burst never
/// falls behind; memory stays level; and everything adds up - sales and the report, stock, the drawer's cash, payments, the audit log, and
/// SQLite's integrity check. p50/p95/p99 go to the output and to soak-results.txt next to the test assembly.
/// The smoke run (200 sales) is part of the normal suite; the full run is opt-in (GENERICPOS_SOAK=1).
/// </summary>
[Collection(HostCollection.Name)]
public sealed class SoakTests(ITestOutputHelper output)
{
    [Fact]
    public Task Smoke_a_short_day_of_200_sales_stays_fast_and_adds_up() => new SalesDay(output).RunAsync(daySales: 200, burstSales: 0);

    [SoakFact]
    public Task Soak_a_full_day_of_1500_sales_and_a_5_minute_burst_at_120_per_minute() => new SalesDay(output).RunAsync(daySales: 1500, burstSales: 600);
}

internal sealed class SalesDay(ITestOutputHelper output)
{
    private const int ProductCount = 40;

    /// <summary>The summary goes to the test output and to soak-results.txt next to the test assembly (small; the host's own logs are not in it).</summary>
    private void Write(string line)
    {
        output.WriteLine(line);
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "soak-results.txt"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}"); }
        catch (IOException) { }
    }

    private const decimal StartingStock = 1_000_000m;
    private const decimal OpeningFloat = 50m;   // FailureTestKit.OpenCashDrawerAsync
    private const int Warmup = 20;
    private static readonly TimeSpan BurstInterval = TimeSpan.FromMilliseconds(500);   // 120 sales per minute

    private readonly Random _random = new(20261014);
    private readonly List<(string Sku, Guid ProductId, decimal Price)> _products = [];
    private readonly Dictionary<Guid, decimal> _sold = [];
    private readonly List<double> _latencies = [];
    private readonly List<double> _checkouts = [];   // the checkout step alone (the rest is the cart being filled)
    private decimal _expectedTotal, _cashKept;
    private Guid _warehouse;
    private int _cash, _card, _split;

    public async Task RunAsync(int daySales, int burstSales)
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        Write($"---- {daySales} sales in the day + {burstSales} in the burst");
        var warehouse = await SetUpShopAsync(services, Path.Combine(desktop.Host.Folder, "usb"));
        var sessionId = await InScopeAsync(services, async p =>
        {
            var session = await p.GetRequiredService<IPOSService>().OpenSessionAsync("cashier-1", warehouse);
            Assert.True(session.IsSuccess, session.ErrorMessage);
            return session.SessionId;
        });
        var dayStart = DateTimeOffset.UtcNow;   // sales are stamped with the real time (the test clock drives licensing only)

        long memoryAfterWarmup = 0;
        for (var i = 0; i < daySales; i++)
        {
            await SellAsync(services, sessionId);
            if (i + 1 == Warmup) memoryAfterWarmup = Memory();

            if (i + 1 == daySales / 2)
            {
                var backup = await TimedAsync(() => InScopeAsync(services, p => p.GetRequiredService<CreateBackupCommandHandler>().HandleAsync(new CreateBackupCommand())));
                Assert.True(backup.Result.IsSuccess, backup.Result.IsFailure ? backup.Result.Error.Description : null);
                var report = await TimedAsync(() => ReportAsync(services, dayStart.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)));
                Assert.Equal(i + 1, report.Result.SaleCount);
                Write($"Mid-day after {i + 1} sales: backup {backup.Elapsed.TotalMilliseconds:0} ms ({new FileInfo(backup.Result.Value.Location).Length / 1024} KB), sales report {report.Elapsed.TotalMilliseconds:0} ms.");
            }
        }

        var dayLatencies = _latencies.ToList();
        var behindSchedule = TimeSpan.Zero;
        if (burstSales > 0)
        {
            var started = Stopwatch.StartNew();
            for (var i = 0; i < burstSales; i++)
            {
                var due = BurstInterval * i;
                if (started.Elapsed < due) await Task.Delay(due - started.Elapsed);
                else if (started.Elapsed - due > behindSchedule) behindSchedule = started.Elapsed - due;
                await SellAsync(services, sessionId);
            }

            Write($"Burst: {burstSales} sales at 120 per minute in {started.Elapsed.TotalSeconds:0} s; at most {behindSchedule.TotalMilliseconds:0} ms behind schedule.");
        }

        var memoryAtEnd = Memory();
        Report(dayLatencies, memoryAfterWarmup, memoryAtEnd, desktop.Host.DatabasePath);

        // ---- never stuck, and the checkout not slower at the end of the day (speed itself is reported against the 250 ms goal)
        Assert.True(_latencies[0] < 5000, $"The first sale took {_latencies[0]:0} ms.");
        Assert.All(_latencies.Skip(Warmup), ms => Assert.True(ms < 2000, $"A sale took {ms:0} ms."));
        var checkouts = _checkouts.Take(daySales).Skip(Warmup).ToList();
        var tenth = Math.Max(10, checkouts.Count / 10);
        var first = Percentile(checkouts.Take(tenth).ToList(), 50);
        var last = Percentile(checkouts.TakeLast(tenth).ToList(), 50);
        Assert.True(last <= Math.Max(1.5 * first, first + 15), $"The checkout is slower at the end of the day: p50 {first:0.0} ms at the start, {last:0.0} ms at the end.");
        Assert.True(behindSchedule < TimeSpan.FromSeconds(1), $"The burst fell {behindSchedule.TotalMilliseconds:0} ms behind 120 sales per minute.");
        Assert.True(memoryAtEnd - memoryAfterWarmup < 64L * 1024 * 1024, $"Memory grew by {(memoryAtEnd - memoryAfterWarmup) / 1024 / 1024} MB.");

        // ---- everything adds up
        var sales = daySales + burstSales;
        await ReconcileAsync(desktop, dayStart, sales);
    }

    private async Task<Guid> SetUpShopAsync(IServiceProvider services, string backupFolder)
    {
        Shop? first = null;
        for (var i = 0; i < ProductCount; i++)
        {
            var sku = $"SOAK-{i:000}";
            var price = Math.Round(0.75m + i * 0.37m, 2);
            var shop = await CreateShopAsync(services, sku, $"Item {i}", price, StartingStock, first?.WarehouseId);
            first ??= shop;
            _products.Add((sku, shop.ProductId, price));
        }

        var settings = await InScopeAsync(services, p => p.GetRequiredService<UpdateBackupSettingsCommandHandler>().HandleAsync(new UpdateBackupSettingsCommand(backupFolder, 14, ScheduleEnabled: false)));
        Assert.True(settings.IsSuccess, settings.IsFailure ? settings.Error.Description : null);
        _warehouse = first!.WarehouseId;
        return _warehouse;
    }

    /// <summary>One sale as the till does it: its own scope, a cart, 1-5 different products of 1-3 pieces, then cash, card or a split.</summary>
    private async Task SellAsync(IServiceProvider services, Guid sessionId)
    {
        var lines = _products.OrderBy(_ => _random.Next()).Take(_random.Next(1, 6)).Select(p => (p.Sku, p.ProductId, p.Price, Quantity: (decimal)_random.Next(1, 4))).ToList();
        var total = lines.Sum(l => l.Price * l.Quantity);   // prices include tax; no discount
        var kind = _random.Next(10);

        var watch = Stopwatch.StartNew();
        using (var scope = services.CreateScope())
        {
            var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
            var cart = await pos.StartCartAsync(sessionId);
            Assert.True(cart.IsSuccess, cart.ErrorMessage);
            foreach (var line in lines)
            {
                var added = await pos.AddProductAsync(cart.CartId, line.Sku, line.Quantity);
                Assert.True(added.IsSuccess, added.ErrorMessage);
            }

            IReadOnlyList<POSPaymentRequest> payments = kind switch
            {
                < 6 => [new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: Math.Ceiling(total / 5m) * 5m, Amount: total)],
                < 9 => [new POSPaymentRequest(POSPaymentMethod.Card, Amount: total)],
                _ => Split(total),
            };
            var checkoutStarted = watch.Elapsed;
            var checkout = await pos.CheckoutWithPaymentsAsync(cart.CartId, payments);
            _checkouts.Add((watch.Elapsed - checkoutStarted).TotalMilliseconds);
            Assert.True(checkout.IsSuccess, $"{checkout.ErrorCode}: {checkout.ErrorMessage}");
        }

        _latencies.Add(watch.Elapsed.TotalMilliseconds);
        _expectedTotal += total;
        foreach (var line in lines) _sold[line.ProductId] = _sold.GetValueOrDefault(line.ProductId) + line.Quantity;
        switch (kind)
        {
            case < 6: _cash++; _cashKept += total; break;
            case < 9: _card++; break;
            default: _split++; _cashKept += Math.Floor(total * 50m) / 100m; break;
        }
    }

    private static IReadOnlyList<POSPaymentRequest> Split(decimal total)
    {
        var cash = Math.Floor(total * 50m) / 100m;   // half in cash (to the cent), the rest by card
        return cash <= 0m
            ? [new POSPaymentRequest(POSPaymentMethod.Card, Amount: total)]
            : [new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: cash, Amount: cash), new POSPaymentRequest(POSPaymentMethod.Card, Amount: total - cash)];
    }

    private async Task ReconcileAsync(OfflineDesktop desktop, DateTimeOffset dayStart, int sales)
    {
        var services = desktop.Services;
        var to = DateTimeOffset.UtcNow.AddMinutes(1);

        var completed = await InScopeAsync(services, p => p.GetRequiredService<ISalesReader>().GetCompletedBetweenAsync(dayStart.UtcDateTime.AddMinutes(-1), to.UtcDateTime));
        Assert.Equal(sales, completed.Count);
        Assert.Equal(_expectedTotal, completed.Sum(s => s.GrandTotal));
        Assert.Equal(sales, await CountAsync(desktop.Host, "sal_Sales"));

        var report = await ReportAsync(services, dayStart.AddMinutes(-1), to);
        Assert.Equal(sales, report.SaleCount);
        Assert.Equal(_expectedTotal, report.GrandTotal);

        foreach (var (_, productId, _) in _products)
        {
            var level = await InScopeAsync(services, p => p.GetRequiredService<IInventoryReader>().GetStockLevelByProductAsync(productId, _warehouse));
            Assert.Equal(StartingStock - _sold.GetValueOrDefault(productId), level!.OnHand);
        }

        var drawer = await InScopeAsync(services, p => p.GetRequiredService<ICashSessionReader>().GetOpenSessionAsync("MAIN"));
        Assert.Equal(OpeningFloat + _cashKept, drawer!.Balance);
        Assert.Equal(_cash + _card + 2L * _split, await CountAsync(desktop.Host, "pay_Payments"));

        // the business audit entries are written in the background after each commit (FIX-05): wait for all of them
        var deadline = DateTime.UtcNow.AddSeconds(60);
        long audited;
        while ((audited = await CountAsync(desktop.Host, "aud_AuditEntries", "Action = 'sale.completed'")) < sales && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.Equal(sales, audited);

        Assert.Equal("ok", await ScalarAsync(desktop.Host, "PRAGMA integrity_check"));
        Write($"Reconciled: {sales} sales ({_cash} cash, {_card} card, {_split} split), total {_expectedTotal:0.00}, drawer {drawer.Balance:0.00}, " +
                         $"{_sold.Values.Sum():0} pieces from {_products.Count} products, {audited} audit entries, integrity ok.");
    }

    private static async Task<Reporting.Application.DTOs.SalesReportDto> ReportAsync(IServiceProvider services, DateTimeOffset from, DateTimeOffset to)
    {
        var report = await InScopeAsync(services, p => p.GetRequiredService<GetSalesReportQueryHandler>().HandleAsync(new GetSalesReportQuery(from.UtcDateTime, to.UtcDateTime)));
        Assert.True(report.IsSuccess, report.IsFailure ? report.Error.Description : null);
        return report.Value;
    }

    private void Report(List<double> day, long memoryAfterWarmup, long memoryAtEnd, string databasePath)
    {
        var all = _latencies;
        Write($"Sales: {all.Count} ({day.Count} in the day, {all.Count - day.Count} in the burst).");
        Write($"Day throughput: {day.Count / (day.Sum() / 1000.0):0.0} sales per second back to back.");
        var afterWarmup = all.Skip(Warmup).ToList();
        Write($"Latency ms - first sale {all[0]:0}, then p50 {Percentile(afterWarmup, 50):0.0}, p95 {Percentile(afterWarmup, 95):0.0} (goal 250: {(Percentile(afterWarmup, 95) < 250 ? "met" : "NOT met on this run")}), " +
              $"p99 {Percentile(afterWarmup, 99):0.0}, max {afterWarmup.Max():0.0}.");
        var fill = all.Zip(_checkouts, (whole, checkout) => whole - checkout).ToList();
        var tenthAll = Math.Max(10, all.Count / 10);
        Write($"p50 by step, first 10% -> last 10%: filling the cart {Percentile(fill.Skip(Warmup).Take(tenthAll).ToList(), 50):0.0} -> {Percentile(fill.TakeLast(tenthAll).ToList(), 50):0.0} ms, " +
                         $"checkout {Percentile(_checkouts.Skip(Warmup).Take(tenthAll).ToList(), 50):0.0} -> {Percentile(_checkouts.TakeLast(tenthAll).ToList(), 50):0.0} ms.");
        var measured = day.Skip(Warmup).ToList();
        var tenth = Math.Max(10, measured.Count / 10);
        Write($"Day p95 first 10%: {Percentile(measured.Take(tenth).ToList(), 95):0.0} ms, last 10%: {Percentile(measured.TakeLast(tenth).ToList(), 95):0.0} ms.");
        Write($"Thread pool: {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} pending work items, {ThreadPool.CompletedWorkItemCount} completed.");
        Write($"Managed memory after warm-up {memoryAfterWarmup / 1024 / 1024} MB, at the end {memoryAtEnd / 1024 / 1024} MB; database {new FileInfo(databasePath).Length / 1024} KB.");
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }

    private static long Memory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    private static async Task<(T Result, TimeSpan Elapsed)> TimedAsync<T>(Func<Task<T>> work)
    {
        var watch = Stopwatch.StartNew();
        var result = await work();
        return (result, watch.Elapsed);
    }

    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }
}
