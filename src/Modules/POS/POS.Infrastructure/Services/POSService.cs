using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Core.Results;
using POS.Application.Commands;
using POS.Application.Queries;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace POS.Infrastructure.Services;

/// <summary>
/// Implements IPOSService by wrapping the command handlers and translating their Result types
/// into contract-level result records.
/// </summary>
internal sealed class POSService(
    OpenPosSessionCommandHandler openSessionHandler,
    ClosePosSessionCommandHandler closeSessionHandler,
    StartCartCommandHandler startCartHandler,
    AddProductToCartCommandHandler addProductHandler,
    RemoveProductFromCartCommandHandler removeProductHandler,
    ChangeCartQuantityCommandHandler changeQuantityHandler,
    ClearCartCommandHandler clearCartHandler,
    CheckoutCartCommandHandler checkoutHandler,
    SetLineDiscountCommandHandler lineDiscountHandler,
    SetCartDiscountCommandHandler cartDiscountHandler,
    ILogger<POSService>? logger = null) : IPOSService
{
    private readonly ILogger _logger = logger ?? NullLogger<POSService>.Instance;

    public Task<POSOpenSessionResult> OpenSessionAsync(
        string cashierReference, Guid warehouseId, CancellationToken cancellationToken = default)
        => GuardAsync("open session", POSOpenSessionResult.Failure, async () =>
        {
            var result = await openSessionHandler.HandleAsync(
                new OpenPosSessionCommand(cashierReference, warehouseId), cancellationToken);
            return result.IsSuccess
                ? POSOpenSessionResult.Success(result.Value)
                : POSOpenSessionResult.Failure(result.Error.Code, result.Error.Description);
        });

    public Task<POSOperationResult> CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => GuardAsync("close session", POSOperationResult.Failure, async () =>
            ToOperation(await closeSessionHandler.HandleAsync(new ClosePosSessionCommand(sessionId), cancellationToken)));

    public Task<POSStartCartResult> StartCartAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => GuardAsync("start cart", POSStartCartResult.Failure, async () =>
        {
            var result = await startCartHandler.HandleAsync(new StartCartCommand(sessionId), cancellationToken);
            return result.IsSuccess
                ? POSStartCartResult.Success(result.Value)
                : POSStartCartResult.Failure(result.Error.Code, result.Error.Description);
        });

    public Task<POSAddItemResult> AddProductAsync(
        Guid cartId, string productCode, decimal quantity = 1m, CancellationToken cancellationToken = default)
        => GuardAsync("add product", POSAddItemResult.Failure, async () =>
        {
            var result = await addProductHandler.HandleAsync(
                new AddProductToCartCommand(cartId, productCode, quantity), cancellationToken);
            return result.IsSuccess
                ? POSAddItemResult.Success(result.Value)
                : POSAddItemResult.Failure(result.Error.Code, result.Error.Description);
        });

    public Task<POSOperationResult> RemoveProductAsync(
        Guid cartId, Guid productId, CancellationToken cancellationToken = default)
        => GuardAsync("remove product", POSOperationResult.Failure, async () =>
            ToOperation(await removeProductHandler.HandleAsync(
                new RemoveProductFromCartCommand(cartId, productId), cancellationToken)));

    public Task<POSOperationResult> ChangeQuantityAsync(
        Guid cartId, Guid productId, decimal quantity, CancellationToken cancellationToken = default)
        => GuardAsync("change quantity", POSOperationResult.Failure, async () =>
            ToOperation(await changeQuantityHandler.HandleAsync(
                new ChangeCartQuantityCommand(cartId, productId, quantity), cancellationToken)));

    public Task<POSOperationResult> ClearCartAsync(Guid cartId, CancellationToken cancellationToken = default)
        => GuardAsync("clear cart", POSOperationResult.Failure, async () =>
            ToOperation(await clearCartHandler.HandleAsync(new ClearCartCommand(cartId), cancellationToken)));

    public Task<POSOperationResult> SetLineDiscountAsync(Guid cartId, Guid productId, POSDiscountKind kind, decimal value, CancellationToken cancellationToken = default)
        => GuardAsync("line discount", POSOperationResult.Failure, async () =>
            ToOperation(await lineDiscountHandler.HandleAsync(new SetLineDiscountCommand(cartId, productId, (POS.Domain.ValueObjects.DiscountKind)(int)kind, value), cancellationToken)));

    public Task<POSOperationResult> SetCartDiscountAsync(Guid cartId, POSDiscountKind kind, decimal value, CancellationToken cancellationToken = default)
        => GuardAsync("cart discount", POSOperationResult.Failure, async () =>
            ToOperation(await cartDiscountHandler.HandleAsync(new SetCartDiscountCommand(cartId, (POS.Domain.ValueObjects.DiscountKind)(int)kind, value), cancellationToken)));

    /// <summary>
    /// An unexpected failure (database locked or unavailable, disk error) must reach the cashier as a plain statement that the action did not
    /// happen - never as an exception, a stack trace or database text. The details go to the log only.
    /// </summary>
    private async Task<T> GuardAsync<T>(string operation, Func<string, string, T> failure, Func<Task<T>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "POS operation '{Operation}' failed unexpectedly; nothing was changed.", operation);
            return failure("POS.OperationFailed",
                "The operation could not be completed and nothing was changed. Try again; if it keeps failing, contact support.");
        }
    }

    public async Task<POSCheckoutResult> CheckoutAsync(
        Guid cartId, string? transactionReference = null, POSPaymentRequest? payment = null, CancellationToken cancellationToken = default)
    {
        Result<CheckoutOutcome> result;
        try
        {
            result = await checkoutHandler.HandleAsync(
                new CheckoutCartCommand(cartId, transactionReference, payment), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The sale ran inside one database transaction, so an unexpected failure (disk, lock, corruption) saved nothing.
            // The cashier gets a plain statement of that; the details (which may name tables or files) go to the log only.
            _logger.LogError(ex, "Checkout of cart {CartId} failed unexpectedly; the database transaction was rolled back.", cartId);
            return POSCheckoutResult.Failure("POS.Checkout.NotSaved",
                "The sale could not be saved and nothing was changed. Try again; if it keeps failing, contact support.");
        }

        if (result.IsFailure)
            return POSCheckoutResult.Failure(result.Error.Code, result.Error.Description);

        foreach (var notice in result.Value.HardwareNotices ?? [])
            _logger.LogWarning("Sale {SaleId} completed but a peripheral failed ({Device}, {Code}): {Message}", result.Value.SaleId, notice.Device, notice.ErrorCode, notice.Message);

        return POSCheckoutResult.Success(result.Value.SaleId, result.Value.PaymentId, result.Value.ChangeDue, result.Value.HardwareNotices);
    }

    private static POSOperationResult ToOperation(Result result)
        => result.IsSuccess
            ? POSOperationResult.Success()
            : POSOperationResult.Failure(result.Error.Code, result.Error.Description);
}

