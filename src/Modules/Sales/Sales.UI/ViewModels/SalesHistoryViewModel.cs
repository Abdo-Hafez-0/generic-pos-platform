using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Sales.Application.DTOs;
using Sales.Application.Queries;
using Sales.Domain.Enums;
using Sales.UI.Resources;

namespace Sales.UI.ViewModels;

/// <summary>One sale as the list shows it (times in the computer's local time).</summary>
public sealed record SaleRow(SaleDto Sale, string TimeText, string StatusText, int ItemCount)
{
    public bool IsCompleted => Sale.Status == SaleStatus.Completed;

    /// <summary>The customer snapshot taken at the sale (FIX-11): "C-001 Jane Doe", or empty.</summary>
    public string CustomerText => Sale.CustomerId is null ? string.Empty : $"{Sale.CustomerCode} {Sale.CustomerName}";
}

/// <summary>
/// Sales history (FIX-01c, read-only): the sales of a period (local dates, today by default), newest first, the takings of the
/// completed ones, and the lines of the selected sale. Reading your own sales needs no license and no capability.
/// </summary>
public sealed class SalesHistoryViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private DateTime _fromDate = DateTime.Today;
    private DateTime _toDate = DateTime.Today;
    private SaleRow? _selected;
    private string _summaryText = string.Empty;
    private string? _resultInfo;

    public SalesHistoryViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        ShowCommand = Command(() => LoadAsync(CancellationToken.None));
    }

    public ObservableCollection<SaleRow> Sales { get; } = [];
    public ObservableCollection<SaleItemDto> Lines { get; } = [];

    public ICommand ShowCommand { get; }

    /// <summary>First day of the period (local date).</summary>
    public DateTime FromDate { get => _fromDate; set => Set(ref _fromDate, value.Date); }

    /// <summary>Last day of the period, included (local date).</summary>
    public DateTime ToDate { get => _toDate; set => Set(ref _toDate, value.Date); }

    public SaleRow? Selected
    {
        get => _selected;
        set
        {
            if (!Set(ref _selected, value)) return;
            Lines.Clear();
            if (value is not null)
                foreach (var line in value.Sale.Items) Lines.Add(line);
            Raise(nameof(HasSelection));
            Raise(nameof(SelectedCustomerText));
        }
    }

    /// <summary>The selected sale's customer in words (FIX-11), or null when it has none.</summary>
    public string? SelectedCustomerText => Selected is { Sale.CustomerId: not null } row
        ? string.Format(CultureInfo.CurrentCulture, SalesText.SaleCustomer, row.CustomerText)
        : null;

    public bool HasSelection => Selected is not null;
    public string SummaryText { get => _summaryText; private set => Set(ref _summaryText, value); }
    public string? ResultInfo { get => _resultInfo; private set => Set(ref _resultInfo, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (ToDate < FromDate)
        {
            ErrorMessage = SalesText.RangeInvalid;
            return;
        }

        // the user thinks in local days; the database stores UTC
        var fromUtc = DateTime.SpecifyKind(FromDate, DateTimeKind.Local).ToUniversalTime();
        var toUtc = DateTime.SpecifyKind(ToDate.AddDays(1), DateTimeKind.Local).ToUniversalTime();
        var history = await _runner.QueryAsync((scope, ct) => scope.Get<GetSalesHistoryQueryHandler>().HandleAsync(new GetSalesHistoryQuery(fromUtc, toUtc), ct), cancellationToken);
        if (!Accept(history)) return;

        var selectedId = Selected?.Sale.SaleId;
        Sales.Clear();
        foreach (var sale in history.Value.Sales)
            Sales.Add(new SaleRow(sale, LocalTime(sale.CompletedAt ?? sale.CreatedAt), Describe(sale.Status), sale.Items.Count));
        Selected = Sales.FirstOrDefault(s => s.Sale.SaleId == selectedId);

        SummaryText = string.Format(CultureInfo.CurrentCulture, SalesText.Summary, history.Value.CompletedCount, history.Value.CompletedTotal);
        ResultInfo = history.Value.IsTruncated
            ? string.Format(CultureInfo.CurrentCulture, SalesText.Truncated, GetSalesHistoryQuery.MaxResults)
            : Sales.Count == 0 ? SalesText.NoSales : null;
    }

    private static string LocalTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    private static string Describe(SaleStatus status) => status switch
    {
        SaleStatus.Draft => SalesText.StatusDraft,
        SaleStatus.Confirmed => SalesText.StatusConfirmed,
        SaleStatus.Completed => SalesText.StatusCompleted,
        _ => SalesText.StatusCancelled,
    };
}
