using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Purchasing.Tests")]

namespace Purchasing.Infrastructure;

/// <summary>
/// Assembly marker for Purchasing.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Purchasing.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class PurchasingInfrastructureAssemblyMarker;
