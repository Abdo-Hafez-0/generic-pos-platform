using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Fiscal.Tests")]

namespace Fiscal.Infrastructure;

/// <summary>
/// Assembly marker for Fiscal.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Fiscal.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class FiscalInfrastructureAssemblyMarker;
