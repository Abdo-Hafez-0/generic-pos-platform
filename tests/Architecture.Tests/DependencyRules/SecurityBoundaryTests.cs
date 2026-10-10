using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Platform.Application.Abstractions.Authorization;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for Stage 11 (security hardening). ARCH-SEC-001 .. ARCH-SEC-016.
///
/// Shape being protected:
///   Platform.Application (Authorization, Security)  : technology-independent abstractions - who is signed in, may they do this, what happened, protect this secret
///   Business modules                                : declare capabilities, enforce them in their handlers, never know a concrete security technology
///   Client.Security / Client.Licensing / Updater    : the concrete, replaceable implementations (DPAPI, signed licenses, signed packages)
///   Domain                                          : knows nothing about sessions, claims, hashing or licensing
///   Sources and configuration                       : no private keys, no hard-coded credentials, no switch that turns security off
/// </summary>
public sealed class SecurityBoundaryTests
{
    private static IReadOnlyList<Assembly> ModuleAssemblies =>
    [
        .. Assemblies.AllCatalogAssemblies, .. Assemblies.AllInventoryAssemblies, .. Assemblies.AllSalesAssemblies, .. Assemblies.AllPOSAssemblies,
        .. Assemblies.AllCustomersAssemblies, .. Assemblies.AllSuppliersAssemblies, .. Assemblies.AllPurchasingAssemblies, .. Assemblies.AllPricingAssemblies,
        .. Assemblies.AllPaymentsAssemblies, .. Assemblies.AllUsersAssemblies, .. Assemblies.AllAuditAssemblies, .. Assemblies.AllCashManagementAssemblies,
        .. Assemblies.AllReportingAssemblies
    ];

    private static IEnumerable<Assembly> Layer(string suffix) => ModuleAssemblies.Where(a => a.GetName().Name!.EndsWith("." + suffix, StringComparison.Ordinal));

    private static IEnumerable<string> Refs(Assembly assembly) => assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static void AssertNoDependency(Assembly assembly, string forbidden, string because)
    {
        var result = Types.InAssembly(assembly).Should().NotHaveDependencyOn(forbidden).GetResult();
        Assert.True(result.IsSuccessful,
            $"{assembly.GetName().Name} must not depend on {forbidden}. {because} Failing types: " + string.Join(", ", result.FailingTypeNames ?? []));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("GenericPOS.sln was not found above the test binaries.");
    }

