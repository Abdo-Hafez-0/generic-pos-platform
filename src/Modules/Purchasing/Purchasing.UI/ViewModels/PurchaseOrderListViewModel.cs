using System.Collections.ObjectModel;
using Purchasing.Application.Commands;
using Purchasing.Application.DTOs;
using Purchasing.Application.Queries;

namespace Purchasing.UI.ViewModels;

/// <summary>Minimal purchasing screen: list orders, create a draft, add a line, submit and receive. Application handlers only.</summary>
public sealed class PurchaseOrderListViewModel(
    ListPurchaseOrdersQueryHandler list,
    CreatePurchaseOrderCommandHandler create,
    AddPurchaseOrderLineCommandHandler addLine,
    SubmitPurchaseOrderCommandHandler submit,
    ReceivePurchaseOrderCommandHandler receive) : ViewModelBase
{
    private string? _error;
    private bool _isBusy;

    public ObservableCollection<PurchaseOrderListItemDto> Orders { get; } = [];
    public string? ErrorMessage { get => _error; private set => Set(ref _error, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }

    public Task LoadAsync(CancellationToken cancellationToken = default) => RunAsync(() => RefreshAsync(cancellationToken));

    public Task CreateDraftAsync(Guid supplierId, string? reference, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            var r = await create.HandleAsync(new CreatePurchaseOrderCommand(supplierId, reference), cancellationToken);
            if (r.IsFailure) { ErrorMessage = r.Error.Description; return; }
            await RefreshAsync(cancellationToken);
        });

    public Task AddLineAsync(Guid orderId, string productCode, decimal quantity, decimal? unitCost, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            var r = await addLine.HandleAsync(new AddPurchaseOrderLineCommand(orderId, productCode, quantity, unitCost), cancellationToken);
            if (r.IsFailure) ErrorMessage = r.Error.Description;
            await RefreshAsync(cancellationToken);
        });

    public Task SubmitAsync(Guid orderId, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            var r = await submit.HandleAsync(new SubmitPurchaseOrderCommand(orderId), cancellationToken);
            if (r.IsFailure) ErrorMessage = r.Error.Description;
            await RefreshAsync(cancellationToken);
        });

    public Task ReceiveAsync(Guid orderId, Guid warehouseId, CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            var r = await receive.HandleAsync(new ReceivePurchaseOrderCommand(orderId, warehouseId), cancellationToken);
            if (r.IsFailure) ErrorMessage = r.Error.Description;
            await RefreshAsync(cancellationToken);
        });

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var page = await list.HandleAsync(new ListPurchaseOrdersQuery(), cancellationToken);
        Orders.Clear();
        foreach (var item in page.Items) Orders.Add(item);
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
