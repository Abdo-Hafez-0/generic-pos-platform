using Platform.Core.Amounts;
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
///
/// PRICING (FIX-08): prices include tax; each line snapshots its tax rate; the cashier may give a discount on a line and one on the whole
/// cart. <see cref="PricedLines"/> turns all of it into the amounts that are charged and recorded, with the one platform rule
/// (TaxInclusiveLine, rounded per line); the cart discount is spread over the lines (CartDiscountAllocation).
/// </summary>
/// <param name="Item">The cart line.</param>
/// <param name="Amounts">What is charged for it: gross, the line discount plus its share of the cart discount, total, tax, net.</param>
/// <param name="CartShare">Its share of the cart discount.</param>
public sealed record PricedCartLine(PosCartItem Item, TaxInclusiveLine Amounts, decimal CartShare);

/// <summary>The transaction a cashier is currently building (see the remarks above).</summary>
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

    /// <summary>
    /// The customer the sale is for (FIX-11), optional: a plain Guid reference to a Customers customer plus the code/name snapshot taken
    /// when the cashier chose them (no contact details are kept here). Handed to Sales at checkout.
    /// </summary>
    public Guid? CustomerId { get; private set; }
    public string? CustomerCode { get; private set; }
    public string? CustomerName { get; private set; }

    public IReadOnlyList<PosCartItem> Items => _items.AsReadOnly();

    /// <summary>Stored form of <see cref="CartDiscount"/> (FIX-08c): null = no cart discount.</summary>
    public DiscountKind? CartDiscountKind { get; private set; }

    public decimal? CartDiscountValue { get; private set; }

    /// <summary>The discount the cashier gave on the whole cart, as given, or null. FIX-08c.</summary>
    public DiscountRule? CartDiscount => CartDiscountKind is { } kind && CartDiscountValue is { } value ? new DiscountRule(kind, value) : null;

    /// <summary>What every line costs after the line discounts (the base a cart discount applies to).</summary>
    public decimal AfterLineDiscounts => _items.Sum(i => i.Amounts.Total);

    /// <summary>The cart discount as an amount against <see cref="AfterLineDiscounts"/>.</summary>
    public decimal CartDiscountAmount => CartDiscount?.AmountOf(AfterLineDiscounts) ?? 0m;

    /// <summary>The amounts that are charged and recorded, line by line (see the class remarks).</summary>
    public IReadOnlyList<PricedCartLine> PricedLines
    {
        get
        {
            var shares = CartDiscountAllocation.Spread(CartDiscountAmount, _items.Select(i => i.Amounts.Total).ToList());
            return _items.Select((item, n) => new PricedCartLine(
                item,
                TaxInclusiveLine.Compute(item.UnitPrice.Amount, item.Quantity.Value, item.LineDiscountAmount + shares[n], item.TaxRate),
                shares[n])).ToList();
        }
    }

    /// <summary>The lines before discounts (unit price x quantity, tax included).</summary>
    public Money Subtotal => new(PricedLines.Sum(l => l.Amounts.Gross));

    /// <summary>All discounts given: the line discounts and the cart discount (FIX-08c).</summary>
    public Money DiscountTotal => new(PricedLines.Sum(l => l.Amounts.Discount));

    /// <summary>The tax contained in <see cref="Total"/> (prices include tax - FIX-08b): the sum of the rounded line taxes.</summary>
    public Money TaxTotal => new(PricedLines.Sum(l => l.Amounts.Tax));

    /// <summary>Total payable, tax included: the sum of the rounded line totals.</summary>
    public Money Total => new(PricedLines.Sum(l => l.Amounts.Total));

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

    /// <summary>Gives (or with null removes) a discount on the line of a product. FIX-08c.</summary>
    public Result SetLineDiscount(Guid catalogProductId, DiscountRule? rule)
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        var item = FindItem(catalogProductId);
        if (item is null)
            return Result.Failure(Error.NotFound(
                "POS.Cart.ItemNotFound", $"Product '{catalogProductId}' is not in the cart."));

        item.SetLineDiscount(rule);
        Touch();
        return Result.Success();
    }

    /// <summary>Gives (or with null removes) a discount on the whole cart. FIX-08c.</summary>
    public Result SetCartDiscount(DiscountRule? rule)
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        if (rule is not null && _items.Count == 0)
            return Result.Failure(Error.Validation(
                "POS.Cart.Empty", "Add products before giving a discount on the cart."));

        CartDiscountKind = rule?.Kind;
        CartDiscountValue = rule?.Value;
        Touch();
        return Result.Success();
    }

    /// <summary>Chooses the customer of the sale (FIX-11), or with a null ID removes them. Open carts only.</summary>
    public Result SetCustomer(Guid? customerId, string? code = null, string? name = null)
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        if (customerId is { } id)
        {
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                return Result.Failure(Error.Validation("POS.Cart.CustomerInvalid", "A customer needs an ID, a code and a name."));
            (CustomerId, CustomerCode, CustomerName) = (id, code.Trim(), name.Trim());
        }
        else
        {
            (CustomerId, CustomerCode, CustomerName) = (null, null, null);
        }

        Touch();
        return Result.Success();
    }

    public Result Clear()
    {
        var open = EnsureOpen();
        if (open.IsFailure) return open;

        CartDiscountKind = null;
        CartDiscountValue = null;
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
