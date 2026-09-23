using Platform.Core.Modules;

namespace Catalog.Infrastructure.Module;

/// <summary>
/// The Catalog module's manifest — describes its identity, version, and capabilities.
///
/// Implements IModuleManifest from Platform.Core.
/// This is the runtime manifest (not the package manifest — hash/signature are Stage 7).
///
/// Architecture reference: Architecture §38 (Module Manifest), §42 (Feature vs Module).
/// </summary>
public sealed class CatalogModuleManifest : IModuleManifest
{
    public static readonly CatalogModuleManifest Instance = new();

    private CatalogModuleManifest() { }

    public ModuleId ModuleId => new("catalog");
    public string Name => "Catalog";
    public ModuleVersion Version => new(1, 0, 0);
    public string Publisher => "GenericPOS Platform";
    public ModuleVersion MinimumPlatformVersion => new(1, 0, 0);
    public ModuleVersion? MaximumPlatformVersion => null;

    public IReadOnlyList<ModuleDependency> Dependencies => [];

    public IReadOnlyList<IFeatureDescriptor> ProvidedFeatures =>
    [
        new CatalogFeatureDescriptor(new FeatureId("catalog.products"), "Product Catalog"),
        new CatalogFeatureDescriptor(new FeatureId("catalog.categories"), "Product Categories"),
        new CatalogFeatureDescriptor(new FeatureId("catalog.barcodes"), "Barcode Management")
    ];

    public int DatabaseSchemaVersion => 1;

    private sealed record CatalogFeatureDescriptor(FeatureId Id, string DisplayName) : IFeatureDescriptor;
}
