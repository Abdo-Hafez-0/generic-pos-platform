using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("POS.Tests")]

namespace POS.Infrastructure;

/// <summary>
/// Assembly marker for POS.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
///
/// POS.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class POSInfrastructureAssemblyMarker;
