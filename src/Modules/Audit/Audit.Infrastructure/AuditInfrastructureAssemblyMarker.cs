using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Audit.Tests")]

namespace Audit.Infrastructure;

/// <summary>
/// Assembly marker for Audit.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Audit.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class AuditInfrastructureAssemblyMarker;
