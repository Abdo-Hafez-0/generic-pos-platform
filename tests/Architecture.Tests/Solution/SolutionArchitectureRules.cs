using System.Reflection;
using System.Text.RegularExpressions;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Modules;
using Platform.Core.Modules;
using Tests.Common;

namespace Architecture.Tests.Solution;

/// <summary>
/// The twelve mandatory dependency rules of "Architecture &amp; Solution Design" section 77, enforced on EVERY project and module of the
/// solution (Stage 13). ARCH-SOL-001 .. ARCH-SOL-016.
///
/// The per-module suites (ARCH-CAT/INV/.../REP) check each module against a hand-written list. These rules are generic: modules and
/// projects are discovered (project files, module folders, manifests), so a new module, a new project or a WPF project cannot escape
/// them. Project-level rules read the .csproj graph (which also covers the net10.0-windows projects); type-level rules use reflection.
/// </summary>
public sealed class SolutionArchitectureRules
{
    private static SolutionGraph Graph => SolutionGraph.Instance;

    private static readonly string[] Persistence = ["Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Sqlite", "Microsoft.EntityFrameworkCore.Design", "Microsoft.Data.Sqlite"];
    private static readonly string[] Http = ["Microsoft.Extensions.Http", "Microsoft.AspNetCore.App", "System.Net.Http"];

    private static bool IsCloud(ProjectNode p) => p.RelativePath.StartsWith(Path.Combine("src", "Cloud"), StringComparison.Ordinal);

    private static string Describe(IEnumerable<string> violations) => "\n  " + string.Join("\n  ", violations);

    // ------------------------------------------------------------------ discovery is complete

    [Fact(DisplayName = "ARCH-SOL-001: Every project under src/ and tools/ is in the solution, and every module project has a known layer")]
    public void EveryProject_IsBuiltAndClassified()
    {
        var solution = File.ReadAllText(Path.Combine(RepoPaths.Root(), "GenericPOS.sln"));
        Assert.Empty(Graph.Projects.Where(p => !solution.Contains(p.RelativePath, StringComparison.OrdinalIgnoreCase)).Select(p => p.RelativePath));
        Assert.Empty(Graph.ModuleProjects().Where(p => p.Layer == "Unknown").Select(p => p.Name));
        Assert.True(Graph.Modules.Count >= 13, "the module folders were not discovered");
    }

