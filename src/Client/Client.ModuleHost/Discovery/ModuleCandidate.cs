using Platform.Core.Modules;

namespace Client.ModuleHost.Discovery;

/// <summary>
/// Represents a candidate module assembly that has been discovered on disk
/// but has not yet been validated, loaded, or activated.
///
/// IMPORTANT — Stage 2 vs Stage 4 boundary:
/// This type is intentionally minimal. It represents a discovered assembly path and
/// some basic metadata read from the assembly name before any business logic runs.
///
/// Stage 4 (Module Contract) will introduce IModule and IModuleManifest which define
/// the full lifecycle. At that point, ModuleCandidate will be used as the input to
/// the manifest reading process.
///
/// Do NOT add business concerns to this type.
/// </summary>
public sealed class ModuleCandidate
{
    /// <summary>
    /// The module identifier, derived from the assembly name convention.
    /// Convention: assemblies named "Modules.{Name}.Infrastructure" yield ModuleId("name").
    /// </summary>
    public ModuleId ModuleId { get; }

    /// <summary>
    /// The absolute path to the module's primary assembly file.
    /// </summary>
    public string AssemblyPath { get; }

    /// <summary>
    /// The directory that contains the module's assembly files.
    /// </summary>
    public string ModuleDirectory { get; }

    public ModuleCandidate(ModuleId moduleId, string assemblyPath, string moduleDirectory)
    {
        ModuleId = moduleId ?? throw new ArgumentNullException(nameof(moduleId));
        AssemblyPath = assemblyPath ?? throw new ArgumentNullException(nameof(assemblyPath));
        ModuleDirectory = moduleDirectory ?? throw new ArgumentNullException(nameof(moduleDirectory));
    }

    public override string ToString() => $"ModuleCandidate[{ModuleId}] at {AssemblyPath}";
}
