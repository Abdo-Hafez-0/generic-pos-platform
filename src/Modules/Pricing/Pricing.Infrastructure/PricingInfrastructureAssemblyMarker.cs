using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Pricing.Tests")]

namespace Pricing.Infrastructure;

/// <summary>
/// Assembly marker for Pricing.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Pricing.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class PricingInfrastructureAssemblyMarker;
