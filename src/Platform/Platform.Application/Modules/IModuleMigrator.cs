using Platform.Core.Modules;

namespace Platform.Application.Modules;

/// <summary>Outcome of a module-owned schema migration.</summary>
/// <param name="Succeeded">True if the module's schema is now at the requested version.</param>
/// <param name="DatabaseModified">True if anything in the module's tables may have changed (conservative on failure).</param>
public sealed record ModuleMigrationResult(bool Succeeded, bool DatabaseModified, string? Message);

/// <summary>
/// Contract for a module to run ITS OWN schema migrations on request (e.g. by the update system). The implementation
/// belongs to the module and may touch only that module's tables. No business module is required to implement it yet;
/// without one the update system defers migration to the module's own startup initializer.
/// </summary>
public interface IModuleMigrator
{
    ModuleId ModuleId { get; }

    Task<ModuleMigrationResult> MigrateAsync(int targetSchemaVersion, CancellationToken cancellationToken = default);
}
