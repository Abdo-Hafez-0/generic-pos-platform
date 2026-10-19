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

/// <summary>One sale as the list shows it (times in the computer's local time). <see cref="CanReturn"/>: a completed sale and a screen to return it.</summary>
public sealed record SaleRow(SaleDto Sale, string TimeText, string StatusText, int ItemCount, bool CanReturn = false, decimal Refunded = 0m)
{
    /// <summary>MISS-10c: what was refunded of the sale, or "–".</summary>
    public string RefundedText => Refunded > 0m ? Refunded.ToString("N2", CultureInfo.CurrentCulture) : "–";

    public bool IsCompleted => Sale.Status == SaleStatus.Completed;

    /// <summary>The customer snapshot taken at the sale (FIX-11): "C-001 Jane Doe", or empty.</summary>
    public string CustomerText => Sale.CustomerId is null ? string.Empty : $"{Sale.CustomerCode} {Sale.CustomerName}";
}

/// <summary>MISS-10c: a line of the chosen sale with how many of its units came back.</summary>
public sealed record SaleLineRow(SaleItemDto Item, decimal Returned)
{
    public string ReturnedText => Returned > 0m ? Returned.ToString("0.###", CultureInfo.CurrentCulture) : "–";
}

/// <summary>
/// Sales history (FIX-01c, read-only): the sales of a period (local dates, today by default), newest first, the takings of the
/// completed ones, and the lines of the selected sale. Reading your own sales needs no license and no capability.
/// MISS-10b: a completed sale's "⋯" offers "Return…", which asks the shell for the screen that handles returns (the till) - Sales does not
/// know POS; without such a screen for the signed-in user the menu is not shown.
/// </summary>
public sealed class SalesHistoryViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private readonly IShellNavigation? _shell;
    private DateTime _fromDate = DateTime.Today;
    private DateTime _toDate = DateTime.Today;
    private SaleRow? _selected;
    private string _summaryText = string.Empty;
    private string? _resultInfo;

    public SalesHistoryViewModel(IUiActionRunner runner, IShellNavigation? shell = null)
    {
        _runner = runner;
        _shell = shell;
        ShowCommand = Command(() => LoadAsync(CancellationToken.None));
        ReturnRowCommand = Command<SaleRow>(row => row is null ? Task.CompletedTask : _shell!.OpenForAsync(new ScreenRequest(ScreenRequests.ReturnSale, row.Sale.SaleId)),
            row => row is { CanReturn: true } && _shell is not null);
    }

    /// <summary>MISS-10b: opens the return of the sale given as the parameter (in the till).</summary>
    public ICommand ReturnRowCommand { get; }

    public ObservableCollection<SaleRow> Sales { get; } = [];
    public ObservableCollection<SaleLineRow> Lines { get; } = [];
    private IReadOnlyDictionary<Guid, decimal> _returnedByItem = new Dictionary<Guid, decimal>();

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
                foreach (var line in value.Sale.Items) Lines.Add(new SaleLineRow(line, _returnedByItem.GetValueOrDefault(line.SaleItemId)));
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
        var mayReturn = _shell?.CanOpenFor(ScreenRequests.ReturnSale) == true;
        _returnedByItem = history.Value.ReturnedByItem;
        Selected = null;   // MISS-10c: the lines are rebuilt below with the new returned quantities
        Sales.Clear();
        foreach (var sale in history.Value.Sales)
            Sales.Add(new SaleRow(sale, LocalTime(sale.CompletedAt ?? sale.CreatedAt), Describe(sale.Status), sale.Items.Count,
                CanReturn: mayReturn && sale.Status == SaleStatus.Completed, Refunded: history.Value.RefundedBySale.GetValueOrDefault(sale.SaleId)));
        Selected = Sales.FirstOrDefault(s => s.Sale.SaleId == selectedId);

        SummaryText = history.Value.RefundedTotal > 0m
            ? string.Format(CultureInfo.CurrentCulture, SalesText.SummaryWithRefunds, history.Value.CompletedCount, history.Value.CompletedTotal,
                history.Value.RefundedTotal, history.Value.NetTotal)
            : string.Format(CultureInfo.CurrentCulture, SalesText.Summary, history.Value.CompletedCount, history.Value.CompletedTotal);
        ResultInfo = history.Value.IsTruncated
            ? string.Format(CultureInfo.CurrentCulture, SalesText.Truncated, GetSalesHistoryQuery.MaxResults)
            : Sales.Count == 0 ? SalesText.NoSales : null;
    }

    private static string LocalTime(DateTime utc)
        => "‎" + DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture) + "‎";   // keeps its order in Arabic (MISS-08c)

    private static string Describe(SaleStatus status) => status switch
    {
        SaleStatus.Draft => SalesText.StatusDraft,
        SaleStatus.Confirmed => SalesText.StatusConfirmed,
        SaleStatus.Completed => SalesText.StatusCompleted,
        _ => SalesText.StatusCancelled,
    };
}
