namespace Catalog.Domain.Enums;

/// <summary>
/// Describes the operational status of a product in the catalog.
/// </summary>
public enum ProductStatus
{
    /// <summary>Product is active and available for sale.</summary>
    Active = 0,

    /// <summary>Product is temporarily inactive (hidden from sale, not discontinued).</summary>
    Inactive = 1,

    /// <summary>Product is permanently discontinued and will not be restocked.</summary>
    Discontinued = 2
}
