using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Reporting.Application.DTOs;
using Reporting.Application.Queries;
using Reporting.UI.Resources;

namespace Reporting.UI.ViewModels;

/// <summary>One figure of a report card.</summary>
public sealed record ReportLine(string Label, string Value);

/// <summary>One section of the overview: its figures, or the plain reason it is not available (for example the module is not installed).</summary>
public sealed record ReportCard(string Title, IReadOnlyList<ReportLine> Lines, string? Message)
{
    public bool IsAvailable => Message is null;
}

/// <summary>
/// The business overview (FIX-01d, read-only): sales of a period (local days; this month by default) with a daily breakdown, and the
/// current stock, purchasing, customer and supplier figures. Each section stands alone: a module that is missing makes only its own card
/// unavailable. Needs reporting.view (available in every license state: reading your own figures).
/// </summary>
public sealed class BusinessOverviewViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private DateTime _fromDate = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _toDate = DateTime.Today;

    public BusinessOverviewViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        ShowCommand = Command(() => LoadAsync(CancellationToken.None));
    }

    public ObservableCollection<ReportCard> Cards { get; } = [];
    public ObservableCollection<DailySalesDto> Days { get; } = [];

    public ICommand ShowCommand { get; }

    public DateTime FromDate { get => _fromDate; set => Set(ref _fromDate, value.Date); }
    public DateTime ToDate { get => _toDate; set => Set(ref _toDate, value.Date); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (ToDate < FromDate) { ErrorMessage = ReportsText.RangeInvalid; return; }

        // local days, end of the last day included (the report range is inclusive)
        var fromUtc = DateTime.SpecifyKind(FromDate, DateTimeKind.Local).ToUniversalTime();
        var toUtc = DateTime.SpecifyKind(ToDate.AddDays(1), DateTimeKind.Local).ToUniversalTime().AddTicks(-1);
        var overview = await _runner.RunAsync((scope, ct) => scope.Get<GetBusinessOverviewQueryHandler>().HandleAsync(new GetBusinessOverviewQuery(fromUtc, toUtc), ct), cancellationToken);
        if (!Accept(overview)) return;

        var o = overview.Value;
        Cards.Clear();
        Cards.Add(Card(ReportsText.SalesCard, o.Sales, s =>
        [
            new(ReportsText.SaleCount, s.SaleCount.ToString(CultureInfo.CurrentCulture)),
            new(ReportsText.SalesTotal, s.GrandTotal.ToString("N2", CultureInfo.CurrentCulture)),
            new(ReportsText.AverageSale, s.AverageSale.ToString("N2", CultureInfo.CurrentCulture)),
        ], s => s.IsTruncated ? ReportsText.SalesTruncated : null));
        Cards.Add(Card(ReportsText.InventoryCard, o.Inventory, i =>
        [
            new(ReportsText.StockItems, i.StockItemCount.ToString(CultureInfo.CurrentCulture)),
            new(ReportsText.Products, i.ProductCount.ToString(CultureInfo.CurrentCulture)),
            new(ReportsText.TotalOnHand, i.TotalOnHand.ToString("0.###", CultureInfo.CurrentCulture)),
            new(ReportsText.OutOfStock, i.OutOfStockCount.ToString(CultureInfo.CurrentCulture)),
        ]));
        Cards.Add(Card(ReportsText.PurchasingCard, o.Purchasing, p =>
        [
            new(ReportsText.Orders, p.TotalOrders.ToString(CultureInfo.CurrentCulture)),
            new(ReportsText.OrdersByStatus, string.Join(" / ", new[] { p.Draft, p.Submitted, p.Received, p.Cancelled }.Select(n => n.ToString(CultureInfo.CurrentCulture)))),
            new(ReportsText.OpenValue, p.OpenValue.ToString("N2", CultureInfo.CurrentCulture)),
            new(ReportsText.ReceivedValue, p.ReceivedValue.ToString("N2", CultureInfo.CurrentCulture)),
        ]));
        Cards.Add(Card(ReportsText.CustomersCard, o.Customers, Party));
        Cards.Add(Card(ReportsText.SuppliersCard, o.Suppliers, Party));

        Days.Clear();
        if (o.Sales is { IsAvailable: true, Data: { } sales })
            // FIX-12: the report's days are the shop's local calendar days, so every day of the chosen period is listed (empty ones too)
            foreach (var day in sales.Days.OrderBy(d => d.Date)) Days.Add(day);
    }

    private static IReadOnlyList<ReportLine> Party(PartySummaryDto p) =>
    [
        new(ReportsText.Total, p.Total.ToString(CultureInfo.CurrentCulture)),
        new(ReportsText.PartyCounts, $"{p.Active.ToString(CultureInfo.CurrentCulture)} / {p.Inactive.ToString(CultureInfo.CurrentCulture)}"),
    ];

    private static ReportCard Card<T>(string title, OverviewSection<T> section, Func<T, IReadOnlyList<ReportLine>> lines, Func<T, string?>? note = null)
    {
        if (section is not { IsAvailable: true, Data: { } data })
            return new ReportCard(title, [], section.Message ?? string.Empty);

        var figures = lines(data);
        return note?.Invoke(data) is { } text
            ? new ReportCard(title, [.. figures, new ReportLine(string.Empty, text)], null)
            : new ReportCard(title, figures, null);
    }
}
