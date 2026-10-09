using Platform.Core.Results;
using Sales.Domain.Enums;
using Sales.Domain.Events;
using Sales.Domain.ValueObjects;

namespace Sales.Domain.Entities;

/// <summary>
/// The Sale aggregate root.
///
/// Owns the lifecycle of a commercial sales transaction, from Draft to Completed/Cancelled.
///
/// HISTORICAL DATA RULE (Architecture §18, Module Map §18):
/// Once Completed, the Sale and its items must not be mutated. All price/discount/tax
/// values were snapshotted at creation time and represent the historical truth of the sale.
///
/// LIFECYCLE:
///   Draft -> Confirmed -> Completed   (normal path)
///   Draft -> Cancelled                (cancelled before confirmation)
///   Confirmed -> Cancelled            (cancelled after confirmation but before completion)
///
/// INVARIANTS:
/// - A Sale must have a valid identity.
/// - Draft and Confirmed sales accept item additions (while in Draft).
/// - Items can only be added in Draft status.
/// - A Sale cannot be Completed if it has no items.
/// - A Completed or Cancelled sale cannot be mutated.
///
/// Architecture: Sales.Domain — no EF Core, no WPF, no HTTP.
/// Architecture reference: Module Map §16 (Sales Responsibility), §17 (Sales Owns).
/// </summary>
public sealed class Sale
{
    private readonly List<SaleItem> _items = [];
    private readonly List<object> _domainEvents = [];

    private Sale() { }

    public SaleId Id { get; private set; }
    public SaleStatus Status { get; private set; }

    /// <summary>
    /// Optional reference number (e.g., POS session reference, order number).
    /// </summary>
    public string? Reference { get; private set; }

    /// <summary>Optional notes for this sale.</summary>
    public string? Notes { get; private set; }

    /// <summary>
    /// The customer the sale was made to (FIX-11), optional. A plain Guid reference to a Customers customer plus a code/name SNAPSHOT taken
    /// at the sale (rule 14): later changes in Customers never rewrite history. Sales never calls the Customers module itself.
    /// </summary>
    public Guid? CustomerId { get; private set; }
    public string? CustomerCode { get; private set; }
    public string? CustomerName { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }

    /// <summary>Reason for cancellation, if applicable.</summary>
    public string? CancellationReason { get; private set; }

    /// <summary>Read-only view of the sale's line items.</summary>
    public IReadOnlyList<SaleItem> Items => _items.AsReadOnly();

    /// <summary>Domain events raised during this aggregate's lifetime.</summary>
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Clears collected domain events after dispatch.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // Computed totals — derived from items (no separate stored total)
    /// <summary>Sum of all item SubTotals (before tax).</summary>
    public Money SubTotal => _items.Aggregate(Money.Zero, (acc, i) => acc + i.SubTotal);

    /// <summary>Sum of all item TaxAmounts.</summary>
    public Money TaxTotal => _items.Aggregate(Money.Zero, (acc, i) => acc + i.TaxAmount);

    /// <summary>Grand total including tax.</summary>
    public Money GrandTotal => _items.Aggregate(Money.Zero, (acc, i) => acc + i.LineTotal);

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a new Sale in Draft status.
    /// </summary>
    public static Result<Sale> Create(string? reference = null, string? notes = null)
    {
        if (reference is not null && reference.Length > 100)
            return Result.Failure<Sale>(Error.Validation(
                "Sales.Sale.ReferenceTooLong",
                "Sale reference cannot exceed 100 characters."));

        if (notes is not null && notes.Length > 500)
            return Result.Failure<Sale>(Error.Validation(
                "Sales.Sale.NotesTooLong",
                "Sale notes cannot exceed 500 characters."));

        var now = DateTime.UtcNow;
        var sale = new Sale
        {
            Id = SaleId.New(),
            Status = SaleStatus.Draft,
            Reference = reference?.Trim(),
            Notes = notes?.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };

        sale._domainEvents.Add(new SaleCreatedEvent(sale.Id, now));
        return Result.Success(sale);
    }

