using System.Collections.ObjectModel;
using Users.Application.Commands;
using Users.Application.DTOs;
using Users.Application.Queries;

namespace Users.UI.ViewModels;

/// <summary>Minimal users screen: list users, create one, deactivate or reactivate. Application handlers only.</summary>
public sealed class UsersViewModel(
    ListUsersQueryHandler list,
    CreateUserCommandHandler create,
    DeactivateUserCommandHandler deactivate,
    ReactivateUserCommandHandler reactivate) : ViewModelBase
{
    private string? _error;
    private string? _search;
    private bool _includeInactive;

    public ObservableCollection<UserSummaryDto> Users { get; } = [];
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }
    public string? Search { get => _search; set => Set(ref _search, value); }
    public bool IncludeInactive { get => _includeInactive; set => Set(ref _includeInactive, value); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        Users.Clear();
        var page = await list.HandleAsync(new ListUsersQuery(Search, IncludeInactive), cancellationToken);
        if (page.IsFailure) { ErrorMessage = page.Error.Description; return; }
        foreach (var u in page.Value.Items) Users.Add(u);
    }

    public async Task CreateAsync(string username, string displayName, string? email, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var r = await create.HandleAsync(new CreateUserCommand(username, displayName, email), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
        await LoadAsync(cancellationToken);
    }

    public async Task SetActiveAsync(Guid userId, bool active, CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        var r = active
            ? await reactivate.HandleAsync(new ReactivateUserCommand(userId), cancellationToken)
            : await deactivate.HandleAsync(new DeactivateUserCommand(userId), cancellationToken);
        if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
        await LoadAsync(cancellationToken);
    }
}
