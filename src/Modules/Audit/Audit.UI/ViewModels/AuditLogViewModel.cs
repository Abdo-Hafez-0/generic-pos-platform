using System.Collections.ObjectModel;
using Audit.Application.DTOs;
using Audit.Application.Queries;

namespace Audit.UI.ViewModels;

/// <summary>Minimal read-only audit log screen: filter by module/action and page through entries, newest first. Application handlers only.</summary>
public sealed class AuditLogViewModel(QueryAuditEntriesQueryHandler query) : ViewModelBase
{
    private string? _module;
    private string? _action;
    private int _page = 1;
    private int _totalCount;
    private string? _errorMessage;

    public ObservableCollection<AuditEntryDto> Entries { get; } = [];
    public string? Module { get => _module; set => Set(ref _module, value); }
    public string? Action { get => _action; set => Set(ref _action, value); }
    public int Page { get => _page; private set => Set(ref _page, value); }
    public int TotalCount { get => _totalCount; private set => Set(ref _totalCount, value); }
    public string? ErrorMessage { get => _errorMessage; private set => Set(ref _errorMessage, value); }

    public async Task LoadAsync(int page = 1, CancellationToken cancellationToken = default)
    {
        Entries.Clear();
        ErrorMessage = null;
        var outcome = await query.HandleAsync(new QueryAuditEntriesQuery(Module, Action, Page: page), cancellationToken);
        if (outcome.IsFailure) { ErrorMessage = outcome.Error.Description; return; }

        var result = outcome.Value;
        foreach (var e in result.Items) Entries.Add(e);
        Page = result.Page;
        TotalCount = result.TotalCount;
    }
}
