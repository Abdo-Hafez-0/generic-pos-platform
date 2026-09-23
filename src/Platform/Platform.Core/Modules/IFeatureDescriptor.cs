namespace Platform.Core.Modules;

/// <summary>
/// Describes a feature provided by a module.
///
/// The architecture (§42) distinguishes modules from features:
///   - A module is a technical unit.
///   - A feature is a commercial/capability unit.
///   - One module can expose multiple features.
///
/// Example (§42): The Accounting module exposes features such as:
///   - accounting.general-ledger
///   - accounting.accounts-payable
///   - accounting.accounts-receivable
///   - accounting.financial-reports
///
/// Modules declare their provided features through their manifest.
/// The licensing stage (Stage 6) will use these FeatureIds to determine
/// what a customer's license entitles them to use.
///
/// This is intentionally a minimal read-only descriptor.
/// It does NOT contain license state, activation state, or entitlement data —
/// those belong to Stage 6 licensing contracts.
/// </summary>
public interface IFeatureDescriptor
{
    /// <summary>
    /// Gets the unique identifier of this feature.
    /// Convention: "{module-id}.{feature-name}" (e.g., "accounting.general-ledger").
    /// </summary>
    FeatureId Id { get; }

    /// <summary>
    /// Gets a human-readable display name for this feature.
    /// Used for administration, licensing UI, and diagnostics.
    /// </summary>
    string DisplayName { get; }
}
