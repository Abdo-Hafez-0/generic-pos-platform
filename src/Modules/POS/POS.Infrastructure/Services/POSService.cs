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
    CheckoutCartCommandHandler checkoutHandler) : IPOSService
{
    public async Task<POSOpenSessionResult> OpenSessionAsync(
        string cashierReference, Guid warehouseId, CancellationToken cancellationToken = default)
    {
        var result = await openSessionHandler.HandleAsync(
            new OpenPosSessionCommand(cashierReference, warehouseId), cancellationToken);
        return result.IsSuccess
            ? POSOpenSessionResult.Success(result.Value)
            : POSOpenSessionResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<POSOperationResult> CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => ToOperation(await closeSessionHandler.HandleAsync(new ClosePosSessionCommand(sessionId), cancellationToken));

    public async Task<POSStartCartResult> StartCartAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var result = await startCartHandler.HandleAsync(new StartCartCommand(sessionId), cancellationToken);
        return result.IsSuccess
            ? POSStartCartResult.Success(result.Value)
            : POSStartCartResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<POSAddItemResult> AddProductAsync(
        Guid cartId, string productCode, decimal quantity = 1m, CancellationToken cancellationToken = default)
    {
        var result = await addProductHandler.HandleAsync(
            new AddProductToCartCommand(cartId, productCode, quantity), cancellationToken);
        return result.IsSuccess
            ? POSAddItemResult.Success(result.Value)
            : POSAddItemResult.Failure(result.Error.Code, result.Error.Description);
    }

    public async Task<POSOperationResult> RemoveProductAsync(
        Guid cartId, Guid productId, CancellationToken cancellationToken = default)
        => ToOperation(await removeProductHandler.HandleAsync(
            new RemoveProductFromCartCommand(cartId, productId), cancellationToken));

    public async Task<POSOperationResult> ChangeQuantityAsync(
        Guid cartId, Guid productId, decimal quantity, CancellationToken cancellationToken = default)
        => ToOperation(await changeQuantityHandler.HandleAsync(
            new ChangeCartQuantityCommand(cartId, productId, quantity), cancellationToken));

    public async Task<POSOperationResult> ClearCartAsync(Guid cartId, CancellationToken cancellationToken = default)
        => ToOperation(await clearCartHandler.HandleAsync(new ClearCartCommand(cartId), cancellationToken));

    public async Task<POSCheckoutResult> CheckoutAsync(
        Guid cartId, string? transactionReference = null, CancellationToken cancellationToken = default)
    {
        var result = await checkoutHandler.HandleAsync(
            new CheckoutCartCommand(cartId, transactionReference), cancellationToken);
        return result.IsSuccess
            ? POSCheckoutResult.Success(result.Value)
            : POSCheckoutResult.Failure(result.Error.Code, result.Error.Description);
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
    GetCurrentCartQueryHandler currentCartHandler) : IPOSReader
{
    public Task<POSSessionResult?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => sessionHandler.HandleAsync(new GetPosSessionQuery(sessionId), cancellationToken);

    public Task<POSCartResult?> GetCartAsync(Guid cartId, CancellationToken cancellationToken = default)
        => cartHandler.HandleAsync(new GetCartQuery(cartId), cancellationToken);

    public Task<POSCartResult?> GetCurrentCartAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => currentCartHandler.HandleAsync(new GetCurrentCartQuery(sessionId), cancellationToken);
}
