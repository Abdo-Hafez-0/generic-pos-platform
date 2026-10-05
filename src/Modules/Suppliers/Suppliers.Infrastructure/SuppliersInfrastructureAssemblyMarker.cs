using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Suppliers.Tests")]

namespace Suppliers.Infrastructure;

/// <summary>
/// Assembly marker for Suppliers.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Suppliers.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class SuppliersInfrastructureAssemblyMarker;
