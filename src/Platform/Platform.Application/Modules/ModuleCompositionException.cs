using Platform.Core.Modules;

namespace Platform.Application.Modules;

/// <summary>
/// The installed modules cannot run together: a module needs another module that is missing or has an unsupported version, the
/// dependencies form a cycle, or a module failed to initialize or start. The application does not start half-composed: a module whose
/// dependency is absent would otherwise fail on first use, in front of the user.
///
/// <see cref="Exception.Message"/> holds module IDs and versions only (no paths, no exception text), so it is safe to show; the original
/// failure, if any, is the <see cref="Exception.InnerException"/> and goes to the log.
/// </summary>
public sealed class ModuleCompositionException : Exception
{
    public ModuleCompositionException(IReadOnlyList<ModuleId> modules, IReadOnlyList<string> problems, Exception? inner = null)
        : base(string.Join(" ", problems), inner)
    {
        Modules = modules;
        Problems = problems;
    }

    /// <summary>The modules that cannot run (empty when the problem is the graph as a whole, e.g. a cycle).</summary>
    public IReadOnlyList<ModuleId> Modules { get; }

    /// <summary>One plain sentence per problem.</summary>
    public IReadOnlyList<string> Problems { get; }
}