/// <summary>Implements IPOSReader by delegating to the application query handlers.</summary>
internal sealed class POSReader(
    GetPosSessionQueryHandler sessionHandler,
    GetCartQueryHandler cartHandler,
    GetCurrentCartQueryHandler currentCartHandler,
    GetOpenSessionForCashierQueryHandler openSessionHandler,
    GetSaleWarehousesQueryHandler warehousesHandler) : IPOSReader
{
    public Task<POSSessionResult?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => sessionHandler.HandleAsync(new GetPosSessionQuery(sessionId), cancellationToken);

    public Task<POSCartResult?> GetCartAsync(Guid cartId, CancellationToken cancellationToken = default)
        => cartHandler.HandleAsync(new GetCartQuery(cartId), cancellationToken);

    public Task<POSCartResult?> GetCurrentCartAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => currentCartHandler.HandleAsync(new GetCurrentCartQuery(sessionId), cancellationToken);

    public Task<POSSessionResult?> FindOpenSessionAsync(string cashierReference, CancellationToken cancellationToken = default)
        => openSessionHandler.HandleAsync(new GetOpenSessionForCashierQuery(cashierReference), cancellationToken);

    public Task<IReadOnlyList<POSWarehouseResult>> GetWarehousesAsync(CancellationToken cancellationToken = default)
        => warehousesHandler.HandleAsync(new GetSaleWarehousesQuery(), cancellationToken);
}
