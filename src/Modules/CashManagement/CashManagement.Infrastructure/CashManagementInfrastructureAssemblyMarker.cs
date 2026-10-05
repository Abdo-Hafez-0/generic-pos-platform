using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("CashManagement.Tests")]

namespace CashManagement.Infrastructure;

/// <summary>
/// Assembly marker for CashManagement.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// CashManagement.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class CashManagementInfrastructureAssemblyMarker;
