using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Payments.Tests")]

namespace Payments.Infrastructure;

/// <summary>
/// Assembly marker for Payments.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Payments.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class PaymentsInfrastructureAssemblyMarker;
