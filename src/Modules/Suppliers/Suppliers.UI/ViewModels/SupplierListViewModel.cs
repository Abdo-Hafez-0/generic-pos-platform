using System.Collections.ObjectModel;
using Suppliers.Application.Commands;
using Suppliers.Application.DTOs;
using Suppliers.Application.Queries;

namespace Suppliers.UI.ViewModels;

/// <summary>Minimal supplier screen: paged list, search and create. Talks to Application handlers only (never a DbContext).</summary>
public sealed class SupplierListViewModel(
    ListSuppliersQueryHandler list,
    SearchSuppliersQueryHandler search,
    CreateSupplierCommandHandler create) : ViewModelBase
{
    private string _searchText = string.Empty;
    private string? _error;
    private bool _isBusy;

    public ObservableCollection<SupplierListItemDto> Suppliers { get; } = [];

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () => Replace((await list.HandleAsync(new ListSuppliersQuery(), cancellationToken)).Items));
    }

    public async Task SearchAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
            Replace(string.IsNullOrWhiteSpace(SearchText)
                ? (await list.HandleAsync(new ListSuppliersQuery(), cancellationToken)).Items
                : await search.HandleAsync(new SearchSuppliersQuery(SearchText), cancellationToken)));
    }

    public async Task CreateAsync(string code, string name, string? email, string? phone, CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var result = await create.HandleAsync(new CreateSupplierCommand(code, name, email, phone), cancellationToken);
            if (result.IsFailure) { ErrorMessage = result.Error.Description; return; }

            Replace((await list.HandleAsync(new ListSuppliersQuery(), cancellationToken)).Items);
        });
    }

    private void Replace(IEnumerable<SupplierListItemDto> items)
    {
        Suppliers.Clear();
        foreach (var item in items) Suppliers.Add(item);
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
