using System.Collections.ObjectModel;
using Customers.Application.Commands;
using Customers.Application.DTOs;
using Customers.Application.Queries;

namespace Customers.UI.ViewModels;

/// <summary>Minimal customer screen: paged list, search and create. Talks to Application handlers only (never a DbContext).</summary>
public sealed class CustomerListViewModel(
    ListCustomersQueryHandler list,
    SearchCustomersQueryHandler search,
    CreateCustomerCommandHandler create) : ViewModelBase
{
    private string _searchText = string.Empty;
    private string? _error;
    private bool _isBusy;

    public ObservableCollection<CustomerListItemDto> Customers { get; } = [];

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () => Replace((await list.HandleAsync(new ListCustomersQuery(), cancellationToken)).Items));
    }

    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
            Replace(string.IsNullOrWhiteSpace(SearchText)
                ? (await list.HandleAsync(new ListCustomersQuery(), cancellationToken)).Items
                : await search.HandleAsync(new SearchCustomersQuery(SearchText), cancellationToken)));
    }

    public async Task CreateAsync(string code, string name, string? email, string? phone, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var result = await create.HandleAsync(new CreateCustomerCommand(code, name, email, phone), cancellationToken);
            if (result.IsFailure) { ErrorMessage = result.Error.Description; return; }

            Replace((await list.HandleAsync(new ListCustomersQuery(), cancellationToken)).Items);
        });
    }

    private void Replace(IEnumerable<CustomerListItemDto> items)
    {
        Customers.Clear();
        foreach (var item in items) Customers.Add(item);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        try { await action(); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsBusy = false; }
    }
}
