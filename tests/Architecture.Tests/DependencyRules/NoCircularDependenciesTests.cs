using System.Reflection;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// ARCH-010: Circular module dependencies are forbidden.
///
/// Circular dependencies prevent independent versioning, testing, and deployment of modules.
/// They indicate a design problem where two modules are too tightly coupled.
///
/// This test uses reflection to detect circular references in the assembly reference graph.
/// NetArchTest.Rules does not provide a built-in circular dependency check.
///
/// Status: TESTABLE in Stage 1 for Platform assemblies.
/// Will automatically expand to detect circles involving module assemblies as they are added
/// to the Assemblies registry.
/// </summary>
public sealed class NoCircularDependenciesTests
{
    [Fact(DisplayName = "ARCH-010: All project assemblies must have no circular dependencies")]
    public void AllProjectAssemblies_MustHave_NoCircularDependencies()
    {
        var assemblies = Assemblies.AllProjectAssemblies;
        var assemblyNames = new HashSet<string>(
            assemblies.Select(a => a.GetName().Name!),
            StringComparer.OrdinalIgnoreCase);

        var cycles = FindCycles(assemblies, assemblyNames);

        Assert.True(
            cycles.Count == 0,
            $"[ARCH-010] Circular dependencies detected in platform assemblies:\n" +
            string.Join("\n", cycles.Select(c => "  " + string.Join(" -> ", c))));
    }

    /// <summary>
    /// Detects cycles in the assembly reference graph using DFS with path tracking.
    /// Only considers assemblies that are part of the project (not framework/NuGet assemblies).
    /// </summary>
    private static List<List<string>> FindCycles(
        IReadOnlyList<Assembly> assemblies,
        HashSet<string> knownAssemblyNames)
    {
        var graph = BuildReferenceGraph(assemblies, knownAssemblyNames);
        var cycles = new List<List<string>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = new Stack<string>();
        var inPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.Keys)
        {
            if (!visited.Contains(node))
                DetectCycleDfs(node, graph, visited, path, inPath, cycles);
        }

        return cycles;
    }

    private static Dictionary<string, List<string>> BuildReferenceGraph(
        IReadOnlyList<Assembly> assemblies,
        HashSet<string> knownAssemblyNames)
    {
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name!;
            var references = assembly
                .GetReferencedAssemblies()
                .Select(r => r.Name!)
                .Where(r => knownAssemblyNames.Contains(r))
                .ToList();

            graph[name] = references;
        }

        return graph;
    }

    private static void DetectCycleDfs(
        string current,
        Dictionary<string, List<string>> graph,
        HashSet<string> visited,
        Stack<string> path,
        HashSet<string> inPath,
        List<List<string>> cycles)
    {
        visited.Add(current);
        path.Push(current);
        inPath.Add(current);

        if (graph.TryGetValue(current, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    DetectCycleDfs(neighbor, graph, visited, path, inPath, cycles);
                }
                else if (inPath.Contains(neighbor))
                {
                    // Found a cycle — extract the path
                    var cycle = path.Reverse().ToList();
                    var cycleStart = cycle.IndexOf(neighbor);
                    cycles.Add(cycle.Skip(cycleStart).Append(neighbor).ToList());
                }
            }
        }

        path.Pop();
        inPath.Remove(current);
    }
}
