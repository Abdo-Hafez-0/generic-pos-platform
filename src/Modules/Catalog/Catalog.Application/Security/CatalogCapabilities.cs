using Platform.Application.Abstractions.Authorization;

namespace Catalog.Application.Security;

/// <summary>The capabilities the Catalog module's operations check (declared to the platform catalog by <see cref="CatalogCapabilityProvider"/>).</summary>
public static class CatalogCapabilities
{
    public const string Module = "catalog";

        public const string CreateProduct = "catalog.product.create";
        public const string EditProduct = "catalog.product.edit";
        public const string DeactivateProduct = "catalog.product.deactivate";
        public const string ManageCategories = "catalog.category.manage";
        public const string ManageUnits = "catalog.unit.manage";

    public static IReadOnlyList<CapabilityDescriptor> All { get; } =
    [
            new(CreateProduct, Module, "Create products", "Add products to the catalog.", LicenseRequirement.Module),
            new(EditProduct, Module, "Edit products", "Change product details, prices and barcodes.", LicenseRequirement.Module),
            new(DeactivateProduct, Module, "Deactivate products", "Take products out of sale.", LicenseRequirement.Module),
            new(ManageCategories, Module, "Manage categories", "Create product categories.", LicenseRequirement.Module),
            new(ManageUnits, Module, "Manage units", "Create units of measure.", LicenseRequirement.Module)
    ];
}

public sealed class CatalogCapabilityProvider : ICapabilityProvider
{
    public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => CatalogCapabilities.All;
}
