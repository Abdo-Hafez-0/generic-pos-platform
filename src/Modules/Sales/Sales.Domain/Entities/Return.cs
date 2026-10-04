using Platform.Core.Results;
using Sales.Domain.Enums;
using Sales.Domain.ValueObjects;

namespace Sales.Domain.Entities;

/// <summary>
/// The Return aggregate — represents a customer return against a completed Sale.
///
/// A Return tracks which items are being returned and in what quantity.
/// It references the original Sale to maintain traceability.
///
/// INVARIANTS:
/// - Must reference a valid completed Sale.
/// - Must have at least one ReturnItem.
/// - Items can only be added in Pending status.
/// - Processed/Rejected returns are terminal.
///
/// Architecture: Sales.Domain — no EF Core, no WPF, no HTTP.
/// Architecture reference: Module Map §17 (Sales Owns: Return, ReturnItem).
/// </summary>
public sealed class Return
{
    private readonly List<ReturnItem> _items = [];

    private Return() { }

    public ReturnId Id { get; private set; }

    /// <summary>The Sale this return is against. Must be a Completed sale.</summary>
    public SaleId OriginalSaleId { get; private set; }

    public ReturnStatus Status { get; private set; }

    public string? Notes { get; private set; }

    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }
    public DateTime? ProcessedAt { get; private set; }

    /// <summary>Read-only view of return line items.</summary>
    public IReadOnlyList<ReturnItem> Items => _items.AsReadOnly();

    /// <summary>Total refund amount (sum of all item line totals).</summary>
    public Money TotalRefundAmount => _items.Aggregate(
        Money.Zero,
        (acc, i) => acc + i.UnitPrice * i.Quantity.Value);

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    public static Result<Return> Create(SaleId originalSaleId, string? notes = null)
    {
        if (originalSaleId == SaleId.Empty)
            return Result.Failure<Return>(Error.Validation(
                "Sales.Return.SaleRequired",
                "A return must reference a valid original Sale."));

        if (notes is not null && notes.Length > 500)
            return Result.Failure<Return>(Error.Validation(
                "Sales.Return.NotesTooLong",
                "Return notes cannot exceed 500 characters."));

        var now = DateTime.UtcNow;
        return Result.Success(new Return
        {
            Id = ReturnId.New(),
            OriginalSaleId = originalSaleId,
            Status = ReturnStatus.Pending,
            Notes = notes?.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    // -----------------------------------------------------------------------
    // Behaviour
    // -----------------------------------------------------------------------

    /// <summary>Adds a return line item. Only permitted in Pending status.</summary>
    public Result<ReturnItem> AddItem(
        SaleItemId originalSaleItemId,
        Guid catalogProductId,
        string productName,
        SaleQuantity quantity,
        Money unitPrice,
        string? reason = null)
    {
        if (Status != ReturnStatus.Pending)
            return Result.Failure<ReturnItem>(Error.Conflict(
                "Sales.Return.NotPending",
                $"Items can only be added to a Pending return. Current status: {Status}."));

        var itemResult = ReturnItem.Create(
            Id, originalSaleItemId, catalogProductId,
            productName, quantity, unitPrice, reason);

        if (itemResult.IsFailure)
            return Result.Failure<ReturnItem>(itemResult.Error);

        _items.Add(itemResult.Value);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success(itemResult.Value);
    }

    /// <summary>Marks the return as processed. Terminal state.</summary>
    public Result Process()
    {
        if (Status != ReturnStatus.Pending)
            return Result.Failure(Error.Conflict(
                "Sales.Return.CannotProcess",
                $"Only a Pending return can be processed. Current status: {Status}."));

        if (_items.Count == 0)
            return Result.Failure(Error.Validation(
                "Sales.Return.NoItems",
                "A return must have at least one item to be processed."));

        var now = DateTime.UtcNow;
        Status = ReturnStatus.Processed;
        ProcessedAt = now;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Rejects the return. Terminal state.</summary>
    public Result Reject(string reason)
    {
        if (Status != ReturnStatus.Pending)
            return Result.Failure(Error.Conflict(
                "Sales.Return.CannotReject",
                $"Only a Pending return can be rejected. Current status: {Status}."));

        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(
                "Sales.Return.RejectionReasonRequired",
                "A reason must be provided when rejecting a return."));

        Status = ReturnStatus.Rejected;
        Notes = (Notes is null ? "" : Notes + " | ") + $"Rejected: {reason.Trim()}";
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
