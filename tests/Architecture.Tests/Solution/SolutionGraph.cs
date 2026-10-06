using System.Xml.Linq;
using Tests.Common;

namespace Architecture.Tests.Solution;

/// <summary>
/// The project graph of the solution as the .csproj files declare it, discovered from the file system (src/ and tools/).
///
/// Why from the project files and not from loaded assemblies: the WPF projects (Client.Desktop and every Module.UI) target
/// net10.0-windows and cannot be referenced by this net10.0 test project, so assembly-based rules never saw them. The project
/// files cover every project, and a new project or module is checked the moment it is added, without editing a list.
/// </summary>
internal sealed class SolutionGraph
{
    private static readonly Lazy<SolutionGraph> Current = new(Load);

    public static SolutionGraph Instance => Current.Value;

    private readonly Dictionary<string, ProjectNode> _byName;

    private SolutionGraph(IEnumerable<ProjectNode> projects)
    {
        _byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<ProjectNode> Projects => _byName.Values;

    public ProjectNode this[string name] => _byName[name];

    public bool Contains(string name) => _byName.ContainsKey(name);

    /// <summary>The business modules: one folder per module under src/Modules.</summary>
    public IReadOnlyList<string> Modules => Projects.Where(p => p.Module is not null).Select(p => p.Module!).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();

    public IEnumerable<ProjectNode> ModuleProjects(string? layer = null)
        => Projects.Where(p => p.Module is not null && (layer is null || p.Layer == layer));

    /// <summary>Every project reachable through project references (not including the project itself).</summary>
    public IReadOnlySet<string> Closure(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(_byName[name].References);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next) || !_byName.TryGetValue(next, out var node)) continue;
            foreach (var reference in node.References) stack.Push(reference);
        }

        return seen;
    }

    /// <summary>Every NuGet package and framework reference a project gets directly or through its project references.</summary>
    public IReadOnlySet<string> PackageClosure(string name)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Closure(name).Append(name))
        {
            if (!_byName.TryGetValue(project, out var node)) continue;
            all.UnionWith(node.Packages);
            all.UnionWith(node.FrameworkReferences);
            if (node.UsesWpf) all.Add("WPF");
        }

        return all;
    }

    private static SolutionGraph Load()
    {
        var root = RepoPaths.Root();
        var files = new[] { "src", "tools" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.csproj", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        return new SolutionGraph(files.Select(f => ProjectNode.Parse(root, f)));
    }
}

internal sealed record ProjectNode(
    string Name,
    string RelativePath,
    IReadOnlyList<string> References,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> FrameworkReferences,
    bool UsesWpf,
    string? Module,
    string? Layer)
{
    public static readonly string[] ModuleLayers = ["Domain", "Application", "Contracts", "Infrastructure", "UI"];

    public string Directory => Path.GetDirectoryName(RelativePath)!;

    public static ProjectNode Parse(string root, string path)
    {
        var xml = XDocument.Load(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var relative = Path.GetRelativePath(root, path);

        IReadOnlyList<string> Items(string element) => xml.Descendants(element)
            .Select(e => (string?)e.Attribute("Include"))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        var references = Items("ProjectReference").Select(r => Path.GetFileNameWithoutExtension(r.Replace('\\', Path.DirectorySeparatorChar))).ToList();
        var usesWpf = xml.Descendants("UseWPF").Any(e => string.Equals(e.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase));
        var sdk = (string?)xml.Root?.Attribute("Sdk") ?? string.Empty;
        var frameworks = Items("FrameworkReference").ToList();
        if (sdk.Contains("Web", StringComparison.Ordinal)) frameworks.Add("Microsoft.AspNetCore.App");

        // src/Modules/<Module>/<Module>.<Layer>/<Module>.<Layer>.csproj
        string? module = null, layer = null;
        var parts = relative.Split(Path.DirectorySeparatorChar);
        if (parts.Length >= 4 && parts[0] == "src" && parts[1] == "Modules")
        {
            module = parts[2];
            var suffix = name.StartsWith(module + ".", StringComparison.Ordinal) ? name[(module.Length + 1)..] : null;
            layer = suffix is not null && ModuleLayers.Contains(suffix) ? suffix : "Unknown";
        }

        return new ProjectNode(name, relative, references, Items("PackageReference"), frameworks, usesWpf, module, layer);
    }
}
