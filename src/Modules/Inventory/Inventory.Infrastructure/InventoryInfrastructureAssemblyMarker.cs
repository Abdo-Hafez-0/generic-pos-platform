using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Inventory.Tests")]

namespace Inventory.Infrastructure;

/// <summary>
/// Assembly marker for Inventory.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
///
/// Inventory.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class InventoryInfrastructureAssemblyMarker;