    /// <summary>Records the customer of a Draft sale (FIX-11): ID plus a code/name snapshot.</summary>
    public Result AssignCustomer(Guid customerId, string code, string name)
    {
        if (Status != SaleStatus.Draft)
            return Result.Failure(Error.Conflict("Sales.Sale.NotDraft", "The customer can only be set on a draft sale."));
        if (customerId == Guid.Empty || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return Result.Failure(Error.Validation("Sales.Sale.CustomerInvalid", "A customer needs an ID, a code and a name."));
        if (code.Trim().Length > 30 || name.Trim().Length > 200)
            return Result.Failure(Error.Validation("Sales.Sale.CustomerTooLong", "The customer code (30) or name (200) is too long."));

        CustomerId = customerId;
        CustomerCode = code.Trim();
        CustomerName = name.Trim();
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    // -----------------------------------------------------------------------
    // Behaviour — Item management (Draft only)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Adds a line item to the sale. Only permitted in Draft status.
    /// UnitPrice, Discount, and TaxRate are snapshotted at call time.
    /// </summary>
    public Result<SaleItem> AddItem(
        Guid catalogProductId,
        string productName,
        string productSku,
        SaleQuantity quantity,
        Money unitPrice,
        Money discount,
        decimal taxRate)
    {
        if (Status != SaleStatus.Draft)
            return Result.Failure<SaleItem>(Error.Conflict(
                "Sales.Sale.NotDraft",
                $"Items can only be added to a Sale in Draft status. Current status: {Status}."));

        var itemResult = SaleItem.Create(
            Id, catalogProductId, productName, productSku,
            quantity, unitPrice, discount, taxRate);

        if (itemResult.IsFailure)
            return Result.Failure<SaleItem>(itemResult.Error);

        _items.Add(itemResult.Value);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success(itemResult.Value);
    }

    /// <summary>
    /// Removes a line item from the sale. Only permitted in Draft status.
    /// </summary>
    public Result RemoveItem(SaleItemId saleItemId)
    {
        if (Status != SaleStatus.Draft)
            return Result.Failure(Error.Conflict(
                "Sales.Sale.NotDraft",
                $"Items can only be removed from a Sale in Draft status. Current status: {Status}."));

        var item = _items.FirstOrDefault(i => i.Id == saleItemId);
        if (item is null)
            return Result.Failure(Error.NotFound(
                "Sales.Sale.ItemNotFound",
                $"SaleItem '{saleItemId}' was not found in this sale."));

        _items.Remove(item);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    // -----------------------------------------------------------------------
    // Behaviour — Status transitions
    // -----------------------------------------------------------------------

    /// <summary>
    /// Confirms the sale. Transitions from Draft to Confirmed.
    /// A Confirmed sale cannot have items added or removed.
    /// </summary>
    public Result Confirm()
    {
        if (Status != SaleStatus.Draft)
            return Result.Failure(Error.Conflict(
                "Sales.Sale.CannotConfirm",
                $"Only a Draft sale can be confirmed. Current status: {Status}."));

        if (_items.Count == 0)
            return Result.Failure(Error.Validation(
                "Sales.Sale.NoItems",
                "A sale must have at least one item before it can be confirmed."));

        Status = SaleStatus.Confirmed;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    /// Completes the sale. Transitions from Confirmed to Completed.
    /// After completion, the sale and its items are frozen (historical record).
    /// </summary>
    public Result Complete()
    {
        if (Status != SaleStatus.Confirmed)
            return Result.Failure(Error.Conflict(
                "Sales.Sale.CannotComplete",
                $"Only a Confirmed sale can be completed. Current status: {Status}."));

        if (_items.Count == 0)
            return Result.Failure(Error.Validation(
                "Sales.Sale.NoItems",
                "A sale must have at least one item to be completed."));

        var now = DateTime.UtcNow;
        Status = SaleStatus.Completed;
        CompletedAt = now;
        UpdatedAt = now;

        _domainEvents.Add(new SaleCompletedEvent(Id, GrandTotal.Amount, now));
        return Result.Success();
    }

    /// <summary>
    /// Cancels the sale. Valid from Draft or Confirmed status only.
    /// Cancelled sales are terminal — no further mutations.
    /// </summary>
    public Result Cancel(string reason)
    {
        if (Status == SaleStatus.Completed)
            return Result.Failure(Error.Conflict(
                "Sales.Sale.AlreadyCompleted",
                "A completed sale cannot be cancelled."));

        if (Status == SaleStatus.Cancelled)
            return Result.Failure(Error.Conflict(
                "Sales.Sale.AlreadyCancelled",
                "Sale is already cancelled."));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(
                "Sales.Sale.CancellationReasonRequired",
                "A reason must be provided when cancelling a sale."));

        if (reason.Length > 500)
            return Result.Failure(Error.Validation(
                "Sales.Sale.CancellationReasonTooLong",
                "Cancellation reason cannot exceed 500 characters."));

        var now = DateTime.UtcNow;
        Status = SaleStatus.Cancelled;
        CancellationReason = reason.Trim();
        CancelledAt = now;
        UpdatedAt = now;

        _domainEvents.Add(new SaleCancelledEvent(Id, reason.Trim(), now));
        return Result.Success();
    }
}
