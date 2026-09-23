using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Platform.Infrastructure.Tests")]

namespace Platform.Infrastructure;

/// <summary>
/// Marker class that anchors the Platform.Infrastructure assembly boundary.
///
/// IMPORTANT — Stage 1 constraint (updated in Stage 3):
/// Platform.Infrastructure owns EF Core, SQLite, DbContext, and all persistence implementations.
/// It is referenced only by:
///   - Platform.Infrastructure.Tests (via InternalsVisibleTo, for testing internal types)
///   - Client.Host (the composition root, for DI registration only)
///
/// All other layers depend on Platform.Application abstractions (IUnitOfWork, etc.), not this assembly.
/// </summary>
public static class PlatformInfrastructureAssemblyMarker
{
    // Intentionally empty.
    // Used by architecture tests to locate this assembly.
}
