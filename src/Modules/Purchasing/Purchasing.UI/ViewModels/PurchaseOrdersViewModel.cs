using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Mvvm;
using Platform.Presentation.Screens;
using Purchasing.Application.Commands;
using Purchasing.Application.DTOs;
using Purchasing.Application.Queries;
using Purchasing.Domain.Enums;
using Purchasing.UI.Resources;

namespace Purchasing.UI.ViewModels;

/// <summary>A status filter choice; <see cref="Status"/> null means every order.</summary>
public sealed record StatusChoice(PurchaseOrderStatus? Status, string Name);

/// <summary>One order as the list shows it.</summary>
public sealed record OrderRow(PurchaseOrderListItemDto Order, string StatusText, string CreatedText);

/// <summary>
/// One line of the selected order (FIX-09): what was ordered, received so far and still expected, and - while the order awaits goods -
/// how much arrived in the delivery being entered (<see cref="ReceiveNow"/>, starting at what is still expected).
/// </summary>
public sealed class OrderLineRow(PurchaseOrderLineDto line) : System.ComponentModel.INotifyPropertyChanged
{
    private string _receiveNow = line.OutstandingQuantity > 0m ? line.OutstandingQuantity.ToString("0.###", CultureInfo.CurrentCulture) : string.Empty;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public PurchaseOrderLineDto Line { get; } = line;
    public Guid LineId => Line.LineId;
    public string ProductSku => Line.ProductSku;
    public string ProductName => Line.ProductName;
    public decimal Quantity => Line.Quantity;
    public decimal UnitCost => Line.UnitCost;
    public decimal LineTotal => Line.LineTotal;
    public decimal ReceivedQuantity => Line.ReceivedQuantity;
    public decimal OutstandingQuantity => Line.OutstandingQuantity;
    public bool IsReceived => Line.IsReceived;

    /// <summary>Only a line with something still expected can take part in a delivery.</summary>
    public bool CanReceive => Line.OutstandingQuantity > 0m;

    public string ReceiveNow
    {
        get => _receiveNow;
        set
        {
            if (_receiveNow == value) return;
            _receiveNow = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ReceiveNow)));
        }
    }
}

/// <summary>
/// Purchase orders (FIX-01d): list by status; create an order for an active supplier; while it is a draft add and remove lines (by SKU,
/// unit cost defaults to the product's cost) and place it; cancel a draft or placed order with a reason; receive a placed order into a
/// warehouse (one transaction with Inventory, Stage 12). Through the runner; the handlers authorize (purchasing.order.create/.submit/
/// .cancel/.receive) and refuse what the order's state does not allow, in plain words.
///
/// FIX-09: a delivery says how much of each line arrived (starting at what is still expected); a part delivery leaves the order
/// "Partly received" for the next delivery, and such an order can be closed short with a reason.
/// </summary>
public sealed class PurchaseOrdersViewModel : ViewModelBase, INavigationAware
{
    private readonly IUiActionRunner _runner;

    private StatusChoice _filter;
    private OrderRow? _selectedRow;
    private PurchaseOrderDto? _order;
    private OrderLineRow? _selectedLine;
    private string _supplierText = string.Empty;
    private OrderSupplierDto? _supplier;
    private string _newReference = string.Empty;
    private string _lineCode = string.Empty;
    private string _lineQuantity = string.Empty;
    private string _lineCost = string.Empty;
    private string _cancelReason = string.Empty;
    private ReceivingWarehouseDto? _warehouse;

    public PurchaseOrdersViewModel(IUiActionRunner runner)
    {
        _runner = runner;
        Filters =
        [
            new(null, PurchasingText.AllStatuses), new(PurchaseOrderStatus.Draft, PurchasingText.Draft), new(PurchaseOrderStatus.Submitted, PurchasingText.Submitted),
            new(PurchaseOrderStatus.PartiallyReceived, PurchasingText.PartiallyReceived), new(PurchaseOrderStatus.Received, PurchasingText.Received),
            new(PurchaseOrderStatus.Closed, PurchasingText.ClosedShort), new(PurchaseOrderStatus.Cancelled, PurchasingText.Cancelled),
        ];
        _filter = Filters[0];

        ShowCommand = Command(() => LoadOrdersAsync(CancellationToken.None));
        FindSupplierCommand = Command(FindSuppliersAsync, () => !string.IsNullOrWhiteSpace(SupplierText));
        CreateCommand = Command(CreateAsync, () => Supplier is not null);
        AddLineCommand = Command(AddLineAsync, () => IsDraft && !string.IsNullOrWhiteSpace(LineCode) && !string.IsNullOrWhiteSpace(LineQuantity));
        RemoveLineCommand = Command(RemoveLineAsync, () => IsDraft && SelectedLine is not null);
        SubmitCommand = Command(() => ChangeAsync(OrderChange.Submit), () => IsDraft && Order!.Lines.Count > 0);
        CancelOrderCommand = Command(() => ChangeAsync(OrderChange.Cancel), () => (IsDraft || IsSubmitted) && !string.IsNullOrWhiteSpace(CancelReason));
        ReceiveCommand = Command(() => ChangeAsync(OrderChange.Receive), () => IsAwaitingGoods);
        CloseShortCommand = Command(() => ChangeAsync(OrderChange.CloseShort), () => IsPartiallyReceived && !string.IsNullOrWhiteSpace(CancelReason));
    }

