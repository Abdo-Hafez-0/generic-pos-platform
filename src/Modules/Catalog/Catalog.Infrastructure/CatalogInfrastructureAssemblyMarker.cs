using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Catalog.Tests")]

namespace Catalog.Infrastructure;

/// <summary>
/// Marker class that anchors the Catalog.Infrastructure assembly boundary.
///
/// IMPORTANT — Catalog.Infrastructure owns:
/// - CatalogDbContext (EF Core, SQLite)
/// - Repositories (EfProductRepository, etc.) — internal to this assembly
/// - Unit of Work (CatalogUnitOfWork) — internal to this assembly
/// - Contract implementations (CatalogProductLookup, CatalogBarcodeResolver)
/// - Migration hosting (CatalogDatabaseInitializer)
/// - Module hosting (CatalogHostingModule, CatalogModule)
///
/// External references:
///   - Client.Host references this for IHostingModule (CatalogHostingModule).
///   - Catalog.Tests accesses internal types via InternalsVisibleTo for integration testing.
///
/// Other modules access Catalog through Catalog.Contracts only.
/// </summary>
public static class CatalogInfrastructureAssemblyMarker
{
    // Intentionally empty.
    // Used by architecture tests to locate this assembly.
}
