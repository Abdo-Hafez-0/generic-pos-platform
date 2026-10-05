using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Customers.Tests")]

namespace Customers.Infrastructure;

/// <summary>
/// Assembly marker for Customers.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Customers.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class CustomersInfrastructureAssemblyMarker;
