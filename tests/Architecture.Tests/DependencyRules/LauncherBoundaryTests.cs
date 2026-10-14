using System.Text.RegularExpressions;
using Platform.Core.Modules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for the launcher (PKG-01). ARCH-PKG-001 .. ARCH-PKG-002.
///
/// Shape being protected:
///   Client.Launcher (GenericPOS.exe): replaced only by the installer, never by an update, so it shares no assembly with what updates
///                    replace - no project of the solution and no package; nothing references it.
///   Module files:    the launcher lays an activated module over the core and lets a module replace only ITS assemblies, recognised by
///                    name - module id "cash-management" owns CashManagement.* - so every module's assemblies must be named that way.
/// </summary>
public sealed class LauncherBoundaryTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact(DisplayName = "ARCH-PKG-001: The launcher stands alone: no project of the solution, no package, and nothing in src references it")]
    public void TheLauncherStandsAlone()
    {
        var root = RepoRoot();
        var launcher = File.ReadAllText(Path.Combine(root, "src", "Client", "Client.Launcher", "Client.Launcher.csproj"));
        Assert.DoesNotContain("<ProjectReference", launcher);
        Assert.DoesNotContain("<PackageReference", launcher);
        Assert.Contains("<AssemblyName>GenericPOS</AssemblyName>", launcher);

        var referencing = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("Client.Launcher.csproj", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(referencing);
    }

    [Fact(DisplayName = "ARCH-PKG-002: Every module's assemblies are named after its module id (cash-management -> CashManagement.*), the rule the launcher uses")]
    public void ModuleAssembliesAreNamedAfterTheModule()
    {
        // the manifests: the id each module declares, and the assembly it lives in
        var manifests = Assemblies.AllModuleAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IModuleManifest).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false } && t.GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes) is not null)
            .Select(t => (Type: t, Manifest: (IModuleManifest)Activator.CreateInstance(t, nonPublic: true)!))
            .ToList();
        Assert.True(manifests.Count >= 14, $"only {manifests.Count} module manifests found");

        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, manifest) in manifests)
        {
            var prefix = manifest.ModuleId.Value.Replace("-", string.Empty, StringComparison.Ordinal);
            Assert.True(type.Assembly.GetName().Name!.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase),
                $"module '{manifest.ModuleId.Value}' is declared in {type.Assembly.GetName().Name}, not in an assembly named {prefix}.*");
            prefixes.Add(prefix);
        }

        // every project of a module folder (UI included, which this test cannot load) carries the module's name
        var modules = Path.Combine(RepoRoot(), "src", "Modules");
        foreach (var folder in Directory.GetDirectories(modules))
        {
            var name = Path.GetFileName(folder);
            Assert.Contains(name, prefixes);
            foreach (var project in Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            {
                var assemblyName = Regex.Match(File.ReadAllText(project), "<AssemblyName>([^<]+)</AssemblyName>") is { Success: true } m
                    ? m.Groups[1].Value
                    : Path.GetFileNameWithoutExtension(project);
                Assert.True(assemblyName.StartsWith(name + ".", StringComparison.Ordinal), $"{Path.GetFileName(project)} builds {assemblyName}, not {name}.*");
            }
        }

        // and no core assembly could be mistaken for a module's
        foreach (var core in new[] { "Client", "Platform", "Security", "Updates", "Licensing", "Cloud" })
            Assert.DoesNotContain(core, prefixes);
    }
}
