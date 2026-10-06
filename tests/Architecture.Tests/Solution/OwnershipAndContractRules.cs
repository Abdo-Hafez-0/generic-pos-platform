using System.Reflection;
using System.Text.RegularExpressions;
using Platform.Application.Abstractions.Data;
using Platform.Infrastructure.Persistence;
using Tests.Common;

namespace Architecture.Tests.Solution;

/// <summary>
/// Stage 13: contract surfaces and logical database ownership (Rule 5, Rule 11, "Architecture &amp; Solution Design" sections 18, 32-36).
/// ARCH-SOL-017 .. ARCH-SOL-022. Modules are discovered, not listed.
///
/// The physical side (tables created per prefix, no foreign key across modules) is proven on a real database by
/// Integration.Tests RealHostMigrationTests; the EF models of the real composition by Integration.Tests ArchitectureCompositionTests.
/// </summary>
public sealed class OwnershipAndContractRules
{
    private static SolutionGraph Graph => SolutionGraph.Instance;

    private static string Describe(IEnumerable<string> violations) => "\n  " + string.Join("\n  ", violations);

    private static IEnumerable<string> Files(string relativeDirectory, string pattern)
        => Directory.EnumerateFiles(Path.Combine(RepoPaths.Root(), relativeDirectory), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static bool IsMigration(string file) => file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}");

    // ------------------------------------------------------------------ contracts

    private static readonly string[] ForbiddenNamespaces =
    [
        "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "System.Data", "System.Windows", "System.Net.Http", "Microsoft.AspNetCore",
        "System.Linq.Expressions"
    ];

