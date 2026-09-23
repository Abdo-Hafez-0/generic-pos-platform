using Microsoft.Extensions.Logging;
using Platform.Core.Modules;

namespace Client.ModuleHost.Discovery;

/// <summary>
/// Default file-system based module discovery implementation.
/// Scans the 'modules/' directory relative to the application base directory.
/// </summary>
internal sealed class FileSystemModuleDiscoveryService : IModuleDiscoveryService
{
    private readonly ILogger<FileSystemModuleDiscoveryService> _logger;
    private readonly string _modulesDirectory;

    // Module assembly suffix convention (to be finalized in Stage 4).
    private const string ModuleAssemblySuffix = ".Infrastructure.dll";

    public FileSystemModuleDiscoveryService(ILogger<FileSystemModuleDiscoveryService> logger)
    {
        _logger = logger;
        _modulesDirectory = Path.Combine(AppContext.BaseDirectory, "modules");
    }

    /// <inheritdoc/>
    public IReadOnlyList<ModuleCandidate> DiscoverModules()
    {
        _logger.LogInformation("Scanning for modules in: {ModulesDirectory}", _modulesDirectory);

        if (!Directory.Exists(_modulesDirectory))
        {
            _logger.LogInformation(
                "Modules directory does not exist at {ModulesDirectory}. No modules discovered.",
                _modulesDirectory);
            return [];
        }

        var candidates = new List<ModuleCandidate>();

        foreach (var moduleDir in Directory.GetDirectories(_modulesDirectory))
        {
            var moduleName = Path.GetFileName(moduleDir);

            // Look for the Infrastructure assembly by convention.
            var expectedAssemblyName = $"{moduleName}{ModuleAssemblySuffix}";
            var assemblyPath = Path.Combine(moduleDir, expectedAssemblyName);

            if (!File.Exists(assemblyPath))
            {
                _logger.LogDebug(
                    "Skipping directory '{ModuleDir}': no matching assembly '{AssemblyName}' found.",
                    moduleDir, expectedAssemblyName);
                continue;
            }

            try
            {
                var moduleId = new ModuleId(moduleName);
                var candidate = new ModuleCandidate(moduleId, assemblyPath, moduleDir);
                candidates.Add(candidate);

                _logger.LogInformation(
                    "Discovered module candidate: {ModuleId} at {AssemblyPath}",
                    moduleId, assemblyPath);
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex,
                    "Skipping module directory '{ModuleDir}': invalid module name.",
                    moduleDir);
            }
        }

        _logger.LogInformation("Module discovery complete. Found {Count} candidate(s).", candidates.Count);

        return candidates.AsReadOnly();
    }
}
