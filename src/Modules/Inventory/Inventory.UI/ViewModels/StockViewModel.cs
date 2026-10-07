using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Inventory.Application.Commands;
using Inventory.Application.Queries;
using Inventory.Contracts.Models;
using Inventory.Domain.Enums;
using Inventory.UI.Resources;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;

namespace Inventory.UI.ViewModels;

/// <summary>A warehouse filter choice; <see cref="WarehouseId"/> null means every warehouse.</summary>
public sealed record WarehouseChoice(Guid? WarehouseId, string Name);

/// <summary>An adjustment reason with its plain-words name.</summary>
public sealed record ReasonChoice(AdjustmentReason Reason, string Name);

/// <summary>
/// The stock screen (FIX-01c): what is on hand per product and warehouse (filter by warehouse and SKU/name), receive stock by SKU or
/// barcode, and correct the selected line with a reason. Through the runner and the Inventory handlers, which authorize
/// (inventory.stock.receive, inventory.stock.adjust) and keep the movement history.
/// </summary>
public sealed class StockViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;

    private string _searchText = string.Empty;
    private WarehouseChoice? _filterWarehouse;
    private StockOverviewItem? _selected;
    private bool _hasNoWarehouses;
    private string? _resultInfo;

    private string _receiveCode = string.Empty;
    private WarehouseDto? _receiveWarehouse;
    private string _receiveQuantity = string.Empty;
    private string _receiveReference = string.Empty;

    private string _adjustBy = string.Empty;
    private ReasonChoice? _adjustReason;
    private string _adjustNotes = string.Empty;

    public StockViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        Reasons =
        [
            new(AdjustmentReason.CycleCount, InventoryText.ReasonCycleCount),
            new(AdjustmentReason.DamageWrite, InventoryText.ReasonDamageWrite),
            new(AdjustmentReason.Found, InventoryText.ReasonFound),
            new(AdjustmentReason.TransferIn, InventoryText.ReasonTransferIn),
            new(AdjustmentReason.TransferOut, InventoryText.ReasonTransferOut),
            new(AdjustmentReason.OpeningBalance, InventoryText.ReasonOpeningBalance),
            new(AdjustmentReason.Other, InventoryText.ReasonOther),
        ];
        _adjustReason = Reasons[0];

        SearchCommand = Command(() => RefreshAsync(CancellationToken.None));
        ReceiveCommand = Command(ReceiveAsync, () => !string.IsNullOrWhiteSpace(ReceiveCode) && !string.IsNullOrWhiteSpace(ReceiveQuantity));
        AdjustCommand = Command(AdjustAsync, () => Selected is not null && !string.IsNullOrWhiteSpace(AdjustBy) && AdjustReason is not null);
    }

    public ObservableCollection<StockOverviewItem> Stock { get; } = [];
    public ObservableCollection<WarehouseChoice> FilterWarehouses { get; } = [];
    public ObservableCollection<WarehouseDto> Warehouses { get; } = [];
    public IReadOnlyList<ReasonChoice> Reasons { get; }

    public ICommand SearchCommand { get; }
    public ICommand ReceiveCommand { get; }
    public ICommand AdjustCommand { get; }

    public string SearchText { get => _searchText; set => Set(ref _searchText, value); }
    public WarehouseChoice? FilterWarehouse { get => _filterWarehouse; set => Set(ref _filterWarehouse, value); }
    public StockOverviewItem? Selected { get => _selected; set => Set(ref _selected, value); }
    public bool HasNoWarehouses { get => _hasNoWarehouses; private set => Set(ref _hasNoWarehouses, value); }
    public string? ResultInfo { get => _resultInfo; private set => Set(ref _resultInfo, value); }

    public string ReceiveCode { get => _receiveCode; set => Set(ref _receiveCode, value); }
    public WarehouseDto? ReceiveWarehouse { get => _receiveWarehouse; set => Set(ref _receiveWarehouse, value); }
    public string ReceiveQuantity { get => _receiveQuantity; set => Set(ref _receiveQuantity, value); }
    public string ReceiveReference { get => _receiveReference; set => Set(ref _receiveReference, value); }

    public string AdjustBy { get => _adjustBy; set => Set(ref _adjustBy, value); }
    public ReasonChoice? AdjustReason { get => _adjustReason; set => Set(ref _adjustReason, value); }
    public string AdjustNotes { get => _adjustNotes; set => Set(ref _adjustNotes, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        var warehouses = await _runner.RunAsync((scope, ct) => scope.Get<GetWarehousesQueryHandler>().HandleAsync(new GetWarehousesQuery(ActiveOnly: true), ct), cancellationToken);
        if (!Accept(warehouses)) return;

        var filterId = FilterWarehouse?.WarehouseId;
        var receiveId = ReceiveWarehouse?.WarehouseId;
        Warehouses.Clear();
        FilterWarehouses.Clear();
        FilterWarehouses.Add(new WarehouseChoice(null, InventoryText.AllWarehouses));
        foreach (var warehouse in warehouses.Value.OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Warehouses.Add(warehouse);
            FilterWarehouses.Add(new WarehouseChoice(warehouse.WarehouseId, warehouse.Name));
        }

        HasNoWarehouses = Warehouses.Count == 0;
        FilterWarehouse = FilterWarehouses.FirstOrDefault(w => w.WarehouseId == filterId) ?? FilterWarehouses[0];
        ReceiveWarehouse = Warehouses.FirstOrDefault(w => w.WarehouseId == receiveId) ?? (Warehouses.Count == 1 ? Warehouses[0] : null);

        await RefreshAsync(cancellationToken);
    });

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var (search, warehouseId) = (SearchText, FilterWarehouse?.WarehouseId);
        var stock = await _runner.RunAsync((scope, ct) => scope.Get<GetStockOverviewQueryHandler>().HandleAsync(new GetStockOverviewQuery(search, warehouseId), ct), cancellationToken);
        if (Accept(stock)) Show(stock.Value);
    }

    private async Task ReceiveAsync()
    {
        if (ReceiveWarehouse is not { } warehouse) { Fail(HasNoWarehouses ? InventoryText.NoWarehouses : InventoryText.ChooseWarehouse); return; }
        if (!decimal.TryParse(ReceiveQuantity, NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity) || quantity <= 0) { Fail(InventoryText.QuantityInvalid); return; }

        var (code, reference) = (ReceiveCode, string.IsNullOrWhiteSpace(ReceiveReference) ? null : ReceiveReference.Trim());
        var (search, filterId) = (SearchText, FilterWarehouse?.WarehouseId);
        var received = await _runner.RunAsync<(StockProduct Product, IReadOnlyList<StockOverviewItem> Stock)>(async (scope, ct) =>
        {
            var product = await scope.Get<FindStockProductQueryHandler>().HandleAsync(new FindStockProductQuery(code), ct);
            if (product.IsFailure) return Result.Failure<(StockProduct, IReadOnlyList<StockOverviewItem>)>(product.Error);

            var added = await scope.Get<AddStockCommandHandler>().HandleAsync(new AddStockCommand(product.Value.ProductId, warehouse.WarehouseId, quantity, null, reference), ct);
            if (added.IsFailure) return Result.Failure<(StockProduct, IReadOnlyList<StockOverviewItem>)>(added.Error);

            var stock = await scope.Get<GetStockOverviewQueryHandler>().HandleAsync(new GetStockOverviewQuery(search, filterId), ct);
            return stock.IsFailure
                ? Result.Failure<(StockProduct, IReadOnlyList<StockOverviewItem>)>(stock.Error)
                : Result.Success((product.Value, stock.Value));
        });

        if (!Accept(received)) return;
        Show(received.Value.Stock);
        ReceiveCode = ReceiveQuantity = ReceiveReference = string.Empty;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, InventoryText.Received, quantity, received.Value.Product.Name, warehouse.Name);
    }

    private async Task AdjustAsync()
    {
        if (Selected is not { } line || AdjustReason is not { } reason) return;
        if (!decimal.TryParse(AdjustBy, NumberStyles.Number, CultureInfo.CurrentCulture, out var change) || change == 0) { Fail(InventoryText.AdjustInvalid); return; }

        var notes = string.IsNullOrWhiteSpace(AdjustNotes) ? null : AdjustNotes.Trim();
        var (search, filterId) = (SearchText, FilterWarehouse?.WarehouseId);
        var adjusted = await _runner.RunAsync<IReadOnlyList<StockOverviewItem>>(async (scope, ct) =>
        {
            var done = await scope.Get<AdjustStockCommandHandler>().HandleAsync(new AdjustStockCommand(line.StockItemId, change, reason.Reason, notes), ct);
            if (done.IsFailure) return Result.Failure<IReadOnlyList<StockOverviewItem>>(done.Error);
            return await scope.Get<GetStockOverviewQueryHandler>().HandleAsync(new GetStockOverviewQuery(search, filterId), ct);
        });

        if (!Accept(adjusted)) return;
        Show(adjusted.Value);
        AdjustBy = AdjustNotes = string.Empty;
        StatusMessage = string.Format(CultureInfo.CurrentCulture, InventoryText.Adjusted, line.ProductName, line.WarehouseName, change.ToString("+0.###;-0.###", CultureInfo.CurrentCulture));
    }

    private void Show(IReadOnlyList<StockOverviewItem> stock)
    {
        var selectedId = Selected?.StockItemId;
        Stock.Clear();
        foreach (var item in stock) Stock.Add(item);
        Selected = Stock.FirstOrDefault(s => s.StockItemId == selectedId);
        ResultInfo = Stock.Count == 0 ? InventoryText.NoStock : null;
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = null;
    }
}