    [Fact(DisplayName = "ARCH-SOL-002: The assembly registry used by the type-level rules contains every non-UI module assembly")]
    public void TheAssemblyRegistry_CoversEveryModule()
    {
        var registered = Assemblies.AllModuleAssemblies.Select(a => a.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        var missing = Graph.ModuleProjects().Where(p => p.Layer != "UI").Select(p => p.Name).Except(registered).ToList();
        Assert.True(missing.Count == 0, "Add to Assemblies.AllModuleAssemblies: " + string.Join(", ", missing));
    }

    // ------------------------------------------------------------------ Rule 1: core never depends on business modules

    [Fact(DisplayName = "ARCH-SOL-003 (Rule 1): No Platform project reaches a business module, a client, a server or a tool")]
    public void Platform_ReachesNoModule()
    {
        var violations = Graph.Projects.Where(p => p.Name.StartsWith("Platform.", StringComparison.Ordinal))
            .SelectMany(p => Graph.Closure(p.Name).Where(r => !r.StartsWith("Platform.", StringComparison.Ordinal)).Select(r => $"{p.Name} -> {r}"));
        Assert.Empty(violations);
    }

    // ------------------------------------------------------------------ Rules 2 and 3: domain knows no infrastructure and no UI

    [Fact(DisplayName = "ARCH-SOL-004 (Rules 2, 3): Every Domain project references only Platform.Core and has no persistence, UI, HTTP or package dependency")]
    public void Domain_IsPure()
    {
        var violations = new List<string>();
        foreach (var domain in Graph.ModuleProjects("Domain"))
        {
            violations.AddRange(domain.References.Where(r => r != "Platform.Core").Select(r => $"{domain.Name} -> {r}"));
            violations.AddRange(Graph.PackageClosure(domain.Name).Select(p => $"{domain.Name} uses {p}"));
        }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    // ------------------------------------------------------------------ Rules 4 and 5: other modules only through their Contracts

    [Fact(DisplayName = "ARCH-SOL-005 (Rules 4, 5): A module project references another module only through that module's Contracts")]
    public void CrossModuleReferences_AreContractsOnly()
    {
        var violations = Graph.ModuleProjects()
            .SelectMany(p => p.References.Where(r => Graph.Contains(r) && Graph[r].Module is { } other && other != p.Module && Graph[r].Layer != "Contracts")
                .Select(r => $"{p.Name} -> {r}"));
        Assert.Empty(violations);
    }

    [Fact(DisplayName = "ARCH-SOL-006 (Rule 4): Only the desktop composition root references a module's Infrastructure from outside the module")]
    public void ModuleInfrastructure_IsReferencedOnlyByItsModuleAndTheCompositionRoot()
    {
        var violations = Graph.Projects
            .SelectMany(p => p.References.Where(r => Graph.Contains(r) && Graph[r].Layer == "Infrastructure" && Graph[r].Module != p.Module && p.Name != "Client.Desktop")
                .Select(r => $"{p.Name} -> {r}"));
        Assert.Empty(violations);
    }

    [Fact(DisplayName = "ARCH-SOL-007 (Rule 5): Contracts reference nothing but Platform.Core; Domain and Contracts reach no other module; a UI reaches no other module's implementation")]
    public void Contracts_AreSelfContained()
    {
        var violations = new List<string>();
        foreach (var contracts in Graph.ModuleProjects("Contracts"))
            violations.AddRange(contracts.References.Where(r => r != "Platform.Core").Select(r => $"{contracts.Name} -> {r}"));

        // Domain and Contracts know no other module at all; a UI reaches other modules' Contracts only through its own Application layer.
        foreach (var project in Graph.ModuleProjects().Where(p => p.Layer is "Domain" or "Contracts" or "UI"))
            violations.AddRange(Graph.Closure(project.Name)
                .Where(r => Graph.Contains(r) && Graph[r].Module is { } other && other != project.Module && (project.Layer != "UI" || Graph[r].Layer != "Contracts"))
                .Select(r => $"{project.Name} reaches {r}"));

        Assert.True(violations.Count == 0, Describe(violations));
    }

    // ------------------------------------------------------------------ Rule 6: UI never accesses the database

    [Fact(DisplayName = "ARCH-SOL-008 (Rule 6): No module UI can reach a DbContext: no Infrastructure, Client.Host, EF Core or SQLite anywhere in its closure")]
    public void ModuleUi_CannotReachPersistence()
    {
        var violations = new List<string>();
        foreach (var ui in Graph.ModuleProjects("UI"))
        {
            violations.AddRange(Graph.Closure(ui.Name)
                .Where(r => r is "Client.Host" or "Platform.Infrastructure" || r.EndsWith(".Infrastructure", StringComparison.Ordinal))
                .Select(r => $"{ui.Name} reaches {r}"));
            violations.AddRange(Graph.PackageClosure(ui.Name).Where(p => Persistence.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(p => $"{ui.Name} uses {p}"));
        }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    [Fact(DisplayName = "ARCH-SOL-009 (Rule 6): The desktop shell is a composition root only: its code touches no DbContext, connection or SQL")]
    public void DesktopShell_TouchesNoDatabase()
    {
        var forbidden = new Regex(@"DbContext|SqliteConnection|EntityFrameworkCore|\bDatabase\.|ExecuteSql|""\s*(SELECT|INSERT|UPDATE|DELETE)\b", RegexOptions.IgnoreCase);
        var folder = Path.Combine(RepoPaths.Root(), "src", "Client", "Client.Desktop");
        var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal)) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        Assert.NotEmpty(files);
        Assert.Empty(files.Where(f => forbidden.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName));
    }

    // ------------------------------------------------------------------ Rules 7 and 8: no HTTP in business logic, no cloud requirement

    [Fact(DisplayName = "ARCH-SOL-010 (Rules 7, 8): Business modules and Platform reach no HTTP stack, cloud transport, server or cloud project")]
    public void BusinessCode_ReachesNoHttpOrCloud()
    {
        var violations = new List<string>();
        foreach (var project in Graph.Projects.Where(p => p.Module is not null || p.Name.StartsWith("Platform.", StringComparison.Ordinal)))
        {
            violations.AddRange(Graph.Closure(project.Name)
                .Where(r => r.EndsWith(".Http", StringComparison.Ordinal) || r.EndsWith(".Api", StringComparison.Ordinal) || (Graph.Contains(r) && IsCloud(Graph[r]))
                            || r.StartsWith("Client.Licensing", StringComparison.Ordinal) || r.StartsWith("Client.Updater", StringComparison.Ordinal) || r.StartsWith("Licensing.", StringComparison.Ordinal) || r.StartsWith("Updates.", StringComparison.Ordinal))
                .Select(r => $"{project.Name} reaches {r}"));
            violations.AddRange(Graph.PackageClosure(project.Name).Where(p => Http.Contains(p, StringComparer.OrdinalIgnoreCase)).Select(p => $"{project.Name} uses {p}"));
        }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    [Fact(DisplayName = "ARCH-SOL-011 (Rule 7): No type in a business module or Platform assembly uses System.Net.Http or ASP.NET Core")]
    public void BusinessTypes_UseNoHttp()
    {
        var violations = new List<string>();
        foreach (var assembly in Assemblies.AllModuleAssemblies.Concat(Assemblies.AllPlatformAssemblies))
            foreach (var forbidden in new[] { "System.Net.Http", "Microsoft.AspNetCore" })
            {
                var result = NetArchTest.Rules.Types.InAssembly(assembly).ShouldNot().HaveDependencyOn(forbidden).GetResult();
                if (!result.IsSuccessful) violations.Add($"{assembly.GetName().Name}: {string.Join(", ", result.FailingTypeNames ?? [])} -> {forbidden}");
            }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    // ------------------------------------------------------------------ Rule 9: explicit module dependencies

    private static IReadOnlyDictionary<string, IModuleManifest> ManifestsByModule()
    {
        var result = new Dictionary<string, IModuleManifest>(StringComparer.Ordinal);
        foreach (var assembly in Assemblies.AllModuleAssemblies.Where(a => a.GetName().Name!.EndsWith(".Infrastructure", StringComparison.Ordinal)))
        {
            var moduleType = assembly.GetTypes().Single(t => t is { IsClass: true, IsAbstract: false } && typeof(IModule).IsAssignableFrom(t));
            var module = (IModule)Activator.CreateInstance(moduleType)!;
            result[assembly.GetName().Name!.Split('.')[0]] = module.Manifest;
        }

        return result;
    }

    [Fact(DisplayName = "ARCH-SOL-012 (Rule 9): Every module has exactly one runtime module and manifest, and the manifests of all modules resolve (no missing, incompatible or circular dependency)")]
    public void EveryModule_HasAManifest_AndTheFullSetResolves()
    {
        var manifests = ManifestsByModule();
        Assert.Equal(Graph.Modules, manifests.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(manifests.Count, manifests.Values.Select(m => m.ModuleId).Distinct().Count());

        var resolution = new ModuleDependencyResolver().Resolve(manifests.Values.ToList());
        Assert.True(resolution.IsSuccess, string.Join("; ", resolution.Errors));
    }

    [Fact(DisplayName = "ARCH-SOL-013 (Rule 9): A module's declared dependencies are real, and every undeclared use of another module is optional (a nullable, defaulted constructor parameter)")]
    public void ModuleDependencies_AreExplicit()
    {
        var manifests = ManifestsByModule();
        var moduleOfContracts = manifests.Keys.ToDictionary(m => m + ".Contracts", m => m, StringComparer.Ordinal);
        var violations = new List<string>();

        foreach (var (module, manifest) in manifests)
        {
            var declared = manifest.Dependencies.Select(d => d.RequiredModuleId).ToHashSet();

            // declared => the module really uses that module's contracts
            var referenced = Graph.ModuleProjects().Where(p => p.Module == module && p.Layer is "Application" or "Infrastructure")
                .SelectMany(p => p.References).Where(moduleOfContracts.ContainsKey).Select(r => manifests[moduleOfContracts[r]].ModuleId).ToHashSet();
            violations.AddRange(declared.Where(d => !referenced.Contains(d)).Select(d => $"{module} declares '{d}' but references none of its contracts"));

            // used but not declared => must be optional at every constructor that asks for it
            var assemblies = Assemblies.AllModuleAssemblies.Where(a => a.GetName().Name == module + ".Application" || a.GetName().Name == module + ".Infrastructure");
            foreach (var type in assemblies.SelectMany(a => a.GetTypes()).Where(t => t.IsClass))
                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    foreach (var parameter in ctor.GetParameters())
                    {
                        var owner = moduleOfContracts.GetValueOrDefault(parameter.ParameterType.Assembly.GetName().Name!);
                        if (owner is null || owner == module || declared.Contains(manifests[owner].ModuleId)) continue;
                        if (!parameter.HasDefaultValue)
                            violations.Add($"{type.FullName}({parameter.ParameterType.Name} {parameter.Name}) needs '{manifests[owner].ModuleId}', which {module} does not declare");
                    }
        }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    // ------------------------------------------------------------------ Rule 10: no cycles

    [Fact(DisplayName = "ARCH-SOL-014 (Rule 10): Neither the project graph nor the module graph (projects collapsed to their module) has a cycle")]
    public void ProjectAndModuleGraphs_AreAcyclic()
    {
        var projectEdges = Graph.Projects.ToDictionary(p => p.Name, p => p.References.Where(Graph.Contains).ToList());
        Assert.Empty(Cycles(projectEdges));

        var moduleEdges = Graph.Modules.ToDictionary(m => m, m => Graph.ModuleProjects().Where(p => p.Module == m)
            .SelectMany(p => p.References).Where(r => Graph.Contains(r) && Graph[r].Module is { } o && o != m).Select(r => Graph[r].Module!).Distinct().ToList());
        Assert.Empty(Cycles(moduleEdges));
    }

    private static List<string> Cycles(Dictionary<string, List<string>> edges)
    {
        var cycles = new List<string>();
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the path, 2 = done
        var path = new List<string>();

        void Visit(string node)
        {
            state[node] = 1;
            path.Add(node);
            foreach (var next in edges.GetValueOrDefault(node) ?? [])
            {
                if (state.GetValueOrDefault(next) == 1) cycles.Add(string.Join(" -> ", path.SkipWhile(n => n != next).Append(next)));
                else if (state.GetValueOrDefault(next) == 0) Visit(next);
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2;
        }

        foreach (var node in edges.Keys.Where(n => state.GetValueOrDefault(n) == 0)) Visit(node);
        return cycles;
    }

    // ------------------------------------------------------------------ Rule 12: licensing contains no business logic

    [Fact(DisplayName = "ARCH-SOL-015 (Rule 12): Licensing (client, contracts, server) reaches no business module, and no business module reaches licensing")]
    public void Licensing_ContainsNoBusinessLogic()
    {
        static bool IsLicensing(string name) => name.StartsWith("Client.Licensing", StringComparison.Ordinal) || name.StartsWith("Licensing.", StringComparison.Ordinal) || name.StartsWith("LicenseServer.", StringComparison.Ordinal);

        var violations = Graph.Projects.Where(p => IsLicensing(p.Name)).SelectMany(p => Graph.Closure(p.Name)
            .Where(r => Graph.Contains(r) && Graph[r].Module is not null)
            .Select(r => $"{p.Name} reaches {r}")).ToList();
        violations.AddRange(Graph.ModuleProjects().SelectMany(p => Graph.Closure(p.Name).Where(IsLicensing).Select(r => $"{p.Name} reaches {r}")));
        Assert.True(violations.Count == 0, Describe(violations));
    }

    [Fact(DisplayName = "ARCH-SOL-016 (Rule 12): Entitlements stay per module: every capability a module declares is owned by that module's manifest ID")]
    public void Capabilities_AreOwnedByTheModuleThatDeclaresThem()
    {
        var manifests = ManifestsByModule();
        var violations = new List<string>();
        foreach (var assembly in Assemblies.AllModuleAssemblies.Where(a => a.GetName().Name!.EndsWith(".Application", StringComparison.Ordinal)))
        {
            var module = assembly.GetName().Name!.Split('.')[0];
            foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICapabilityProvider).IsAssignableFrom(t)))
                foreach (var capability in ((ICapabilityProvider)Activator.CreateInstance(type)!).GetCapabilities())
                    if (capability.Module != manifests[module].ModuleId.Value)
                        violations.Add($"{capability.Code} is declared by {module} but owned by '{capability.Module}' (the license would check the wrong module)");
        }

        Assert.True(violations.Count == 0, Describe(violations));
    }
}