    private enum OrderChange { Submit, Cancel, Receive, CloseShort }

    public IReadOnlyList<StatusChoice> Filters { get; }
    public ObservableCollection<OrderRow> Orders { get; } = [];
    public ObservableCollection<OrderSupplierDto> Suppliers { get; } = [];
    public ObservableCollection<ReceivingWarehouseDto> Warehouses { get; } = [];
    public ObservableCollection<OrderLineRow> Lines { get; } = [];

    public ICommand ShowCommand { get; }
    public ICommand FindSupplierCommand { get; }
    public ICommand CreateCommand { get; }
    public ICommand AddLineCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand SubmitCommand { get; }
    public ICommand CancelOrderCommand { get; }
    public ICommand ReceiveCommand { get; }
    public ICommand CloseShortCommand { get; }

    public StatusChoice Filter { get => _filter; set => Set(ref _filter, value); }

    /// <summary>The order selected in the list; selecting one loads it (with its lines).</summary>
    public OrderRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (Set(ref _selectedRow, value) && value is not null && value.Order.OrderId != Order?.OrderId)
                _ = BusyAsync(() => LoadOrderAsync(value.Order.OrderId));
        }
    }

    /// <summary>The order being worked on, or null.</summary>
    public PurchaseOrderDto? Order
    {
        get => _order;
        private set
        {
            if (!Set(ref _order, value)) return;
            Lines.Clear();
            if (value is not null) foreach (var line in value.Lines) Lines.Add(new OrderLineRow(line));
            foreach (var name in new[] { nameof(HasOrder), nameof(HasNoOrder), nameof(IsDraft), nameof(IsSubmitted), nameof(IsPartiallyReceived),
                         nameof(IsAwaitingGoods), nameof(CanEnd), nameof(OrderHeading) }) Raise(name);
        }
    }

    public bool HasOrder => Order is not null;
    public bool HasNoOrder => Order is null;
    public bool IsDraft => Order?.Status == PurchaseOrderStatus.Draft;
    public bool IsSubmitted => Order?.Status == PurchaseOrderStatus.Submitted;
    public bool IsPartiallyReceived => Order?.Status == PurchaseOrderStatus.PartiallyReceived;

    /// <summary>Goods are still expected: a delivery can be received (Submitted or PartiallyReceived).</summary>
    public bool IsAwaitingGoods => IsSubmitted || IsPartiallyReceived;

    /// <summary>The order can still be ended: cancelled (draft or placed) or closed short (partly received).</summary>
    public bool CanEnd => IsDraft || IsAwaitingGoods;

    public string OrderHeading => Order is null ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderHeading, Order.Number, Order.SupplierName, Describe(Order.Status));

    public OrderLineRow? SelectedLine { get => _selectedLine; set => Set(ref _selectedLine, value); }
    public string SupplierText { get => _supplierText; set => Set(ref _supplierText, value); }
    public OrderSupplierDto? Supplier { get => _supplier; set => Set(ref _supplier, value); }
    public string NewReference { get => _newReference; set => Set(ref _newReference, value); }
    public string LineCode { get => _lineCode; set => Set(ref _lineCode, value); }
    public string LineQuantity { get => _lineQuantity; set => Set(ref _lineQuantity, value); }
    public string LineCost { get => _lineCost; set => Set(ref _lineCost, value); }
    public string CancelReason { get => _cancelReason; set => Set(ref _cancelReason, value); }
    public ReceivingWarehouseDto? Warehouse { get => _warehouse; set => Set(ref _warehouse, value); }

    public Task OnNavigatedToAsync(CancellationToken cancellationToken = default) => BusyAsync(async () =>
    {
        var warehouses = await _runner.QueryAsync((scope, ct) => scope.Get<ListReceivingWarehousesQueryHandler>().HandleAsync(new ListReceivingWarehousesQuery(), ct), cancellationToken);
        if (Accept(warehouses))
        {
            var keep = Warehouse?.WarehouseId;
            Warehouses.Clear();
            foreach (var w in warehouses.Value) Warehouses.Add(w);
            Warehouse = Warehouses.FirstOrDefault(w => w.WarehouseId == keep) ?? (Warehouses.Count == 1 ? Warehouses[0] : null);
        }

        await LoadOrdersAsync(cancellationToken);
        if (Order is { } order) await LoadOrderAsync(order.OrderId);
    });

    private async Task LoadOrdersAsync(CancellationToken cancellationToken)
    {
        var status = Filter.Status;
        var page = await _runner.QueryAsync((scope, ct) => scope.Get<ListPurchaseOrdersQueryHandler>().HandleAsync(new ListPurchaseOrdersQuery(0, 200, status), ct), cancellationToken);
        if (!Accept(page)) return;

        var keep = Order?.OrderId;
        Orders.Clear();
        foreach (var o in page.Value.Items)
            Orders.Add(new OrderRow(o, Describe(o.Status), DateTime.SpecifyKind(o.CreatedAt, DateTimeKind.Utc).ToLocalTime().ToString("d", CultureInfo.CurrentCulture)));
        _selectedRow = Orders.FirstOrDefault(o => o.Order.OrderId == keep);
        Raise(nameof(SelectedRow));
    }

    private async Task LoadOrderAsync(Guid orderId)
    {
        var order = await _runner.QueryAsync((scope, ct) => scope.Get<GetPurchaseOrderQueryHandler>().HandleAsync(new GetPurchaseOrderQuery(orderId), ct));
        if (Accept(order)) Order = order.Value;
    }

    private async Task FindSuppliersAsync()
    {
        var text = SupplierText;
        var found = await _runner.QueryAsync((scope, ct) => scope.Get<FindOrderSuppliersQueryHandler>().HandleAsync(new FindOrderSuppliersQuery(text), ct));
        if (!Accept(found)) return;

        Suppliers.Clear();
        foreach (var s in found.Value) Suppliers.Add(s);
        Supplier = Suppliers.Count == 1 ? Suppliers[0] : null;
        if (Suppliers.Count == 0) { ErrorMessage = PurchasingText.NoSupplierFound; }
    }

    private async Task CreateAsync()
    {
        if (Supplier is not { } supplier) return;
        var reference = string.IsNullOrWhiteSpace(NewReference) ? null : NewReference.Trim();
        var created = await _runner.RunAsync<PurchaseOrderDto>(async (scope, ct) =>
        {
            var id = await scope.Get<CreatePurchaseOrderCommandHandler>().HandleAsync(new CreatePurchaseOrderCommand(supplier.SupplierId, reference), ct);
            if (id.IsFailure) return Result.Failure<PurchaseOrderDto>(id.Error);
            return Result.Success((await scope.Get<GetPurchaseOrderQueryHandler>().HandleAsync(new GetPurchaseOrderQuery(id.Value), ct))!);
        });
        if (!Accept(created)) return;

        (SupplierText, NewReference, Supplier) = (string.Empty, string.Empty, null);
        Suppliers.Clear();
        Order = created.Value;
        await LoadOrdersAsync(CancellationToken.None);
        StatusMessage = string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderCreated, created.Value.Number);
    }

    private async Task AddLineAsync()
    {
        if (Order is not { } order) return;
        if (!decimal.TryParse(LineQuantity, NumberStyles.Number, CultureInfo.CurrentCulture, out var quantity) || quantity <= 0) { Fail(PurchasingText.QuantityInvalid); return; }

        decimal? cost = null;
        if (!string.IsNullOrWhiteSpace(LineCost))
        {
            if (!decimal.TryParse(LineCost, NumberStyles.Number, CultureInfo.CurrentCulture, out var c) || c < 0) { Fail(PurchasingText.CostInvalid); return; }
            cost = c;
        }

        var code = LineCode.Trim();
        if (!await ChangeOrderAsync(order.OrderId, (scope, ct) => Drop(scope.Get<AddPurchaseOrderLineCommandHandler>().HandleAsync(new AddPurchaseOrderLineCommand(order.OrderId, code, quantity, cost), ct))))
            return;

        (LineCode, LineQuantity, LineCost) = (string.Empty, string.Empty, string.Empty);
        StatusMessage = PurchasingText.LineAdded;
    }

    private async Task RemoveLineAsync()
    {
        if (Order is not { } order || SelectedLine is not { } line) return;
        if (await ChangeOrderAsync(order.OrderId, (scope, ct) => scope.Get<RemovePurchaseOrderLineCommandHandler>().HandleAsync(new RemovePurchaseOrderLineCommand(order.OrderId, line.LineId), ct)))
            StatusMessage = PurchasingText.LineRemoved;
    }

    private async Task ChangeAsync(OrderChange change)
    {
        if (Order is not { } order) return;
        if (change == OrderChange.Receive && Warehouse is null) { Fail(PurchasingText.ChooseWarehouse); return; }

        List<ReceiveLineQuantity>? delivery = null;
        if (change == OrderChange.Receive)
        {
            delivery = [];
            foreach (var row in Lines.Where(r => r.CanReceive))
            {
                if (string.IsNullOrWhiteSpace(row.ReceiveNow)) continue;
                if (!decimal.TryParse(row.ReceiveNow, NumberStyles.Number, CultureInfo.CurrentCulture, out var arrived) || arrived < 0) { Fail(PurchasingText.ReceiveQuantityInvalid); return; }
                delivery.Add(new ReceiveLineQuantity(row.LineId, arrived));
            }
        }

        var (reason, warehouseId) = (CancelReason.Trim(), Warehouse?.WarehouseId ?? Guid.Empty);
        var received = 0;
        var done = await ChangeOrderAsync(order.OrderId, async (scope, ct) => change switch
        {
            OrderChange.Submit => await scope.Get<SubmitPurchaseOrderCommandHandler>().HandleAsync(new SubmitPurchaseOrderCommand(order.OrderId), ct),
            OrderChange.Cancel => await scope.Get<CancelPurchaseOrderCommandHandler>().HandleAsync(new CancelPurchaseOrderCommand(order.OrderId, reason), ct),
            OrderChange.CloseShort => await scope.Get<ClosePurchaseOrderShortCommandHandler>().HandleAsync(new ClosePurchaseOrderShortCommand(order.OrderId, reason), ct),
            _ => await Count(scope.Get<ReceivePurchaseOrderCommandHandler>().HandleAsync(new ReceivePurchaseOrderCommand(order.OrderId, warehouseId, delivery), ct), n => received = n),
        });
        if (!done) return;

        if (change is OrderChange.Cancel or OrderChange.CloseShort) CancelReason = string.Empty;
        await LoadOrdersAsync(CancellationToken.None);
        StatusMessage = change switch
        {
            OrderChange.Submit => string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderSubmitted, order.Number),
            OrderChange.Cancel => string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderCancelled, order.Number),
            OrderChange.CloseShort => string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderClosedShort, order.Number),
            _ when IsPartiallyReceived => string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderPartlyReceived, order.Number, received),
            _ => string.Format(CultureInfo.CurrentCulture, PurchasingText.OrderReceived, order.Number, received),
        };
    }

    /// <summary>Runs a change and reloads the order in the same action; false (with the plain message shown) when the change was refused.</summary>
    private async Task<bool> ChangeOrderAsync(Guid orderId, Func<IActionScope, CancellationToken, Task<Result>> change)
    {
        var changed = await _runner.RunAsync<PurchaseOrderDto>(async (scope, ct) =>
        {
            var result = await change(scope, ct);
            if (result.IsFailure) return Result.Failure<PurchaseOrderDto>(result.Error);
            return Result.Success((await scope.Get<GetPurchaseOrderQueryHandler>().HandleAsync(new GetPurchaseOrderQuery(orderId), ct))!);
        });
        if (!Accept(changed)) return false;

        Order = changed.Value;
        return true;
    }

    private static async Task<Result> Drop<T>(Task<Result<T>> pending) => await pending is { IsFailure: true } r ? Result.Failure(r.Error) : Result.Success();

    private static async Task<Result> Count(Task<Result<int>> pending, Action<int> received)
    {
        var result = await pending;
        if (result.IsFailure) return Result.Failure(result.Error);
        received(result.Value);
        return Result.Success();
    }

    private static string Describe(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Draft => PurchasingText.Draft,
        PurchaseOrderStatus.Submitted => PurchasingText.Submitted,
        PurchaseOrderStatus.PartiallyReceived => PurchasingText.PartiallyReceived,
        PurchaseOrderStatus.Received => PurchasingText.Received,
        PurchaseOrderStatus.Closed => PurchasingText.ClosedShort,
        _ => PurchasingText.Cancelled,
    };

    private void Fail(string message)
    {
        ErrorMessage = message;
        StatusMessage = null;
    }
}
