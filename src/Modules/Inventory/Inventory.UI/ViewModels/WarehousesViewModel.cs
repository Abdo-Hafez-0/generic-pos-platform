using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Contracts.Models;
using Inventory.UI.Resources;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Inventory.UI.ViewModels;

/// <summary>One warehouse as the list shows it.</summary>
public sealed record WarehouseRow(WarehouseDto Warehouse, string StatusText);

/// <summary>
/// Warehouses (FIX-01c): where stock is kept and sold from. Lists every warehouse and adds new ones; the handler authorizes
/// (inventory.location.manage) and refuses a duplicate code in plain words.
/// </summary>
public sealed class WarehousesViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;
    private string _newName = string.Empty;
    private string _newCode = string.Empty;
    private string _newDescription = string.Empty;
    private bool _isEditorOpen;

    public WarehousesViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        AddCommand = Command(AddAsync, () => IsEditorOpen && !string.IsNullOrWhiteSpace(NewName) && !string.IsNullOrWhiteSpace(NewCode));
        // MISS-08c (owner: the editor in a panel beside the list): "New warehouse" opens it, Cancel closes it
        NewCommand = Command(() => { IsEditorOpen = true; return Task.CompletedTask; }, () => !IsEditorOpen);
        CancelCommand = Command(() => { CloseEditor(); return Task.CompletedTask; }, () => IsEditorOpen);
    }

    public ObservableCollection<WarehouseRow> Warehouses { get; } = [];

    public ICommand AddCommand { get; }
    public ICommand NewCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>The panel for a new warehouse is open.</summary>
    public bool IsEditorOpen { get => _isEditorOpen; private set => Set(ref _isEditorOpen, value); }

    public string NewName { get => _newName; set => Set(ref _newName, value); }
    public string NewCode { get => _newCode; set => Set(ref _newCode, value); }
    public string NewDescription { get => _newDescription; set => Set(ref _newDescription, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(() => ReloadAsync(cancellationToken));

    private async Task AddAsync()
    {
        var (name, code, description) = (NewName.Trim(), NewCode.Trim(), string.IsNullOrWhiteSpace(NewDescription) ? null : NewDescription.Trim());
        var added = await _runner.RunAsync((scope, ct) => Discard(scope.Get<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand(name, code, description), ct)));
        if (!Accept(added)) return;

        CloseEditor();
        await ReloadAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, InventoryText.WarehouseAdded, name);
    }

    private void CloseEditor()
    {
        NewName = NewCode = NewDescription = string.Empty;
        IsEditorOpen = false;
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var loaded = await _runner.RunAsync((scope, ct) => scope.Get<GetWarehousesQueryHandler>().HandleAsync(new GetWarehousesQuery(ActiveOnly: false), ct), cancellationToken);
        if (!Accept(loaded)) return;

        Warehouses.Clear();
        foreach (var warehouse in loaded.Value.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase))
            Warehouses.Add(new WarehouseRow(warehouse, warehouse.IsActive ? InventoryText.Active : InventoryText.Inactive));
    }

    private static async Task<Result> Discard<T>(Task<Result<T>> pending) => await pending;
}
