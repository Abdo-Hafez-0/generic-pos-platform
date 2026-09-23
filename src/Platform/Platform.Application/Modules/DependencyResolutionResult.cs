using Platform.Core.Modules;

namespace Platform.Application.Modules;

/// <summary>
/// Represents the outcome of a module dependency resolution attempt.
///
/// A successful result provides the topological activation order — the sequence
/// in which modules must be initialized and started so that dependencies are
/// always started before the modules that depend on them.
///
/// A failed result provides a list of human-readable error messages describing
/// each problem found in the dependency graph.
/// </summary>
public sealed class DependencyResolutionResult
{
    /// <summary>Gets whether dependency resolution succeeded without errors.</summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets the recommended module activation order when resolution succeeds.
    /// Modules earlier in this list have no unresolved dependencies on modules
    /// later in the list (topological order).
    /// Empty when <see cref="IsSuccess"/> is false.
    /// </summary>
    public IReadOnlyList<ModuleId> ActivationOrder { get; }

    /// <summary>
    /// Gets the list of error messages when resolution fails.
    /// Empty when <see cref="IsSuccess"/> is true.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    private DependencyResolutionResult(
        bool isSuccess,
        IReadOnlyList<ModuleId> activationOrder,
        IReadOnlyList<string> errors)
    {
        IsSuccess = isSuccess;
        ActivationOrder = activationOrder;
        Errors = errors;
    }

    /// <summary>Creates a successful resolution result with the given activation order.</summary>
    public static DependencyResolutionResult Success(IReadOnlyList<ModuleId> activationOrder) =>
        new(true, activationOrder, []);

    /// <summary>Creates a failed resolution result with the given error messages.</summary>
    public static DependencyResolutionResult Failure(IReadOnlyList<string> errors) =>
        new(false, [], errors);

    /// <summary>Creates a failed resolution result with a single error message.</summary>
    public static DependencyResolutionResult Failure(string error) =>
        Failure([error]);

    /// <inheritdoc />
    public override string ToString() =>
        IsSuccess
            ? $"Resolution succeeded. Activation order: [{string.Join(", ", ActivationOrder)}]"
            : $"Resolution failed. Errors: [{string.Join("; ", Errors)}]";
}