    private static IEnumerable<string> Files(string relativeDirectory, params string[] patterns)
        => patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepoRoot(), relativeDirectory), p, SearchOption.AllDirectories))
                   .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static readonly Assembly PlatformApplication = Assemblies.PlatformApplication;

    // ------------------------------------------------------------------ abstractions stay technology-independent

    [Fact(DisplayName = "ARCH-SEC-001: The security abstractions in Platform.Application depend on no technology (ASP.NET, WPF, EF Core, HTTP, claims, DPAPI, modules, clients)")]
    public void SecurityAbstractions_AreTechnologyIndependent()
    {
        string[] forbidden =
        [
            "Microsoft.AspNetCore", "System.Windows", "Microsoft.EntityFrameworkCore", "System.Net.Http", "System.Security.Claims", "System.Security.Principal",
            "System.Security.Cryptography.ProtectedData", "Client.", "Catalog.", "Inventory.", "Sales.", "POS.", "Users.", "Audit.", "Licensing."
        ];

        foreach (var ns in new[] { typeof(IAuthorizationService).Namespace!, "Platform.Application.Abstractions.Security" })
            foreach (var name in forbidden)
            {
                var result = Types.InAssembly(PlatformApplication).That().ResideInNamespace(ns).Should().NotHaveDependencyOn(name).GetResult();
                Assert.True(result.IsSuccessful, $"{ns} must not depend on {name}. Failing: " + string.Join(", ", result.FailingTypeNames ?? []));
            }
    }

    [Fact(DisplayName = "ARCH-SEC-002: Domain assemblies know nothing about sessions, authorization, claims, ASP.NET or licensing")]
    public void Domains_AreFreeOfSecurityInfrastructure()
    {
        string[] forbidden = ["Platform.Application", "System.Security.Claims", "System.Security.Principal", "Microsoft.AspNetCore", "Client.", "Licensing."];

        foreach (var domain in Layer("Domain"))
            foreach (var name in forbidden)
                AssertNoDependency(domain, name, "Domain is authorization-agnostic: the application layer mediates security, never a domain entity.");
    }

    [Fact(DisplayName = "ARCH-SEC-003: No business module references a concrete security implementation (Client.Security, licensing, updater, desktop, signing, DPAPI)")]
    public void BusinessModules_DoNotReferenceConcreteSecurity()
    {
        string[] forbidden = ["Client.Security", "Client.Licensing", "Client.Updater", "Client.Desktop", "Security.Es256", "System.Security.Cryptography.ProtectedData", "Cloud.", "AdminPortal", "BackupServer", "LicenseServer", "UpdateServer"];

        foreach (var assembly in ModuleAssemblies)
            foreach (var name in forbidden)
                Assert.DoesNotContain(Refs(assembly), r => r.StartsWith(name, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-SEC-004: No business module project file references Client.Desktop, Client.Security, Client.Licensing or Client.Updater")]
    public void ModuleProjectFiles_DoNotReferenceClients()
    {
        string[] forbidden = ["Client.Desktop", "Client.Security", "Client.Licensing", "Client.Updater", "Client.Hardware"];

        foreach (var project in Files("src/Modules", "*.csproj"))
        {
            var text = File.ReadAllText(project);
            foreach (var name in forbidden)
                Assert.False(text.Contains($"\\{name}\\", StringComparison.Ordinal) || text.Contains($"/{name}/", StringComparison.Ordinal),
                    $"{project} references {name}.");
        }
    }

    [Fact(DisplayName = "ARCH-SEC-005: License entitlements are enforced in one place - no business module touches ILicenseEntitlementService")]
    public void BusinessModules_DoNotScatterLicenseChecks()
    {
        foreach (var assembly in ModuleAssemblies)
            AssertNoDependency(assembly, "Platform.Application.Abstractions.Licensing", "License enforcement is centralized in AuthorizationService; modules declare capabilities instead.");
    }

    // ------------------------------------------------------------------ authorization is actually enforced at the application boundary

    private static readonly Dictionary<string, string> UnguardedCommandHandlers = new()
    {
        ["CreateSaleCommandHandler"] = "Sales workflow: only reachable through ISalesService inside the POS checkout, which is authorized as pos.sale.create.",
        ["AddSaleItemCommandHandler"] = "Sales workflow (see above).",
        ["ConfirmSaleCommandHandler"] = "Sales workflow (see above).",
        ["CompleteSaleCommandHandler"] = "Sales workflow (see above).",
        ["CancelSaleCommandHandler"] = "Sales workflow, including checkout's own compensation (see above).",
        ["IssueStockCommandHandler"] = "Only reachable through IStockIssueService inside the POS checkout.",
        ["RecordAuditEntryCommandHandler"] = "Modules record to the append-only audit log on their own account through IAuditRecorder.",
        ["SignInCommandHandler"] = "Authentication itself: it happens BEFORE anyone is signed in; protected by lockout instead.",
        ["SignOutCommandHandler"] = "Ending your own session needs no permission.",
        ["ChangePasswordCommandHandler"] = "Proves the current password itself (same lockout as sign-in).",
        ["BootstrapAdministratorCommandHandler"] = "First-run setup: only works while no user exists at all."
    };

    private static readonly string[] ProtectedQueryHandlers =
    [
        "GetUserQueryHandler", "ListUsersQueryHandler", "GetRoleQueryHandler", "ListRolesQueryHandler",
        "GetCustomerByIdQueryHandler", "ListCustomersQueryHandler", "SearchCustomersQueryHandler",
        "GetAuditEntryQueryHandler", "QueryAuditEntriesQueryHandler",
        "GetSalesReportQueryHandler", "GetInventorySnapshotQueryHandler", "GetPurchasingOverviewQueryHandler",
        "GetCustomerSummaryQueryHandler", "GetSupplierSummaryQueryHandler", "GetBusinessOverviewQueryHandler"
    ];

    private static bool TakesAuthorization(Type type)
        => type.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAuthorizationService)));

    [Fact(DisplayName = "ARCH-SEC-006: Every business command handler authorizes before acting, unless it is on the documented list of exceptions")]
    public void EveryCommandHandler_RequiresAuthorization()
    {
        var offenders = new List<string>();
        var seenExempt = new HashSet<string>();

        foreach (var assembly in Layer("Application"))
            foreach (var type in assembly.GetTypes().Where(t => t.IsClass && t.IsPublic && t.Name.EndsWith("CommandHandler", StringComparison.Ordinal)))
            {
                if (UnguardedCommandHandlers.ContainsKey(type.Name))
                {
                    seenExempt.Add(type.Name);
                    continue;
                }

                if (!TakesAuthorization(type)) offenders.Add(type.FullName!);
            }

        Assert.True(offenders.Count == 0, "These command handlers do not take IAuthorizationService (add the capability check or document the exception): " + string.Join(", ", offenders));
        Assert.Equal(UnguardedCommandHandlers.Keys.OrderBy(k => k), seenExempt.OrderBy(k => k)); // the exception list must not rot
    }

    [Fact(DisplayName = "ARCH-SEC-007: The queries that expose users, the audit trail and business reports require authorization")]
    public void SensitiveQueryHandlers_RequireAuthorization()
    {
        var found = Layer("Application").SelectMany(a => a.GetTypes()).Where(t => t.IsClass && t.IsPublic && ProtectedQueryHandlers.Contains(t.Name)).ToList();

        Assert.Equal(ProtectedQueryHandlers.Length, found.Count);
        Assert.All(found, t => Assert.True(TakesAuthorization(t), $"{t.FullName} must take IAuthorizationService."));
    }

    [Fact(DisplayName = "ARCH-SEC-008: Every capability provider builds a valid catalog (valid unique codes, an owning module) and is registered by its module")]
    public void CapabilityProviders_BuildAValidCatalog_AndAreRegistered()
    {
        var providers = new List<ICapabilityProvider>();
        foreach (var assembly in Layer("Application").Concat([Assemblies.ClientLicensing, Assemblies.ClientUpdater, Assemblies.ClientBackup]))
            foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICapabilityProvider).IsAssignableFrom(t)))
                providers.Add((ICapabilityProvider)Activator.CreateInstance(type)!);

        var catalog = new CapabilityCatalog(providers); // throws on a duplicate, malformed or ownerless capability

        Assert.True(catalog.All.Count >= 30, $"expected the platform's capabilities to be declared; found {catalog.All.Count}");
        Assert.Contains(catalog.All, c => c.Code == "pos.drawer.open" && c.IsSensitive);

        var registrationSources = string.Join("\n", Files("src", "*.cs").Where(f => f.Contains("DependencyInjection") || f.Contains("Hosting") || f.Contains("Infrastructure")).Select(File.ReadAllText));
        foreach (var provider in providers)
            Assert.Contains(provider.GetType().Name, registrationSources);
    }

    [Fact(DisplayName = "ARCH-SEC-009: Modules never expose passwords, hashes, tokens or keys through contracts or application DTOs")]
    public void ModulesNeverExposeSecrets()
    {
        string[] secretish = ["Password", "Hash", "Secret", "PrivateKey", "Token", "Salt"];

        var types = ModuleAssemblies.Where(a => a.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal))
            .SelectMany(a => a.GetExportedTypes())
            .Concat(Layer("Application").SelectMany(a => a.GetExportedTypes()).Where(t => t.Namespace is { } ns && ns.EndsWith(".DTOs", StringComparison.Ordinal)));

        foreach (var type in types)
            foreach (var property in type.GetProperties())
                Assert.False(secretish.Any(s => property.Name.Contains(s, StringComparison.OrdinalIgnoreCase)),
                    $"{type.FullName}.{property.Name} looks like secret material in a contract or DTO.");
    }

    // ------------------------------------------------------------------ separation of concerns

    [Fact(DisplayName = "ARCH-SEC-010: Client.Security only protects secrets: no business module, EF Core, HTTP, WPF or server dependency")]
    public void ClientSecurity_IsJustDataProtection()
    {
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "System.Net.Http", "System.Windows", "Catalog.", "Inventory.", "Sales.", "POS.", "Users.", "Audit.", "Cloud.", "AdminPortal", "BackupServer", "LicenseServer", "UpdateServer", "Client.Licensing", "Client.Updater", "Client.Hardware"];

        foreach (var name in forbidden)
        {
            AssertNoDependency(Assemblies.ClientSecurity, name, "Client.Security implements ISecretProtector and nothing else.");
            Assert.DoesNotContain(Refs(Assemblies.ClientSecurity), r => r.StartsWith(name, StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-SEC-011: Client.Hardware stays isolated from security (no authorization, session, protector, licensing or user dependency)")]
    public void ClientHardware_HasNoSecurityDependencies()
    {
        string[] forbidden = ["Platform.Application.Abstractions.Authorization", "Platform.Application.Abstractions.Security", "Platform.Application.Abstractions.Licensing", "Client.Security", "Client.Licensing", "Users.", "Audit."];

        foreach (var name in forbidden)
            AssertNoDependency(Assemblies.ClientHardware, name, "Hardware adapters carry no security logic: authorization happens in the POS handlers before a device is touched.");
    }

    [Fact(DisplayName = "ARCH-SEC-012: Server projects stay separate from the desktop's security (no Client.Security, no Platform.Application) and Cloud.Hosting has no persistence")]
    public void ServerProjects_AreSeparatedFromDesktopSecurity()
    {
        foreach (var project in Files("src/Cloud", "*.csproj"))
        {
            var text = File.ReadAllText(project);
            Assert.False(text.Contains("Client.Security", StringComparison.Ordinal) || text.Contains("Platform.Application", StringComparison.Ordinal),
                $"{project} references desktop security or the platform application layer.");
        }

        var hosting = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Cloud", "Cloud.Hosting", "Cloud.Hosting.csproj"));
        Assert.DoesNotContain("EntityFramework", hosting);
        Assert.DoesNotContain("Cloud.Infrastructure", hosting);
    }

    // ------------------------------------------------------------------ nothing secret in the sources, no switch to turn security off

    [Fact(DisplayName = "ARCH-SEC-013: No private key or key file exists anywhere in the source tree")]
    public void NoPrivateKeys_AnywhereInSources()
    {
        string[] markers = ["BEGIN PRIVATE KEY", "BEGIN EC PRIVATE KEY", "BEGIN RSA PRIVATE KEY", "BEGIN ENCRYPTED PRIVATE KEY", "BEGIN OPENSSH PRIVATE KEY"];

        foreach (var file in Files("src", "*.cs", "*.json", "*.xml", "*.config", "*.pem", "*.txt", "*.md", "*.xaml").Concat(Files("tools", "*.cs", "*.json", "*.xml", "*.pem")))
        {
            var text = File.ReadAllText(file);
            foreach (var marker in markers)
                Assert.False(text.Contains(marker, StringComparison.Ordinal), $"{file} contains '{marker}'.");
        }

        Assert.Empty(Files("src", "*.pem", "*.pfx", "*.key", "*.p12", "*.snk"));
        Assert.Empty(Files("tools", "*.pem", "*.pfx", "*.key", "*.p12", "*.snk"));
    }

    [Fact(DisplayName = "ARCH-SEC-014: No hard-coded credential in sources or production configuration (passwords, tokens, keys, activation keys)")]
    public void NoHardcodedCredentials()
    {
        // the NAME must end with the credential word, so error-code constants such as PasswordChangeRequiredCode are not credentials
        var assignment = new Regex("""(?i)\b\w*(?:password|passwd|pwd|secret|apikey|api_key|accesstoken|bearertoken)\s*=\s*"[^"{}\s]{6,}"(?!\s*\+)""");
        var platformToken = new Regex("gp[ab]_[A-Za-z0-9_\\-]{20,}");
        var activationKey = new Regex("\\b[A-HJ-NP-Z2-9]{5}(?:-[A-HJ-NP-Z2-9]{5}){4}\\b");

        foreach (var file in Files("src", "*.cs").Concat(Files("tools", "*.cs")))
        {
            var text = File.ReadAllText(file);
            Assert.False(platformToken.IsMatch(text), $"{file} contains a literal admin key or backup token.");
            Assert.False(activationKey.IsMatch(text), $"{file} contains a literal activation key.");
            Assert.False(assignment.IsMatch(text), $"{file} assigns a literal to a credential-like name: {assignment.Match(text).Value}");
        }

        string[] secretNames = ["password", "secret", "token", "apikey", "privatekey", "sha256", "signingkey"];
        foreach (var file in Files("src", "appsettings*.json").Where(f => !Path.GetFileName(f).Contains("Development", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "\"(?<key>[^\"]+)\"\\s*:\\s*\"(?<value>[^\"]+)\""))
            {
                var key = m.Groups["key"].Value.ToLowerInvariant();
                if (secretNames.Any(key.Contains) && !key.Contains("pempath") && !key.Contains("trusted") && !key.Contains("publickey"))
                    Assert.Fail($"{file}: '{m.Groups["key"].Value}' has a literal value in production configuration.");
            }
    }

    [Fact(DisplayName = "ARCH-SEC-015: No configuration key or setting exists that turns authorization, authentication or license enforcement off")]
    public void NoSwitchTurnsSecurityOff()
    {
        string[] switches = ["DisableLicense", "DisableAuthorization", "DisableAuthentication", "SkipAuthorization", "SkipAuthentication", "BypassAuth", "AllowAnonymousAccess", "LicenseEnforcement:", "EnforceLicense", "Licensing:Enforcement"];

        foreach (var file in Files("src", "*.cs", "appsettings*.json"))
        {
            var text = File.ReadAllText(file);
            foreach (var name in switches)
                Assert.False(text.Contains(name, StringComparison.OrdinalIgnoreCase), $"{file} mentions '{name}': security cannot be switched off by configuration.");
        }
    }

    [Fact(DisplayName = "ARCH-SEC-016: The desktop composition root registers the security modules (data protection, licensing, updater, users, audit)")]
    public void DesktopRegistersTheSecurityModules()
    {
        // FIX-01a: the module list moved from App.xaml.cs to DesktopComposition.cs (App builds the host from it).
        var composition = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "DesktopComposition.cs"));

        foreach (var module in new[] { "ClientSecurityHostingModule", "LicensingHostingModule", "UpdaterHostingModule", "UsersHostingModule", "AuditHostingModule" })
            Assert.Contains($"new {module}()", composition);
    }

    [Fact(DisplayName = "ARCH-SEC-017: The desktop shows the sign-in window before the shell and exits when nobody signs in; the window is glue over InteractiveSignInService")]
    public void DesktopRequiresSignInBeforeTheShell()
    {
        // FIX-13b: App asks DesktopSession for the start screen and only then for the shell (opened in the user's language)
        var app = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "App.xaml.cs"));
        var signIn = app.IndexOf("session.ShowSignIn(", StringComparison.Ordinal);
        var shell = app.IndexOf("session.OpenShellAsync()", StringComparison.Ordinal);

        Assert.True(signIn >= 0 && shell > signIn, "App must show the start screen before the shell.");
        Assert.Contains("Shutdown(", app[signIn..shell]);

        var session = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "Shell", "DesktopSession.cs"));
        var showSignIn = session[session.IndexOf("public bool ShowSignIn", StringComparison.Ordinal)..session.IndexOf("public async Task OpenShellAsync", StringComparison.Ordinal)];
        Assert.Contains("GetRequiredService<SignInWindow>()", showSignIn);
        Assert.Contains("ShowDialog() == true", showSignIn);
        Assert.DoesNotContain("MainWindow", showSignIn);
        // after a sign-out nobody reaches the shell again without signing in; closing the start screen ends the application
        var signOut = session[session.IndexOf("public async Task SignOutAsync", StringComparison.Ordinal)..];
        Assert.True(signOut.IndexOf("ShowSignIn()", StringComparison.Ordinal) is var again and > 0
            && signOut.IndexOf("Shutdown()", StringComparison.Ordinal) > again && signOut.IndexOf("OpenShellAsync()", StringComparison.Ordinal) > again);

        // the window decides nothing itself: no handler, repository or hasher is used directly
        var window = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Client", "Client.Desktop", "SignInWindow.xaml.cs"));
        Assert.Contains("InteractiveSignInService", window);
        foreach (var forbidden in new[] { "CommandHandler", "Repository", "IPasswordHasher", "ISessionManager", "DbContext" })
            Assert.DoesNotContain(forbidden, window);
    }
}
