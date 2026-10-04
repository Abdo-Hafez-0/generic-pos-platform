namespace Client.Updater;

/// <summary>
/// Marks the Client.Updater assembly (used by Architecture.Tests to locate it).
///
/// Client.Updater is the client side of the secure update system (Stage 7):
///   Domain          - UpdateState machine, UpdateJournal, ActivePointer, version semantics
///   Application     - PackageVerifier (the 14-step verification pipeline), UpdateService (check / download / install /
///                     recover / confirm / rollback), IUpdateClient (transport abstraction), IDataSafeguard,
///                     IMigrationCoordinator, IInstalledStateProvider
///   Infrastructure  - UpdateStore (staging, side-by-side versions, atomic active pointer, journal),
///                     SqliteDataSafeguard (restore points), ModuleOwnedMigrationCoordinator, host registration
///
/// It contains NO HTTP (see Client.Updater.Http), NO EF Core, NO WPF, NO signing key capability and NO reference to
/// any business module or to Client.Licensing (licensing is consulted only through the Platform abstraction
/// ILicenseEntitlementService). Business modules never reference it. Updates never delete or recreate the database.
/// </summary>
public static class ClientUpdaterAssemblyMarker
{
    // Intentionally empty.
}
