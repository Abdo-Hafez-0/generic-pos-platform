namespace Catalog.Contracts.Models;

/// <summary>
/// Contract-level representation of product status.
/// Mirrors Catalog.Domain.Enums.ProductStatus but is completely independent of domain.
/// Other modules use this enum without referencing Catalog.Domain.
/// </summary>
public enum ProductStatusContract
{
    Active = 0,
    Inactive = 1,
    Discontinued = 2
}
