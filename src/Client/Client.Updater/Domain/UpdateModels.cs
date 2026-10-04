using Platform.Core.Modules;
using Updates.Contracts;

namespace Client.Updater.Domain;

/// <summary>
/// The package/update lifecycle. Deliberately separate from IModule runtime status and from LicenseState.
/// Terminal states: Confirmed, VerificationFailed, MigrationFailed, ActivationFailed, Failed, RolledBack.
/// RecoveryRequired needs an explicit decision (see UpdateService.RollbackAsync / restoring data).
/// </summary>
public enum UpdateState
{
    Discovered = 1,
    Downloaded = 2,
    Verified = 3,
    Staged = 4,
    MigrationPending = 5,
    Migrating = 6,
    ReadyToActivate = 7,

    /// <summary>The active-version pointer was switched. Not yet confirmed healthy by a successful start.</summary>
    Activated = 8,

    /// <summary>The new version started healthy; the update is final.</summary>
    Confirmed = 9,

    VerificationFailed = 10,
    MigrationFailed = 11,
    ActivationFailed = 12,

    /// <summary>The database may have been changed and the old binaries may not be compatible: needs an explicit decision.</summary>
    RecoveryRequired = 13,

    RolledBack = 14,

    /// <summary>Interrupted or failed before activation; the known-good installation was kept.</summary>
    Failed = 15
}

public sealed record UpdateStateEntry(UpdateState State, DateTimeOffset At, string? Message);

/// <summary>The persisted record of one update attempt. It lives on disk (not in the business database) so interrupted updates can be recovered.</summary>
public sealed class UpdateJournal
{
    public Guid PackageId { get; set; }
    public PackageType PackageType { get; set; }
    public string TargetId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;

    /// <summary>The version that was active before this update (null = the built-in baseline).</summary>
    public string? PreviousVersion { get; set; }

    public bool MigrationRequired { get; set; }

    /// <summary>True when the previous binaries still run on the migrated schema (or there was no migration).</summary>
    public bool OldBinaryCompatible { get; set; } = true;

    public string? RestorePointPath { get; set; }
    public int StartupAttempts { get; set; }
    public UpdateState State { get; set; }
    public List<UpdateStateEntry> History { get; set; } = [];

    /// <summary>True when binaries can be rolled back without leaving the schema incompatible.</summary>
    public bool BinaryRollbackSafe => !MigrationRequired || OldBinaryCompatible;
}

/// <summary>What is currently active for a target: written atomically; the single source of truth for "which version runs".</summary>
public sealed class ActivePointer
{
    public string Version { get; set; } = string.Empty;
    public string? PreviousVersion { get; set; }
}

/// <summary>An installed module as the updater sees it.</summary>
public sealed record InstalledModule(
    ModuleId Id,
    ModuleVersion Version,
    IReadOnlyList<ModuleDependency> Dependencies,
    ModuleVersion MinimumPlatformVersion,
    ModuleVersion? MaximumPlatformVersion,
    int SchemaVersion);

/// <summary>How a candidate version relates to what is installed.</summary>
public enum VersionRelation
{
    NotInstalled = 0,
    Upgrade = 1,
    Same = 2,
    Downgrade = 3
}

public static class VersionSemantics
{
    public static VersionRelation Compare(ModuleVersion? installed, ModuleVersion candidate)
    {
        if (installed is null) return VersionRelation.NotInstalled;
        var c = candidate.CompareTo(installed);
        return c > 0 ? VersionRelation.Upgrade : c == 0 ? VersionRelation.Same : VersionRelation.Downgrade;
    }
}
