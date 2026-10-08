using Platform.Core.Results;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;

namespace POS.Domain.Entities;

/// <summary>
/// The transaction a cashier is currently building (the cart).
///
/// POS owns the cart; it does NOT own the sale. On checkout the application layer hands the cart
/// to Sales through Sales.Contracts, then records the resulting SaleId here via
/// <see cref="MarkCheckedOut"/>. SaleId is a plain Guid — no reference to Sales domain types.
///
/// A cart holds at most one line per product; adding the same product again increases the quantity
/// of the existing line (the original unit price snapshot is kept).
/// </summary>
public sealed class PosCart
{
    private readonly List<PosCartItem> _items = [];

    private PosCart() { }

    public PosCartId Id { get; private set; }
    public PosSessionId SessionId { get; private set; }
    public PosCartStatus Status { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? CheckedOutAt { get; private set; }

    /// <summary>The Sales module's sale ID once checked out. Reference by ID only.</summary>
    public Guid? SaleId { get; private set; }

    public IReadOnlyList<PosCartItem> Items => _items.AsReadOnly();

    /// <summary>The lines before discounts (unit price x quantity, tax included).</summary>
    public Money Subtotal => _items.Aggregate(Money.Zero, (acc, i) => acc + new Money(i.Amounts.Gross));

    /// <summary>The tax contained in <see cref="Total"/> (prices include tax - FIX-08b): the sum of the rounded line taxes.</summary>
    public Money TaxTotal => _items.Aggregate(Money.Zero, (acc, i) => acc + i.TaxAmount);

    /// <summary>Total payable, tax included: the sum of the rounded line totals.</summary>
    public Money Total => _items.Aggregate(Money.Zero, (acc, i) => acc + i.LineTotal);

    public decimal TotalQuantity => _items.Sum(i => i.Quantity.Value);

    public static Result<PosCart> Start(PosSessionId sessionId)
    {
        if (sessionId == PosSessionId.Empty)
            return Result.Failure<PosCart>(Error.Validation(
                "POS.Cart.SessionRequired", "A cart must belong to a POS session."));

        var now = DateTime.UtcNow;
        return Result.Success(new PosCart
        {
            Id = PosCartId.New(),
            SessionId = sessionId,
            Status = PosCartStatus.Open,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    public PosCartItem? FindItem(Guid catalogProductId)
        => _items.FirstOrDefault(i => i.CatalogProductId == catalogProductId);

    public Result<PosCartItem> AddItem(
        Guid catalogProductId,
        string productSku,
        string productName,
        CartQuantity quantity,
        Money unitPrice,
        decimal taxRate = 0m)
    {
        var open = EnsureOpen<PosCartItem>();
        if (open.IsFailure) return open;

        var existing = FindItem(catalogProductId);
        if (existing is not null)
        {
            existing.SetQuantity(existing.Quantity + quantity);
            Touch();
            return Result.Success(existing);
        }

        // a merge keeps the first line's price and tax snapshots
        var itemResult = PosCartItem.Create(Id, catalogProductId, productSku, productName, quantity, unitPrice, taxRate);
        if (itemResult.IsFailure) return itemResult;

        _items.Add(itemResult.Value);
        Touch();
        return itemResult;
    }

    public Result RemoveItem(Guid catalogProductId)
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        var item = FindItem(catalogProductId);
        if (item is null)
            return Result.Failure(Error.NotFound(
                "POS.Cart.ItemNotFound", $"Product '{catalogProductId}' is not in the cart."));

        _items.Remove(item);
        Touch();
        return Result.Success();
    }

    public Result ChangeQuantity(Guid catalogProductId, CartQuantity quantity)
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        var item = FindItem(catalogProductId);
        if (item is null)
            return Result.Failure(Error.NotFound(
                "POS.Cart.ItemNotFound", $"Product '{catalogProductId}' is not in the cart."));

        item.SetQuantity(quantity);
        Touch();
        return Result.Success();
    }

    public Result Clear()
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        _items.Clear();
        Touch();
        return Result.Success();
    }

    /// <summary>Records that Sales completed the sale for this cart. Terminal.</summary>
    public Result MarkCheckedOut(Guid saleId)
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        if (_items.Count == 0)
            return Result.Failure(Error.Validation(
                "POS.Cart.Empty", "An empty cart cannot be checked out."));

        if (saleId == Guid.Empty)
            return Result.Failure(Error.Validation(
                "POS.Cart.SaleRequired", "A valid sale ID is required to complete checkout."));

        var now = DateTime.UtcNow;
        Status = PosCartStatus.CheckedOut;
        SaleId = saleId;
        CheckedOutAt = now;
        UpdatedAt = now;
        return Result.Success();
    }

    private Result EnsureOpen()
        => Status == PosCartStatus.Open
            ? Result.Success()
            : Result.Failure(Error.Conflict(
                "POS.Cart.NotOpen", $"The cart is not open. Current status: {Status}."));

    private Result<T> EnsureOpen<T>()
        => Status == PosCartStatus.Open
            ? Result.Success<T>(default!)
            : Result.Failure<T>(Error.Conflict(
                "POS.Cart.NotOpen", $"The cart is not open. Current status: {Status}."));

    private void Touch() => UpdatedAt = DateTime.UtcNow;
}