    /// <summary>Every type a public member of <paramref name="type"/> exposes (signatures, generic arguments, element types, base types).</summary>
    private static IEnumerable<Type> ExposedTypes(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var direct = new List<Type?> { type.BaseType };
        direct.AddRange(type.GetInterfaces());
        foreach (var method in type.GetMethods(flags))
        {
            direct.Add(method.ReturnType);
            direct.AddRange(method.GetParameters().Select(p => p.ParameterType));
        }

        foreach (var ctor in type.GetConstructors(flags)) direct.AddRange(ctor.GetParameters().Select(p => p.ParameterType));
        direct.AddRange(type.GetProperties(flags).Select(p => p.PropertyType));
        direct.AddRange(type.GetFields(flags).Select(f => f.FieldType));
        direct.AddRange(type.GetEvents(flags).Select(e => e.EventHandlerType));

        return direct.Where(t => t is not null).SelectMany(t => Unwrap(t!)).Distinct();
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.IsByRef || type.IsArray || type.IsPointer) return Unwrap(type.GetElementType()!);
        if (type.IsGenericParameter) return [];
        if (!type.IsGenericType) return [type];
        return type.GetGenericArguments().SelectMany(Unwrap).Prepend(type.GetGenericTypeDefinition());
    }

    private static IEnumerable<Assembly> ContractAssemblies => Assemblies.AllModuleAssemblies.Where(a => a.GetName().Name!.EndsWith(".Contracts", StringComparison.Ordinal));

    [Fact(DisplayName = "ARCH-SOL-017 (Rule 5): No module contract exposes EF Core, SQLite, ADO.NET, WPF, HTTP, ASP.NET, IQueryable or expression trees")]
    public void Contracts_ExposeNoInfrastructure()
    {
        var violations = new List<string>();
        foreach (var assembly in ContractAssemblies)
            foreach (var type in assembly.GetExportedTypes())
                foreach (var exposed in ExposedTypes(type))
                {
                    var ns = exposed.Namespace ?? string.Empty;
                    if (ForbiddenNamespaces.Any(f => ns == f || ns.StartsWith(f + ".", StringComparison.Ordinal))
                        || exposed == typeof(IQueryable<>) || exposed == typeof(IQueryable) || typeof(IQueryable).IsAssignableFrom(exposed))
                        violations.Add($"{type.FullName} exposes {exposed.FullName}");
                }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    [Fact(DisplayName = "ARCH-SOL-018 (Rule 5): A module contract exposes only its own contract types, Platform.Core and the base class library (no Domain, Application or Infrastructure type of any module)")]
    public void Contracts_ExposeOnlyContractTypes()
    {
        var violations = new List<string>();
        foreach (var assembly in ContractAssemblies)
            foreach (var type in assembly.GetExportedTypes())
                foreach (var exposed in ExposedTypes(type))
                {
                    var owner = exposed.Assembly;
                    var name = owner.GetName().Name!;
                    var allowed = owner == assembly || owner == Assemblies.PlatformCore || name is "System.Private.CoreLib" or "System.Runtime" or "netstandard"
                                  || name.StartsWith("System.", StringComparison.Ordinal) && !ForbiddenNamespaces.Any(f => (exposed.Namespace ?? "").StartsWith(f, StringComparison.Ordinal));
                    if (!allowed) violations.Add($"{type.FullName} exposes {exposed.FullName} from {name}");
                }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    // ------------------------------------------------------------------ Rule 11: a module never modifies another module's tables

    private static readonly Regex TableInMigration = new(@"\b(?:table|principalTable|newName|name)\s*:\s*""(?<table>[A-Za-z]+_[A-Za-z0-9_]+)""");
    private static readonly Regex CreatedTable = new(@"CreateTable\(\s*name\s*:\s*""(?<prefix>[a-z]+)_");

    /// <summary>The table prefix each module's migrations create (module folder -> prefix). Reporting owns no tables.</summary>
    private static Dictionary<string, string> OwnedPrefixes()
    {
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var module in Graph.Modules)
        {
            var created = Files(Path.Combine("src", "Modules", module), "*.cs").Where(IsMigration).Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
                .SelectMany(f => CreatedTable.Matches(File.ReadAllText(f)).Select(m => m.Groups["prefix"].Value)).Distinct().ToList();
            Assert.True(created.Count <= 1, $"{module}'s migrations create tables under several prefixes: {string.Join(", ", created)}");
            if (created.Count == 1) prefixes[module] = created[0] + "_";
        }

        return prefixes;
    }

    [Fact(DisplayName = "ARCH-SOL-019 (Rule 11): Each module's migrations create, alter and reference only tables under its own unique prefix (no cross-module foreign key)")]
    public void Migrations_TouchOnlyTheModulesOwnTables()
    {
        var prefixes = OwnedPrefixes();
        Assert.Equal(prefixes.Count, prefixes.Values.Distinct().Count());
        Assert.True(prefixes.Count >= 12, "the module table prefixes were not discovered");

        var violations = new List<string>();
        foreach (var (module, prefix) in prefixes)
            foreach (var file in Files(Path.Combine("src", "Modules", module), "*.cs").Where(IsMigration).Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal) && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)))
                foreach (Match match in TableInMigration.Matches(File.ReadAllText(file)))
                {
                    var table = match.Groups["table"].Value;
                    if (table.StartsWith("IX_", StringComparison.Ordinal) || table.StartsWith("FK_", StringComparison.Ordinal) || table.StartsWith("PK_", StringComparison.Ordinal) || table.StartsWith("UX_", StringComparison.Ordinal)) continue;
                    if (!table.StartsWith(prefix, StringComparison.Ordinal)) violations.Add($"{Path.GetFileName(file)} ({module}) touches {table}");
                }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    [Fact(DisplayName = "ARCH-SOL-020 (Rule 11): No module's code names another module's table (no raw SQL or mapping onto foreign tables)")]
    public void ModuleCode_NamesNoForeignTable()
    {
        var prefixes = OwnedPrefixes();
        var violations = new List<string>();
        foreach (var module in Graph.Modules)
        {
            var foreign = prefixes.Where(p => p.Key != module).Select(p => p.Value).ToList();
            var pattern = new Regex("\"[^\"]*\\b(" + string.Join("|", foreign.Select(Regex.Escape)) + ")[A-Z][A-Za-z]*");
            foreach (var file in Files(Path.Combine("src", "Modules", module), "*.cs").Where(f => !IsMigration(f)))
                foreach (Match match in pattern.Matches(File.ReadAllText(file)))
                    violations.Add($"{Path.GetFileName(file)} ({module}): {match.Value}");
        }

        Assert.True(violations.Count == 0, Describe(violations));
    }

    [Fact(DisplayName = "ARCH-SOL-021 (Rule 11): Outside the modules nothing issues SQL against business tables (the updater only copies the whole file for a restore point; migrations are module-owned)")]
    public void ClientsAndPlatform_IssueNoBusinessSql()
    {
        var sql = new Regex(@"""\s*(INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM|CREATE\s+TABLE|ALTER\s+TABLE|DROP\s+TABLE)\b", RegexOptions.IgnoreCase);
        var files = Files(Path.Combine("src", "Client"), "*.cs").Concat(Files(Path.Combine("src", "Platform"), "*.cs")).Where(f => !IsMigration(f));
        Assert.Empty(files.Where(f => sql.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName));
    }

    // ------------------------------------------------------------------ the Stage 12 shared transaction does not bypass ownership

    [Fact(DisplayName = "ARCH-SOL-022 (Rules 5, 11): The shared transaction coordinates without exposing a DbContext or a connection; modules join it only by registering their own context")]
    public void SharedTransaction_ExposesNoPersistence()
    {
        // what a handler sees: "run this as one transaction" - a Result-returning delegate, nothing to read or write tables with
        var exposedByAbstraction = ExposedTypes(typeof(IAtomicOperation)).Select(t => t.Namespace ?? string.Empty);
        Assert.DoesNotContain(exposedByAbstraction, ns => ForbiddenNamespaces.Any(f => ns.StartsWith(f, StringComparison.Ordinal)));

        // the implementation's public surface exposes no connection and no context (GetConnection and NoteWriter are internal)
        var exposedByScope = ExposedTypes(typeof(SharedDatabaseScope)).Where(t => t != typeof(object));
        Assert.DoesNotContain(exposedByScope, t => t.Namespace?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true || typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(t));

        // modules name the scope nowhere; they take part only through UseSharedSqlite on their OWN context (ARCH-RES-003)
        var violations = Files(Path.Combine("src", "Modules"), "*.cs").Where(f => File.ReadAllText(f).Contains("SharedDatabaseScope", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
        Assert.Empty(violations);

        // and only Application-layer use cases open the transaction (section 35: the boundary is the use case)
        var users = Assemblies.AllModuleAssemblies.SelectMany(a => a.GetTypes())
            .Where(t => t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAtomicOperation))))
            .ToList();
        Assert.NotEmpty(users);
        Assert.All(users, t => Assert.EndsWith(".Application", t.Assembly.GetName().Name!));
    }
}
