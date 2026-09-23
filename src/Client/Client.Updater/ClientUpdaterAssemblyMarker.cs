namespace Client.Updater;

/// <summary>
/// Marks the Client.Updater project boundary.
///
/// STAGE 2 STATUS: Project boundary only. NO update implementation.
///
/// This project will own:
/// - Update discovery (checking for new platform/module versions)
/// - Package download
/// - Digital signature verification of update packages
/// - Version validation and dependency resolution
/// - Module installation and replacement
/// - Core application update
/// - Rollback capability
/// - Update lifecycle coordination
///
/// Implementation belongs to Stage 7.
///
/// IMPORTANT: Client.Updater operates independently of business modules.
/// Business modules must NOT depend on Client.Updater.
/// Update operations are triggered by the application host, not by business logic.
/// </summary>
public static class ClientUpdaterAssemblyMarker
{
    // Intentionally empty.
    // Architecture tests use this type to locate the Client.Updater assembly.
}
