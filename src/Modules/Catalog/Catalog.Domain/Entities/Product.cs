using Platform.Core.Results;
using Catalog.Domain.ValueObjects;
using Catalog.Domain.Enums;
using Catalog.Domain.Events;

namespace Catalog.Domain.Entities;

/// <summary>
/// The Product aggregate root — the central concept owned by the Catalog module.
///
/// Catalog owns the definition of what the business sells or stores.
/// It answers: "What is this product?"
///
/// Catalog does NOT own: Stock, StockMovement, Warehouse, Sale, Payment.
/// Those belong to their respective modules (Inventory, Sales, etc.).
///
/// Cross-module access to product information goes through Catalog.Contracts:
///   IProductLookup → ProductLookupResult
///   IProductBarcodeResolver → ProductLookupResult
///
/// Architecture reference: Module Map §8, §9, §10.
/// </summary>
public sealed class Product
{
    private readonly List<Barcode> _barcodes = [];
    private readonly List<object> _domainEvents = [];

    private Product() { }

    public ProductId Id { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public CategoryId CategoryId { get; private set; }
    public UnitId UnitId { get; private set; }
    public ProductStatus Status { get; private set; }
    public decimal SalePrice { get; private set; }
    public decimal? CostPrice { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime UpdatedAt { get; private set; }

    /// <summary>The barcodes assigned to this product (read-only collection).</summary>
    public IReadOnlyList<Barcode> Barcodes => _barcodes.AsReadOnly();

    /// <summary>Domain events raised during this aggregate's lifetime.</summary>
    public IReadOnlyList<object> DomainEvents => _domainEvents.AsReadOnly();

    /// <summary>Clears collected domain events after they have been dispatched.</summary>
    public void ClearDomainEvents() => _domainEvents.Clear();

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>
    /// Creates a new Product aggregate.
    /// All business invariants are enforced here — the aggregate is always in a valid state.
    /// </summary>
    public static Result<Product> Create(
        string sku,
        string name,
        CategoryId categoryId,
        UnitId unitId,
        decimal salePrice,
        decimal? costPrice = null,
        string? description = null)
    {
        var validationError = ValidateCore(sku, name, salePrice, costPrice);
        if (validationError is not null)
            return Result.Failure<Product>(validationError);

        if (categoryId == CategoryId.Empty)
            return Result.Failure<Product>(
                Error.Validation("Catalog.Product.CategoryRequired", "A valid category must be assigned."));

        if (unitId == UnitId.Empty)
            return Result.Failure<Product>(
                Error.Validation("Catalog.Product.UnitRequired", "A valid unit must be assigned."));

        var now = DateTime.UtcNow;
        var product = new Product
        {
            Id = ProductId.New(),
            Sku = sku.Trim().ToUpperInvariant(),
            Name = name.Trim(),
            Description = description?.Trim(),
            CategoryId = categoryId,
            UnitId = unitId,
            SalePrice = salePrice,
            CostPrice = costPrice,
            Status = ProductStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

        product._domainEvents.Add(new ProductCreatedEvent(product.Id, product.Sku, product.Name));
        return Result.Success(product);
    }

    // -----------------------------------------------------------------------
    // Behaviour
    // -----------------------------------------------------------------------

    /// <summary>Updates the product's editable fields. SKU is immutable after creation.</summary>
    public Result Update(
        string name,
        CategoryId categoryId,
        UnitId unitId,
        decimal salePrice,
        decimal? costPrice = null,
        string? description = null)
    {
        var validationError = ValidateCore(Sku, name, salePrice, costPrice);
        if (validationError is not null)
            return Result.Failure(validationError);

        if (categoryId == CategoryId.Empty)
            return Result.Failure(Error.Validation("Catalog.Product.CategoryRequired", "A valid category must be assigned."));

        if (unitId == UnitId.Empty)
            return Result.Failure(Error.Validation("Catalog.Product.UnitRequired", "A valid unit must be assigned."));

        Name = name.Trim();
        Description = description?.Trim();
        CategoryId = categoryId;
        UnitId = unitId;
        SalePrice = salePrice;
        CostPrice = costPrice;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>Deactivates the product (hides from sale but does not discontinue).</summary>
    public Result Deactivate()
    {
        if (Status == ProductStatus.Discontinued)
            return Result.Failure(Error.Conflict("Catalog.Product.AlreadyDiscontinued",
                "Cannot deactivate a discontinued product."));

        var previousStatus = Status;
        Status = ProductStatus.Inactive;
        UpdatedAt = DateTime.UtcNow;
        _domainEvents.Add(new ProductStatusChangedEvent(Id, previousStatus, Status));
        return Result.Success();
    }

    /// <summary>Reactivates an inactive product.</summary>
    public Result Activate()
    {
        if (Status == ProductStatus.Discontinued)
            return Result.Failure(Error.Conflict("Catalog.Product.AlreadyDiscontinued",
                "Cannot activate a discontinued product."));

        var previousStatus = Status;
        Status = ProductStatus.Active;
        UpdatedAt = DateTime.UtcNow;
        _domainEvents.Add(new ProductStatusChangedEvent(Id, previousStatus, Status));
        return Result.Success();
    }

    /// <summary>Permanently discontinues the product. This cannot be undone.</summary>
    public void Discontinue()
    {
        var previousStatus = Status;
        Status = ProductStatus.Discontinued;
        UpdatedAt = DateTime.UtcNow;
        _domainEvents.Add(new ProductStatusChangedEvent(Id, previousStatus, Status));
    }

    /// <summary>Assigns a barcode to this product.</summary>
    public Result<Barcode> AssignBarcode(string value, BarcodeFormat format)
    {
        // Guard: duplicate barcode value
        if (_barcodes.Any(b => b.Value.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<Barcode>(
                Error.Conflict("Catalog.Barcode.Duplicate", $"Barcode '{value}' is already assigned to this product."));

        var barcodeResult = Barcode.Create(Id, value, format);
        if (barcodeResult.IsFailure)
            return Result.Failure<Barcode>(barcodeResult.Error);

        _barcodes.Add(barcodeResult.Value);
        UpdatedAt = DateTime.UtcNow;
        return Result.Success(barcodeResult.Value);
    }

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static Error? ValidateCore(string sku, string name, decimal salePrice, decimal? costPrice)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return Error.Validation("Catalog.Product.SkuEmpty", "Product SKU cannot be empty.");

        if (sku.Length > 50)
            return Error.Validation("Catalog.Product.SkuTooLong", "Product SKU cannot exceed 50 characters.");

        if (string.IsNullOrWhiteSpace(name))
            return Error.Validation("Catalog.Product.NameEmpty", "Product name cannot be empty.");

        if (name.Length > 200)
            return Error.Validation("Catalog.Product.NameTooLong", "Product name cannot exceed 200 characters.");

        if (salePrice < 0)
            return Error.Validation("Catalog.Product.NegativeSalePrice", "Product sale price cannot be negative.");

        if (costPrice.HasValue && costPrice.Value < 0)
            return Error.Validation("Catalog.Product.NegativeCostPrice", "Product cost price cannot be negative.");

        return null;
    }
}
