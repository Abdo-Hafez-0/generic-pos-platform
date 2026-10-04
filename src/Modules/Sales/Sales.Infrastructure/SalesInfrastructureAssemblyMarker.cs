using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Sales.Tests")]

namespace Sales.Infrastructure;

/// <summary>
/// Assembly marker for Sales.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
///
/// Sales.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class SalesInfrastructureAssemblyMarker;
