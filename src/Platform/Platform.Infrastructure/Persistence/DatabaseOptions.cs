namespace Platform.Infrastructure.Persistence;

/// <summary>
/// Configuration options for the local SQLite database.
///
/// Loaded from configuration section "Database" in appsettings.json:
/// {
///   "Database": {
///     "DatabaseFileName": "genericpos.db",
///     "DatabaseFolder": "LocalAppData"
///   }
/// }
///
/// Database file location strategy for a Windows desktop application:
///
/// OPTION: LocalAppData (default, recommended)
///   Path: %LOCALAPPDATA%\GenericPOS\{DatabaseFileName}
///   Pros: - Always writable without elevation.
///          - Per-user storage (correct for POS where data belongs to the business/user).
///          - Standard Windows desktop application pattern.
///          - Survives application updates (not in the installation directory).
///   Cons: - Data is not shared across Windows user accounts on the same machine.
///            (Acceptable for a POS scenario where each operator has a Windows account.)
///
/// OPTION: CommonApplicationData
///   Path: %PROGRAMDATA%\GenericPOS\{DatabaseFileName}
///   Use only if the POS must share data across Windows user accounts on the same machine.
///   This option requires either UAC elevation or explicit folder permission setup.
///
/// NOT RECOMMENDED:
///   - Application installation directory (may not be writable, broken by updates)
///   - Hard-coded absolute paths (not portable)
///   - User profile root (not organized, collision risk)
///   - Temp directory (volatile, cleared by Windows)
/// </summary>
public sealed class DatabaseOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Database";

    /// <summary>
    /// The name of the SQLite database file, including extension.
    /// Default: "genericpos.db"
    /// </summary>
    public string DatabaseFileName { get; init; } = "genericpos.db";

    /// <summary>
    /// Determines where the database file is stored.
    /// Supported values: "LocalAppData" (default), "CommonApplicationData", "Custom"
    /// </summary>
    public string DatabaseFolder { get; init; } = "LocalAppData";

    /// <summary>
    /// When DatabaseFolder = "Custom", this path is used directly.
    /// Must be an absolute path. The directory will be created if it doesn't exist.
    /// Ignored when DatabaseFolder is not "Custom".
    /// </summary>
    public string? CustomFolderPath { get; init; }

    /// <summary>
    /// The application subdirectory inside the chosen folder.
    /// Default: "GenericPOS"
    /// </summary>
    public string ApplicationSubDirectory { get; init; } = "GenericPOS";

    /// <summary>
    /// Resolves the full absolute path to the database file based on these options.
    /// </summary>
    public string ResolveDatabasePath()
    {
        string baseFolder = DatabaseFolder switch
        {
            "LocalAppData" => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CommonApplicationData" => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Custom" when !string.IsNullOrWhiteSpace(CustomFolderPath) => CustomFolderPath,
            _ => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
        };

        var directory = Path.Combine(baseFolder, ApplicationSubDirectory);

        // Ensure the directory exists. SQLite cannot create missing parent directories.
        Directory.CreateDirectory(directory);

        return Path.Combine(directory, DatabaseFileName);
    }

    /// <summary>
    /// Returns the EF Core SQLite connection string for the configured path.
    /// </summary>
    public string BuildConnectionString()
    {
        var path = ResolveDatabasePath();
        return $"Data Source={path}";
    }
}
