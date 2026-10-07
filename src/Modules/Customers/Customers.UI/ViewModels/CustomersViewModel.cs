using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Customers.Application.Commands;
using Customers.Application.DTOs;
using Customers.Application.Queries;
using Customers.Domain.Enums;
using Customers.UI.Resources;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Customers.UI.ViewModels;

/// <summary>One customer as the list shows it.</summary>
public sealed record CustomerRow(CustomerListItemDto Customer, string StatusText)
{
    public bool IsActive => Customer.Status == CustomerStatus.Active;
}

/// <summary>
/// Customers (FIX-01d): search, list, create, edit, deactivate and reactivate. Reading needs customers.customer.view (customer data is
/// personal data), changing needs customers.customer.manage; both are enforced by the handlers. Through the per-action runner.
/// </summary>
public sealed class CustomersViewModel : ViewModelBase, INavigationAware
{
    /// <summary>The most rows the list shows at once; more are reached by searching.</summary>
    public const int PageSize = 200;

    private readonly IUiActionRunner _runner;
    private string _searchText = string.Empty;
    private bool _showInactive;
    private CustomerRow? _selected;
    private string? _resultInfo;
    private bool _isEditorOpen;
    private Guid? _editingId;
    private string _editCode = string.Empty;
    private string _editName = string.Empty;
    private string _editEmail = string.Empty;
    private string _editPhone = string.Empty;
    private string _editNotes = string.Empty;

    public CustomersViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        SearchCommand = Command(() => LoadAsync(CancellationToken.None));
        NewCommand = Command(() => { OpenEditor(null); return Task.CompletedTask; }, () => !IsEditorOpen);
        EditCommand = Command(EditSelectedAsync, () => !IsEditorOpen && Selected is not null);
        SaveCommand = Command(SaveAsync, () => IsEditorOpen && !string.IsNullOrWhiteSpace(EditName) && (!IsNew || !string.IsNullOrWhiteSpace(EditCode)));
        CancelCommand = Command(() => { IsEditorOpen = false; return Task.CompletedTask; }, () => IsEditorOpen);
        DeactivateCommand = Command(() => SetActiveAsync(false), () => !IsEditorOpen && Selected is { IsActive: true });
        ReactivateCommand = Command(() => SetActiveAsync(true), () => !IsEditorOpen && Selected is { IsActive: false });
    }

    public ObservableCollection<CustomerRow> Customers { get; } = [];

    public ICommand SearchCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand ReactivateCommand { get; }

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public bool ShowInactive { get => _showInactive; set => Set(ref _showInactive, value); }
    public CustomerRow? Selected { get => _selected; set => Set(ref _selected, value); }
    public string? ResultInfo { get => _resultInfo; private set => Set(ref _resultInfo, value); }

    public bool IsEditorOpen { get => _isEditorOpen; private set { if (Set(ref _isEditorOpen, value)) Raise(nameof(IsListMode)); } }
    public bool IsListMode => !IsEditorOpen;
    public bool IsNew => _editingId is null;

    public string EditCode { get => _editCode; set => Set(ref _editCode, value); }
    public string EditName { get => _editName; set => Set(ref _editName, value); }
    public string EditEmail { get => _editEmail; set => Set(ref _editEmail, value); }
    public string EditPhone { get => _editPhone; set => Set(ref _editPhone, value); }
    public string EditNotes { get => _editNotes; set => Set(ref _editNotes, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => LoadAsync(cancellationToken));

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var (text, showInactive) = (SearchText.Trim(), ShowInactive);
        var loaded = await _runner.RunAsync<(IReadOnlyList<CustomerListItemDto> Items, int Total)>(async (scope, ct) =>
        {
            if (text.Length > 0)
            {
                var found = await scope.Get<SearchCustomersQueryHandler>().HandleAsync(new SearchCustomersQuery(text, PageSize), ct);
                return found.IsFailure
                    ? Result.Failure<(IReadOnlyList<CustomerListItemDto>, int)>(found.Error)
                    : Result.Success<(IReadOnlyList<CustomerListItemDto>, int)>((found.Value, found.Value.Count));
            }

            var page = await scope.Get<ListCustomersQueryHandler>().HandleAsync(new ListCustomersQuery(0, PageSize, showInactive ? null : CustomerStatus.Active), ct);
            return page.IsFailure
                ? Result.Failure<(IReadOnlyList<CustomerListItemDto>, int)>(page.Error)
                : Result.Success<(IReadOnlyList<CustomerListItemDto>, int)>((page.Value.Items, page.Value.Total));
        }, cancellationToken);
        if (!Accept(loaded)) return;

        var selectedId = Selected?.Customer.CustomerId;
        Customers.Clear();
        foreach (var customer in loaded.Value.Items.Where(c => showInactive || c.Status == CustomerStatus.Active))
            Customers.Add(new CustomerRow(customer, customer.Status == CustomerStatus.Active ? CustomersText.Active : CustomersText.Inactive));
        Selected = Customers.FirstOrDefault(c => c.Customer.CustomerId == selectedId);

        ResultInfo = Customers.Count == 0 ? CustomersText.NoResults
            : loaded.Value.Total > Customers.Count && text.Length == 0 ? string.Format(CultureInfo.CurrentCulture, CustomersText.Count, Customers.Count, loaded.Value.Total)
            : null;
    }

    private void OpenEditor(CustomerDto? customer)
    {
        _editingId = customer?.CustomerId;
        EditCode = customer?.Code ?? string.Empty;
        EditName = customer?.Name ?? string.Empty;
        EditEmail = customer?.Email ?? string.Empty;
        EditPhone = customer?.Phone ?? string.Empty;
        EditNotes = customer?.Notes ?? string.Empty;
        ErrorMessage = null;
        StatusMessage = null;
        Raise(nameof(IsNew));
        IsEditorOpen = true;
    }

    private async Task EditSelectedAsync()
    {
        if (Selected is not { } row) return;
        var found = await _runner.RunAsync((scope, ct) => scope.Get<GetCustomerByIdQueryHandler>().HandleAsync(new GetCustomerByIdQuery(row.Customer.CustomerId), ct));
        if (Accept(found) && found.Value is { } customer) OpenEditor(customer);
    }

    private async Task SaveAsync()
    {
        var editingId = _editingId;
        var (code, name, email, phone, notes) = (EditCode.Trim(), EditName.Trim(), Blank(EditEmail), Blank(EditPhone), Blank(EditNotes));
        var saved = await _runner.RunAsync(async (scope, ct) => editingId is { } id
            ? await scope.Get<UpdateCustomerCommandHandler>().HandleAsync(new UpdateCustomerCommand(id, name, email, phone, notes), ct)
            : Drop(await scope.Get<CreateCustomerCommandHandler>().HandleAsync(new CreateCustomerCommand(code, name, email, phone, notes), ct)));
        if (!Accept(saved)) return;

        IsEditorOpen = false;
        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, editingId is null ? CustomersText.Created : CustomersText.Saved, name);
    }

    private async Task SetActiveAsync(bool active)
    {
        if (Selected is not { } row) return;
        var id = row.Customer.CustomerId;
        var done = await _runner.RunAsync((scope, ct) => active
            ? scope.Get<ReactivateCustomerCommandHandler>().HandleAsync(new ReactivateCustomerCommand(id), ct)
            : scope.Get<DeactivateCustomerCommandHandler>().HandleAsync(new DeactivateCustomerCommand(id), ct));
        if (!Accept(done)) return;

        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, active ? CustomersText.Reactivated : CustomersText.Deactivated, row.Customer.Name);
    }

    private static Result Drop<T>(Result<T> result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error);

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
