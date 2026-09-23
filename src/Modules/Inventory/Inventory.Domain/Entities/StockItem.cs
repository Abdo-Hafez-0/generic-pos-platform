using Platform.Core.Results;
using Inventory.Domain.ValueObjects;

namespace Inventory.Domain.Entities;

/// <summary>
/// The StockItem aggregate root.
///
/// Links a Catalog product (by its stable Guid identity) to a Warehouse/Location pair.
/// This is the unit of tracking: "product X in warehouse Y at location Z".
///
/// DESIGN DECISION:
///   CatalogProductId is stored as Guid — NOT as Catalog.Domain.ValueObjects.ProductId.
///   Inventory.Domain must not reference Catalog.Domain.
///   The Guid is the stable identity crossing the module boundary through Catalog.Contracts.
///
/// INVARIANTS:
/// - CatalogProductId cannot be Guid.Empty.
/// - WarehouseId cannot be empty.
/// - LocationId is optional (null = warehouse-level tracking, no bin assignment).
/// - A deactivated StockItem must not receive new stock movements.
///
/// Architecture: Inventory.Domain — no EF Core, no WPF, no HTTP.
/// </summary>
public sealed class StockItem
{
    private readonly List<object> _domainEvents = [];

    private StockItem() { }

    public StockItemId Id { get; private set; }

    /// <summary>
    /// The Catalog product this stock item represents.
    /// Stored as Guid — Inventory does NOT reference Catalog.Domain.ValueObjects.ProductId.
    /// Validated through Catalog.Contracts at the Application layer.
    /// </summary>
    public Guid CatalogProductId { get; private set; }

    public WarehouseId WarehouseId { get; private set; }

    /// <summary>Optional location within the warehouse. Null means warehouse-level tracking.</summary>
    public LocationId? LocationId { get; private set; }

    public bool IsActive { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Domain events raised during this aggregate's lifetime.</summary>
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Clears collected domain events after dispatch.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a new StockItem.
    /// The catalogProductId should be validated against Catalog.Contracts before calling Create().
    /// </summary>
    public static Result<StockItem> Create(
        Guid catalogProductId,
        WarehouseId warehouseId,
        LocationId? locationId = null)
    {
        if (catalogProductId == Guid.Empty)
            return Result.Failure<StockItem>(Error.Validation(
                "Inventory.StockItem.ProductRequired",
                "A valid Catalog product ID must be provided."));

        if (warehouseId == WarehouseId.Empty)
            return Result.Failure<StockItem>(Error.Validation(
                "Inventory.StockItem.WarehouseRequired",
                "A valid warehouse must be assigned."));

        var now = DateTime.UtcNow;
        return Result.Success(new StockItem
        {
            Id = StockItemId.New(),
            CatalogProductId = catalogProductId,
            WarehouseId = warehouseId,
            LocationId = locationId,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        });
    }

    // -----------------------------------------------------------------------
    // Behaviour
    // -----------------------------------------------------------------------

    /// <summary>Deactivates this stock item. No new movements can be recorded against it.</summary>
    public Result Deactivate()
    {
        if (!IsActive)
            return Result.Failure(Error.Conflict(
                "Inventory.StockItem.AlreadyInactive", "Stock item is already inactive."));

        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Reactivates a deactivated stock item.</summary>
    public Result Activate()
    {
        if (IsActive)
            return Result.Failure(Error.Conflict(
                "Inventory.StockItem.AlreadyActive", "Stock item is already active."));

        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Assigns or changes the location within the same warehouse.</summary>
    public Result AssignLocation(LocationId? locationId)
    {
        LocationId = locationId;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
