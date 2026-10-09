using POS.Contracts.Models;

namespace POS.Contracts.Interfaces;

/// <summary>
/// The POS write API for the platform/UI: session handling, cart editing and checkout.
///
/// Implemented by POS.Infrastructure.Services.POSService. Uses only IDs, primitives and result
/// records — never POS.Domain types.
///
/// Checkout orchestrates Catalog, Inventory and Sales through THEIR Contracts. Payments are recorded only
/// when requested and the optional Payments module is installed; no real payment processing exists.
/// </summary>
public interface IPOSService
{
    Task<POSOpenSessionResult> OpenSessionAsync(string cashierReference, Guid warehouseId, CancellationToken cancellationToken = default);

    Task<POSOperationResult> CloseSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Starts a cart for the session, or returns the session's existing open cart.</summary>
    Task<POSStartCartResult> StartCartAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>Adds a product identified by barcode or SKU. Validates the product and stock.</summary>
    Task<POSAddItemResult> AddProductAsync(Guid cartId, string productCode, decimal quantity = 1m, CancellationToken cancellationToken = default);

    Task<POSOperationResult> RemoveProductAsync(Guid cartId, Guid productId, CancellationToken cancellationToken = default);

    Task<POSOperationResult> ChangeQuantityAsync(Guid cartId, Guid productId, decimal quantity, CancellationToken cancellationToken = default);

    Task<POSOperationResult> ClearCartAsync(Guid cartId, CancellationToken cancellationToken = default);

    /// <summary>Gives a discount on the line of a product (percentage or tax-included amount); value 0 removes it. FIX-08c.</summary>
    Task<POSOperationResult> SetLineDiscountAsync(Guid cartId, Guid productId, POSDiscountKind kind, decimal value, CancellationToken cancellationToken = default);

    /// <summary>Gives a discount on the whole cart (percentage or tax-included amount), spread over the lines; value 0 removes it. FIX-08c.</summary>
    Task<POSOperationResult> SetCartDiscountAsync(Guid cartId, POSDiscountKind kind, decimal value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates, confirms and completes the sale in Sales and issues stock from Inventory. If <paramref name="payment"/> is given,
    /// the payment for the cart total is recorded through the optional Payments module between confirming the sale and issuing stock.
    /// </summary>
    Task<POSCheckoutResult> CheckoutAsync(Guid cartId, string? transactionReference = null, POSPaymentRequest? payment = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// FIX-10: checks out with a split payment - several parts (cash, card, other), each naming its amount; the amounts add up to the cart
    /// total, only cash may be tendered above its amount (the change). Every part is recorded in the same transaction as the sale.
    /// </summary>
    Task<POSCheckoutResult> CheckoutWithPaymentsAsync(Guid cartId, IReadOnlyList<POSPaymentRequest> payments, string? transactionReference = null, CancellationToken cancellationToken = default);
}
