using System.Reflection;
using NetArchTest.Rules;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for Stage 6 licensing. ARCH-LIC-001 .. ARCH-LIC-018.
///
/// Shape being protected:
///   Platform.Application  : ILicenseEntitlementService (the ONLY licensing type business code may see)
///   Licensing.Contracts   : shared signed-license wire model (no keys, no HTTP)
///   Client.Licensing      : Domain / Application / Infrastructure namespaces; NO HTTP, NO EF, NO WPF, NO business modules
///   Client.Licensing.Http : the only client project using HttpClient
///   LicenseServer.*       : owns the private signing key; never references client or business code
/// Business modules and Platform never reference licensing implementations or the license server.
/// </summary>
public sealed class LicensingBoundaryTests
{
    private const string Domain = "Client.Licensing.Domain";
    private const string Application = "Client.Licensing.Application";
    private const string Infrastructure = "Client.Licensing.Infrastructure";

    private static readonly string[] BusinessModulePrefixes = ["Catalog", "Inventory", "Sales", "POS"];

    private static void AssertNoDependency(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();

        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static void AssertNamespaceNoDependency(Assembly assembly, string ns, string forbidden, string because)
    {
        Assert.True(Types.InAssembly(assembly).That().ResideInNamespace(ns).GetTypes().Any(),
            $"No types found in namespace {ns}; the rule would pass vacuously.");

        var result = Types.InAssembly(assembly)
            .That().ResideInNamespace(ns)
            .Should().NotHaveDependencyOn(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful,
            $"Types in {ns} must not depend on {forbidden}. {because} Failing types: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static IEnumerable<string> ReferencedNames(Assembly assembly)
        => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    [Fact(DisplayName = "ARCH-LIC-001: Client licensing domain must not depend on HTTP, EF Core, WPF or infrastructure")]
    public void LicensingDomain_MustNotDependOn_HttpEfWpfOrInfrastructure()
    {
        var a = Assemblies.ClientLicensing;
        AssertNamespaceNoDependency(a, Domain, "System.Net.Http", "Domain must be transport-free.");
        AssertNamespaceNoDependency(a, Domain, "Microsoft.EntityFrameworkCore", "Domain must be persistence-free.");
        AssertNamespaceNoDependency(a, Domain, "System.Windows", "Domain must be UI-free.");
        AssertNamespaceNoDependency(a, Domain, Infrastructure, "Dependency direction is inward.");
        AssertNamespaceNoDependency(a, Domain, Application, "Dependency direction is inward.");
    }

    [Fact(DisplayName = "ARCH-LIC-002: Client licensing application must not depend on HttpClient or infrastructure")]
    public void LicensingApplication_MustNotDependOn_HttpOrInfrastructure()
    {
        var a = Assemblies.ClientLicensing;
        AssertNamespaceNoDependency(a, Application, "System.Net.Http", "Application depends on ILicenseClient, never HttpClient.");
        AssertNamespaceNoDependency(a, Application, Infrastructure, "Application must not know infrastructure.");
        AssertNamespaceNoDependency(a, Application, "Microsoft.EntityFrameworkCore", "No EF in licensing.");
    }

    [Fact(DisplayName = "ARCH-LIC-003: The Client.Licensing assembly itself has no HTTP, WPF, EF Core or SQLite reference")]
    public void ClientLicensing_Assembly_HasNoHttpWpfEfOrSqlite()
    {
        var refs = ReferencedNames(Assemblies.ClientLicensing).ToList();

        Assert.DoesNotContain(refs, n => n.StartsWith("System.Net.Http", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, n => n is "PresentationFramework" or "PresentationCore" or "WindowsBase");
        Assert.DoesNotContain(refs, n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain(refs, n => n.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        AssertNoDependency(Assemblies.ClientLicensing, "System.Net.Http", "HTTP lives in Client.Licensing.Http.");
    }

    [Fact(DisplayName = "ARCH-LIC-004: Client licensing must not depend on any business module")]
    public void ClientLicensing_MustNotDependOn_BusinessModules()
    {
        foreach (var assembly in new[] { Assemblies.ClientLicensing, Assemblies.ClientLicensingHttp })
        foreach (var module in BusinessModulePrefixes)
        {
            AssertNoDependency(assembly, module + ".", "Licensing works on ModuleId/FeatureId strings, not module implementations.");
            Assert.DoesNotContain(ReferencedNames(assembly), n => n.StartsWith(module + ".", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-LIC-005: Client licensing must not depend on the license server implementation")]
    public void ClientLicensing_MustNotDependOn_LicenseServer()
    {
        foreach (var assembly in new[] { Assemblies.ClientLicensing, Assemblies.ClientLicensingHttp })
        {
            AssertNoDependency(assembly, "LicenseServer", "The client talks to the server only through ILicenseClient / Licensing.Contracts.");
            Assert.DoesNotContain(ReferencedNames(assembly), n => n.StartsWith("LicenseServer", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-LIC-006: Client.Licensing.Http must not depend on EF Core, WPF or ASP.NET Core")]
    public void LicensingHttp_MustNotDependOn_EfWpfOrAspNet()
    {
        var a = Assemblies.ClientLicensingHttp;
        AssertNoDependency(a, "Microsoft.EntityFrameworkCore", "No persistence in the transport.");
        AssertNoDependency(a, "System.Windows", "No UI in the transport.");
        AssertNoDependency(a, "Microsoft.AspNetCore", "The client must not use server frameworks.");
    }

    [Fact(DisplayName = "ARCH-LIC-007: Licensing.Contracts has no HTTP, EF Core, WPF, client, server or business module dependency")]
    public void LicensingContracts_IsPureWireModel()
    {
        var a = Assemblies.LicensingContracts;
        AssertNoDependency(a, "System.Net.Http", "Wire model only.");
        AssertNoDependency(a, "Microsoft.EntityFrameworkCore", "Wire model only.");
        AssertNoDependency(a, "System.Windows", "Wire model only.");
        AssertNoDependency(a, "Client.", "Contracts sit below client and server.");
        AssertNoDependency(a, "LicenseServer", "Contracts sit below client and server.");
        foreach (var module in BusinessModulePrefixes)
            AssertNoDependency(a, module + ".", "Contracts know no business module.");
    }

    [Fact(DisplayName = "ARCH-LIC-008: License server application has no ASP.NET, HTTP, EF Core, client or infrastructure dependency")]
    public void LicenseServerApplication_IsFramework_AndClientFree()
    {
        var a = Assemblies.LicenseServerApplication;
        AssertNoDependency(a, "Microsoft.AspNetCore", "Application logic is host-agnostic.");
        AssertNoDependency(a, "System.Net.Http", "No HTTP in server logic.");
        AssertNoDependency(a, "Microsoft.EntityFrameworkCore", "No EF in server logic.");
        AssertNoDependency(a, "LicenseServer.Infrastructure", "Dependency direction is inward.");
        AssertNoDependency(a, "Client.", "The server never depends on client code.");
    }

    [Fact(DisplayName = "ARCH-LIC-009: License server must not depend on client implementation")]
    public void LicenseServer_MustNotDependOn_Client()
    {
        foreach (var assembly in Assemblies.AllLicenseServerAssemblies)
        {
            AssertNoDependency(assembly, "Client.", "The server never depends on client code.");
            Assert.DoesNotContain(ReferencedNames(assembly), n => n.StartsWith("Client.", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-LIC-010: License server must not depend on business modules or the Platform runtime")]
    public void LicenseServer_MustNotDependOn_BusinessModulesOrPlatform()
    {
        foreach (var assembly in Assemblies.AllLicenseServerAssemblies)
        {
            foreach (var module in BusinessModulePrefixes)
            {
                AssertNoDependency(assembly, module + ".", "The server never references business modules.");
                Assert.DoesNotContain(ReferencedNames(assembly), n => n.StartsWith(module + ".", StringComparison.Ordinal));
            }

            Assert.DoesNotContain(ReferencedNames(assembly), n => n.StartsWith("Platform.", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-LIC-011: Business modules must not depend on the license server")]
    public void BusinessModules_MustNotDependOn_LicenseServer()
    {
        foreach (var assembly in Assemblies.AllBusinessModuleAssemblies)
        {
            AssertNoDependency(assembly, "LicenseServer", "Modules never reference the license server.");
            Assert.DoesNotContain(ReferencedNames(assembly), n => n.StartsWith("LicenseServer", StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-LIC-012: Business modules must not depend on client licensing implementation or persistence")]
    public void BusinessModules_MustNotDependOn_ClientLicensing()
    {
        foreach (var assembly in Assemblies.AllBusinessModuleAssemblies)
        {
            AssertNoDependency(assembly, "Client.Licensing", "Modules see only ILicenseEntitlementService (Platform.Application).");
            AssertNoDependency(assembly, "Licensing.Contracts", "Modules do not handle signed licenses.");
            Assert.DoesNotContain(ReferencedNames(assembly), n => n.StartsWith("Client.Licensing", StringComparison.Ordinal));
            Assert.DoesNotContain(ReferencedNames(assembly), n => n == "Licensing.Contracts");
        }
    }

    [Fact(DisplayName = "ARCH-LIC-013: Platform must not depend on the license server or client licensing")]
    public void Platform_MustNotDependOn_LicensingImplementations()
    {
        foreach (var assembly in Assemblies.AllPlatformAssemblies)
        {
            AssertNoDependency(assembly, "LicenseServer", "Platform never depends on the server.");
            AssertNoDependency(assembly, "Client.Licensing", "Platform defines the abstraction; the client implements it.");
            AssertNoDependency(assembly, "Licensing.Contracts", "Platform stays free of licensing wire types.");
        }
    }

    [Fact(DisplayName = "ARCH-LIC-014: Platform exposes the licensing abstraction, and Client.Licensing implements it")]
    public void PlatformAbstraction_IsImplementedByClientLicensing()
    {
        var abstraction = typeof(Platform.Application.Abstractions.Licensing.ILicenseEntitlementService);
        var implementations = Assemblies.ClientLicensing.GetTypes().Where(t => abstraction.IsAssignableFrom(t) && !t.IsInterface).ToList();

        Assert.NotEmpty(implementations);
        Assert.Equal("Platform.Application", abstraction.Assembly.GetName().Name);
    }

    [Fact(DisplayName = "ARCH-LIC-015: Client assemblies contain no signing capability (private key stays on the server)")]
    public void ClientAssemblies_HaveNoSigner()
    {
        foreach (var assembly in new[] { Assemblies.ClientLicensing, Assemblies.ClientLicensingHttp, Assemblies.LicensingContracts })
        {
            Assert.DoesNotContain(assembly.GetTypes(), t => t.Name.Contains("Signer", StringComparison.OrdinalIgnoreCase));
            AssertNoDependency(assembly, "LicenseServer.Infrastructure", "Signing lives only in the server.");
        }

        Assert.Contains(Assemblies.LicenseServerInfrastructure.GetTypes(), t => t.Name == "EcdsaLicenseSigner");
    }

    [Fact(DisplayName = "ARCH-LIC-016: Client licensing must not depend on ASP.NET Core")]
    public void ClientLicensing_MustNotDependOn_AspNetCore()
        => AssertNoDependency(Assemblies.ClientLicensing, "Microsoft.AspNetCore", "Client uses abstractions, not server frameworks.");

    [Fact(DisplayName = "ARCH-LIC-017: Licensing assemblies are part of an acyclic project graph")]
    public void LicensingAssemblies_AreAcyclic()
    {
        var projectNames = Assemblies.AllProjectAssemblies.Select(a => a.GetName().Name!).ToHashSet();
        Assert.Contains("Client.Licensing", projectNames);
        Assert.Contains("Licensing.Contracts", projectNames);
        Assert.Contains("LicenseServer.Application", projectNames);

        var graph = Assemblies.AllProjectAssemblies.ToDictionary(
            a => a.GetName().Name!,
            a => a.GetReferencedAssemblies().Select(r => r.Name!).Where(projectNames.Contains).ToList());

        var visiting = new HashSet<string>();
        var done = new HashSet<string>();

        void Visit(string node, Stack<string> path)
        {
            if (done.Contains(node)) return;
            Assert.True(visiting.Add(node), "Circular dependency: " + string.Join(" -> ", path.Reverse()) + " -> " + node);
            path.Push(node);
            foreach (var next in graph[node]) Visit(next, path);
            path.Pop();
            visiting.Remove(node);
            done.Add(node);
        }

        foreach (var node in graph.Keys) Visit(node, new Stack<string>());
    }

    [Fact(DisplayName = "ARCH-LIC-018: Licensing does not reference Stage 7 update/package signing infrastructure")]
    public void Licensing_DoesNotReference_UpdaterOrPackaging()
    {
        foreach (var assembly in new[] { Assemblies.ClientLicensing, Assemblies.ClientLicensingHttp, Assemblies.LicensingContracts }
                     .Concat(Assemblies.AllLicenseServerAssemblies))
        {
            Assert.DoesNotContain(ReferencedNames(assembly), n => n.Contains("Updater", StringComparison.Ordinal)
                || n.Contains("ModulePackager", StringComparison.Ordinal)
                || n.Contains("UpdatePublisher", StringComparison.Ordinal));
        }
    }
}
