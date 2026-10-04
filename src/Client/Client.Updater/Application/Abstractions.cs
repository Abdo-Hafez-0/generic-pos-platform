using Client.Updater.Domain;
using Platform.Core.Modules;
using Platform.Core.Results;
using Updates.Contracts;

namespace Client.Updater.Application;

/// <summary>
/// Transport to the update source. The updater depends on THIS abstraction, never on HttpClient. Implementations must
/// not throw for ordinary network failures: they return failure responses/results. Nothing received through it is trusted
/// until the signed manifest verifies.
/// </summary>
public interface IUpdateClient
{
    Task<UpdateCheckResponse> CheckAsync(UpdateCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>Streams the package file into <paramref name="destination"/>.</summary>
    Task<Result> DownloadAsync(Guid packageId, Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>What is installed right now (host/core version and modules). Read-only.</summary>
public interface IInstalledStateProvider
{
    ModuleVersion HostVersion { get; }

    IReadOnlyList<InstalledModule> GetInstalledModules();
}

/// <summary>
/// Protects customer data around a migration: creates a restore point of the database BEFORE migrating and can restore it
/// on explicit request. The updater never deletes or recreates the database.
/// </summary>
public interface IDataSafeguard
{
    /// <summary>Returns the path/handle of the restore point.</summary>
    Task<Result<string>> CreateRestorePointAsync(Guid packageId, CancellationToken cancellationToken = default);

    /// <summary>Restores the database from a restore point. Destroys changes made after the restore point: explicit action only.</summary>
    Task<Result> RestoreAsync(string restorePoint, CancellationToken cancellationToken = default);
}

/// <param name="StagedPath">Where the verified package payload was staged (not yet active).</param>
public sealed record MigrationRequest(ModuleId ModuleId, int FromSchemaVersion, int ToSchemaVersion, string StagedPath);

/// <param name="Succeeded">Migration done (or legitimately deferred).</param>
/// <param name="DatabaseModified">Something in the module's tables may have changed (conservative when failed).</param>
/// <param name="Deferred">No migrator was available now; the module's own startup initializer applies pending migrations.</param>
public sealed record MigrationOutcome(bool Succeeded, bool DatabaseModified, bool Deferred, string? Message);

/// <summary>
/// Orchestrates a module-owned migration. It never executes SQL itself and never touches tables: the module's own
/// IModuleMigrator does the work (or the module's startup initializer does it when the new version starts).
/// </summary>
public interface IMigrationCoordinator
{
    Task<MigrationOutcome> MigrateAsync(MigrationRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Static updater configuration.</summary>
/// <param name="TargetFramework">The runtime target packages must have been built for.</param>
/// <param name="MaxStartupAttempts">Starts allowed for an activated-but-unconfirmed update before it is rolled back.</param>
public sealed record UpdaterOptions(string TargetFramework, int MaxStartupAttempts = 2);

/// <summary>A package that passed the whole verification pipeline. Verification mutates nothing.</summary>
public sealed record VerifiedPackage(
    PackageManifest Manifest,
    SignedPackageManifest Envelope,
    VersionRelation Relation,
    ModuleVersion? InstalledVersion,
    ModuleVersion CandidateVersion,
    string? PackagePath);

/// <summary>
/// Local update state on disk: staging, side-by-side installed versions, the atomic active-version pointer, the journal.
/// Implemented by Infrastructure.UpdateStore; kept behind an interface so the application layer has no infrastructure dependency.
/// </summary>
public interface IUpdateStore
{
    string Root { get; }
    string DownloadsDir { get; }
    string StagingDir { get; }
    string JournalDir { get; }
    string RestoreDir { get; }

    string DownloadPath(Guid packageId);
    string StagingPath(Guid packageId);
    string TargetDir(string targetId);
    string VersionDir(string targetId, string version);
    string ActivePointerPath(string targetId);
    void EnsureDirectories();

    ActivePointer? ReadActive(string targetId);

    /// <summary>Atomic switch. Throws IOException if the pointer cannot be replaced (nothing is changed then).</summary>
    void WriteActive(string targetId, ActivePointer pointer);

    void ClearActive(string targetId);

    void SaveJournal(UpdateJournal journal);
    UpdateJournal? LoadJournal(Guid packageId);
    IReadOnlyList<UpdateJournal> ListJournals();
}

internal static class DirectoryCleanup
{
    /// <summary>Best-effort delete. Leftovers in staging/downloads are harmless: they are never activated.</summary>
    public static void DeleteQuietly(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
