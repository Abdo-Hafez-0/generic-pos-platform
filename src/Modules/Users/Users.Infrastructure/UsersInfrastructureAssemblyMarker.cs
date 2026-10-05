using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Users.Tests")]

namespace Users.Infrastructure;

/// <summary>
/// Assembly marker for Users.Infrastructure.
/// Used by Architecture.Tests and migration tooling to locate this assembly.
/// Users.Tests accesses internal types via InternalsVisibleTo for integration testing.
/// </summary>
public sealed class UsersInfrastructureAssemblyMarker;
