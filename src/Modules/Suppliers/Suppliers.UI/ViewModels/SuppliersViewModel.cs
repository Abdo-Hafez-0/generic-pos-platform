using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Suppliers.Application.Commands;
using Suppliers.Application.DTOs;
using Suppliers.Application.Queries;
using Suppliers.Domain.Enums;
using Suppliers.UI.Resources;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Suppliers.UI.ViewModels;

/// <summary>One supplier as the list shows it.</summary>
public sealed record SupplierRow(SupplierListItemDto Supplier, string StatusText)
{
    public bool IsActive => Supplier.Status == SupplierStatus.Active;
}

/// <summary>
/// Suppliers (FIX-01d): search, list, create, edit, deactivate and reactivate. Changing needs suppliers.supplier.manage (enforced by the
/// handlers); reading is open. Through the per-action runner.
/// </summary>
public sealed class SuppliersViewModel : ViewModelBase, INavigationAware
{
    /// <summary>The most rows the list shows at once; more are reached by searching.</summary>
    public const int PageSize = 200;

    private readonly IUiActionRunner _runner;
    private string _searchText = string.Empty;
    private bool _showInactive;
    private SupplierRow? _selected;
    private string? _resultInfo;
    private bool _isEditorOpen;
    private Guid? _editingId;
    private string _editCode = string.Empty;
    private string _editName = string.Empty;
    private string _editEmail = string.Empty;
    private string _editPhone = string.Empty;
    private string _editNotes = string.Empty;

    public SuppliersViewModel(IUiActionRunner runner)
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

    public ObservableCollection<SupplierRow> Suppliers { get; } = [];

    public ICommand SearchCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DeactivateCommand { get; }
    public ICommand ReactivateCommand { get; }

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public bool ShowInactive { get => _showInactive; set => Set(ref _showInactive, value); }
    public SupplierRow? Selected { get => _selected; set => Set(ref _selected, value); }
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
        // supplier reads are not restricted (supplier data is business data, not personal data)
        var loaded = await _runner.QueryAsync<(IReadOnlyList<SupplierListItemDto> Items, int Total)>(async (scope, ct) =>
        {
            if (text.Length > 0)
            {
                var found = await scope.Get<SearchSuppliersQueryHandler>().HandleAsync(new SearchSuppliersQuery(text, PageSize), ct);
                return (found, found.Count);
            }

            var page = await scope.Get<ListSuppliersQueryHandler>().HandleAsync(new ListSuppliersQuery(0, PageSize, showInactive ? null : SupplierStatus.Active), ct);
            return (page.Items, page.Total);
        }, cancellationToken);
        if (!Accept(loaded)) return;

        var selectedId = Selected?.Supplier.SupplierId;
        Suppliers.Clear();
        foreach (var supplier in loaded.Value.Items.Where(c => showInactive || c.Status == SupplierStatus.Active))
            Suppliers.Add(new SupplierRow(supplier, supplier.Status == SupplierStatus.Active ? SuppliersText.Active : SuppliersText.Inactive));
        Selected = Suppliers.FirstOrDefault(c => c.Supplier.SupplierId == selectedId);

        ResultInfo = Suppliers.Count == 0 ? SuppliersText.NoResults
            : loaded.Value.Total > Suppliers.Count && text.Length == 0 ? string.Format(CultureInfo.CurrentCulture, SuppliersText.Count, Suppliers.Count, loaded.Value.Total)
            : null;
    }

    private void OpenEditor(SupplierDto? supplier)
    {
        _editingId = supplier?.SupplierId;
        EditCode = supplier?.Code ?? string.Empty;
        EditName = supplier?.Name ?? string.Empty;
        EditEmail = supplier?.Email ?? string.Empty;
        EditPhone = supplier?.Phone ?? string.Empty;
        EditNotes = supplier?.Notes ?? string.Empty;
        ErrorMessage = null;
        StatusMessage = null;
        Raise(nameof(IsNew));
        IsEditorOpen = true;
    }

    private async Task EditSelectedAsync()
    {
        if (Selected is not { } row) return;
        var found = await _runner.QueryAsync((scope, ct) => scope.Get<GetSupplierByIdQueryHandler>().HandleAsync(new GetSupplierByIdQuery(row.Supplier.SupplierId), ct));
        if (Accept(found) && found.Value is { } supplier) OpenEditor(supplier);
    }

    private async Task SaveAsync()
    {
        var editingId = _editingId;
        var (code, name, email, phone, notes) = (EditCode.Trim(), EditName.Trim(), Blank(EditEmail), Blank(EditPhone), Blank(EditNotes));
        var saved = await _runner.RunAsync(async (scope, ct) => editingId is { } id
            ? await scope.Get<UpdateSupplierCommandHandler>().HandleAsync(new UpdateSupplierCommand(id, name, email, phone, notes), ct)
            : Drop(await scope.Get<CreateSupplierCommandHandler>().HandleAsync(new CreateSupplierCommand(code, name, email, phone, notes), ct)));
        if (!Accept(saved)) return;

        IsEditorOpen = false;
        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, editingId is null ? SuppliersText.Created : SuppliersText.Saved, name);
    }

    private async Task SetActiveAsync(bool active)
    {
        if (Selected is not { } row) return;
        var id = row.Supplier.SupplierId;
        var done = await _runner.RunAsync((scope, ct) => active
            ? scope.Get<ReactivateSupplierCommandHandler>().HandleAsync(new ReactivateSupplierCommand(id), ct)
            : scope.Get<DeactivateSupplierCommandHandler>().HandleAsync(new DeactivateSupplierCommand(id), ct));
        if (!Accept(done)) return;

        await LoadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, active ? SuppliersText.Reactivated : SuppliersText.Deactivated, row.Supplier.Name);
    }

    private static Result Drop<T>(Result<T> result) => result.IsSuccess ? Result.Success() : Result.Failure(result.Error);

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
