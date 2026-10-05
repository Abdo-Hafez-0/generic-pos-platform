using Reporting.Application.DTOs;
using Reporting.Application.Queries;

namespace Reporting.UI.ViewModels;

/// <summary>Minimal read-only overview screen: loads the business overview for a date range. Application handlers only.</summary>
public sealed class BusinessOverviewViewModel(GetBusinessOverviewQueryHandler overview) : ViewModelBase
{
    private BusinessOverviewDto? _current;
    private string? _error;

    public BusinessOverviewDto? Current { get => _current; private set => Set(ref _current, value); }
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }

    public async Task LoadAsync(DateTime from, DateTime to, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var result = await overview.HandleAsync(new GetBusinessOverviewQuery(from, to), cancellationToken);
        if (result.IsFailure) { ErrorMessage = result.Error.Description; return; }
        Current = result.Value;
    }
}
