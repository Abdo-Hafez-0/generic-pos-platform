using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Reporting.Tests")]

namespace Reporting.Infrastructure;

/// <summary>
/// Assembly marker for Reporting.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Reporting.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class ReportingInfrastructureAssemblyMarker;
