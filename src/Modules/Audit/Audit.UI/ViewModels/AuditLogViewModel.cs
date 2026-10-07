using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Audit.Application.DTOs;
using Audit.Application.Queries;
using Audit.UI.Resources;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Audit.UI.ViewModels;

/// <summary>One audit entry as the list shows it.</summary>
public sealed record AuditRow(AuditEntryDto Entry, string TimeText, string SubjectText);

/// <summary>
/// The audit log (FIX-01d, read-only; the log is append-only at every layer): entries newest first, filtered by module, action and a period
/// of local days (the last 7 by default), 100 per page, and the details of the selected entry. Needs audit.view (available in every license
/// state). Nothing written to the log ever carries a secret (Stage 11).
/// </summary>
public sealed class AuditLogViewModel : ViewModelBase, INavigationAware
{
    public const int PageSize = 100;

    private readonly IUiActionRunner _runner;
    private string _module = string.Empty;
    private string _action = string.Empty;
    private DateTime _fromDate = DateTime.Today.AddDays(-6);
    private DateTime _toDate = DateTime.Today;
    private int _page = 1;
    private int _total;
    private AuditRow? _selected;
    private string? _pageInfo;

    public AuditLogViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        ShowCommand = Command(() => LoadAsync(1, CancellationToken.None));
        NewerCommand = Command(() => LoadAsync(_page - 1, CancellationToken.None), () => _page > 1);
        OlderCommand = Command(() => LoadAsync(_page + 1, CancellationToken.None), () => _page * PageSize < _total);
    }

    public ObservableCollection<AuditRow> Entries { get; } = [];

    public ICommand ShowCommand { get; }
    public ICommand NewerCommand { get; }
    public ICommand OlderCommand { get; }

    public string Module { get => _module; set => Set(ref _module, value); }
    public string Action { get => _action; set => Set(ref _action, value); }
    public DateTime FromDate { get => _fromDate; set => Set(ref _fromDate, value.Date); }
    public DateTime ToDate { get => _toDate; set => Set(ref _toDate, value.Date); }

    public AuditRow? Selected { get => _selected; set { if (Set(ref _selected, value)) Raise(nameof(DetailsText)); } }

    /// <summary>The summary and details of the selected entry.</summary>
    public string DetailsText => Selected is null ? string.Empty
        : string.Join(Environment.NewLine, new[] { Selected.Entry.Summary, Selected.Entry.Details }.Where(t => !string.IsNullOrWhiteSpace(t)));

    public string? PageInfo { get => _pageInfo; private set => Set(ref _pageInfo, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(1, cancellationToken));

    private async Task LoadAsync(int page, CancellationToken cancellationToken)
    {
        if (ToDate < FromDate) { ErrorMessage = AuditText.RangeInvalid; return; }

        var query = new QueryAuditEntriesQuery(
            Module: Blank(Module), Action: Blank(Action),
            From: DateTime.SpecifyKind(FromDate, DateTimeKind.Local).ToUniversalTime(),
            To: DateTime.SpecifyKind(ToDate.AddDays(1), DateTimeKind.Local).ToUniversalTime().AddTicks(-1),
            Page: Math.Max(1, page), PageSize: PageSize);
        var found = await _runner.RunAsync((scope, ct) => scope.Get<QueryAuditEntriesQueryHandler>().HandleAsync(query, ct), cancellationToken);
        if (!Accept(found)) return;

        _page = found.Value.Page;
        _total = found.Value.TotalCount;
        Entries.Clear();
        foreach (var entry in found.Value.Items)
            Entries.Add(new AuditRow(entry, LocalTime(entry.OccurredAt), string.Join(" ", new[] { entry.EntityType, entry.EntityId }.Where(t => !string.IsNullOrWhiteSpace(t)))));
        Selected = null;

        PageInfo = _total == 0 ? AuditText.NoEntries
            : string.Format(CultureInfo.CurrentCulture, AuditText.PageInfo, (_page - 1) * PageSize + 1, (_page - 1) * PageSize + Entries.Count, _total);
    }

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string LocalTime(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
}
