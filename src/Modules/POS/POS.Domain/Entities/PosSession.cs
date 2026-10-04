using Platform.Core.Results;
using POS.Domain.Enums;
using POS.Domain.ValueObjects;

namespace POS.Domain.Entities;

/// <summary>
/// An active cashier session at a POS terminal.
///
/// The session records who is selling and which warehouse stock is checked against.
/// WarehouseId is a plain Guid reference to an Inventory warehouse (no cross-module navigation).
/// </summary>
public sealed class PosSession
{
    private PosSession() { }

    public PosSessionId Id { get; private set; }

    /// <summary>Identifies the cashier (free-text reference until a Users module exists).</summary>
    public string CashierReference { get; private set; } = string.Empty;

    /// <summary>Inventory warehouse used for stock validation and issue. Reference by ID only.</summary>
    public Guid WarehouseId { get; private set; }

    public PosSessionStatus Status { get; private set; }
    public DateTime OpenedAt { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    public static Result<PosSession> Open(string cashierReference, Guid warehouseId)
    {
        if (string.IsNullOrWhiteSpace(cashierReference))
            return Result.Failure<PosSession>(Error.Validation(
                "POS.Session.CashierRequired",
                "A cashier reference is required to open a POS session."));

        if (cashierReference.Trim().Length > 100)
            return Result.Failure<PosSession>(Error.Validation(
                "POS.Session.CashierTooLong",
                "Cashier reference cannot exceed 100 characters."));

        if (warehouseId == Guid.Empty)
            return Result.Failure<PosSession>(Error.Validation(
                "POS.Session.WarehouseRequired",
                "A warehouse must be specified to open a POS session."));

        return Result.Success(new PosSession
        {
            Id = PosSessionId.New(),
            CashierReference = cashierReference.Trim(),
            WarehouseId = warehouseId,
            Status = PosSessionStatus.Open,
            OpenedAt = DateTime.UtcNow
        });
    }

    public Result Close()
    {
        if (Status != PosSessionStatus.Open)
            return Result.Failure(Error.Conflict(
                "POS.Session.AlreadyClosed",
                "The POS session is already closed."));

        Status = PosSessionStatus.Closed;
        ClosedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
