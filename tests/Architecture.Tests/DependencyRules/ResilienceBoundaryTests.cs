using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Platform.Application.Abstractions.Data;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for Stage 12 (offline and failure behavior). ARCH-RES-001 .. ARCH-RES-008.
///
/// Shape being protected:
///   IAtomicOperation (Platform.Application)      : "this multi-module operation is one transaction" - no database technology in the abstraction
///   SharedDatabaseScope (Platform.Infrastructure): the only place that knows how the shared SQLite transaction works
///   Business modules                             : register their context through UseSharedSqlite, orchestrate through IAtomicOperation
///   Devices and network                          : never inside the transaction (they cannot be rolled back), never required by business logic
/// </summary>
public sealed class ResilienceBoundaryTests
{
    private static IReadOnlyList<Assembly> ApplicationAssemblies =>
    [
        Assemblies.CatalogApplication, Assemblies.InventoryApplication, Assemblies.SalesApplication, Assemblies.POSApplication,
        Assemblies.CustomersApplication, Assemblies.SuppliersApplication, Assemblies.PurchasingApplication, Assemblies.PricingApplication,
        Assemblies.PaymentsApplication, Assemblies.UsersApplication, Assemblies.AuditApplication, Assemblies.CashManagementApplication,
        Assemblies.ReportingApplication
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GenericPOS.sln")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("GenericPOS.sln was not found above the test binaries.");
    }

    private static IEnumerable<string> Files(string relativeDirectory, string pattern)
        => Directory.EnumerateFiles(Path.Combine(RepoRoot(), relativeDirectory), pattern, SearchOption.AllDirectories)
                    .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // ------------------------------------------------------------------ the abstraction

    [Fact(DisplayName = "ARCH-RES-001: IAtomicOperation lives in Platform.Application and the Platform abstractions know no database technology")]
    public void AtomicOperation_IsATechnologyIndependentPlatformAbstraction()
    {
        Assert.True(typeof(IAtomicOperation).IsInterface);
        Assert.Equal(Assemblies.PlatformApplication, typeof(IAtomicOperation).Assembly);

        foreach (var forbidden in new[] { "Microsoft.EntityFrameworkCore", "Microsoft.Data.Sqlite", "System.Data" })
        {
            var result = Types.InAssembly(Assemblies.PlatformApplication).Should().NotHaveDependencyOn(forbidden).GetResult();
            Assert.True(result.IsSuccessful, $"Platform.Application must not depend on {forbidden}: " + string.Join(", ", result.FailingTypeNames ?? []));
        }
    }

    [Fact(DisplayName = "ARCH-RES-002: Only Platform.Infrastructure implements the shared transaction (no module touches the connection, BEGIN or COMMIT)")]
    public void TheSharedTransaction_IsImplementedOnlyInPlatformInfrastructure()
    {
        Assert.Equal(Assemblies.PlatformInfrastructure, typeof(Platform.Infrastructure.Persistence.SharedDatabaseScope).Assembly);

        var statements = new Regex(@"""\s*(BEGIN|COMMIT|ROLLBACK|SAVEPOINT)\b", RegexOptions.IgnoreCase);
        foreach (var file in Files("src/Modules", "*.cs").Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            Assert.False(statements.IsMatch(text), $"{file} issues a transaction statement itself; use IAtomicOperation.");
            Assert.DoesNotContain(".BeginTransaction", text);
            Assert.DoesNotContain("TransactionScope", text);
        }
    }

    // ------------------------------------------------------------------ who takes part

    private static readonly string[] ParticipatingModules = ["Catalog", "Inventory", "Sales", "POS", "Payments", "Purchasing", "Pricing", "CashManagement", "Customers", "Suppliers"];

    // Users and Audit deliberately stay on their own connection: an audit record or a failed sign-in must survive the rollback of the business operation it describes.
    private static readonly string[] IndependentModules = ["Users", "Audit"];

    [Fact(DisplayName = "ARCH-RES-003: Every business module that persists data registers its context through UseSharedSqlite; Users and Audit are the documented exceptions")]
    public void BusinessContexts_TakePartInTheSharedTransaction()
    {
        foreach (var module in ParticipatingModules)
        {
            var file = Files($"src/Modules/{module}/{module}.Infrastructure/DependencyInjection", "*ServicesExtensions.cs").Single();
            var text = File.ReadAllText(file);
            Assert.Contains("UseSharedSqlite(", text);
            Assert.DoesNotContain(".UseSqlite(", text);
        }

        foreach (var module in IndependentModules)
        {
            var text = File.ReadAllText(Files($"src/Modules/{module}/{module}.Infrastructure/DependencyInjection", "*ServicesExtensions.cs").Single());
            Assert.Contains(".UseSqlite(", text);
            Assert.DoesNotContain("UseSharedSqlite(", text);
        }

        // a new persistent module has to be classified here on purpose
        var withContexts = Directory.EnumerateDirectories(Path.Combine(RepoRoot(), "src", "Modules"))
            .Where(d => Directory.EnumerateFiles(d, "*DbContext.cs", SearchOption.AllDirectories).Any(f => !f.Contains("Migrations")))
            .Select(d => Path.GetFileName(d)!)
            .ToList();
        Assert.Empty(withContexts.Except(ParticipatingModules).Except(IndependentModules));
    }

    [Fact(DisplayName = "ARCH-RES-004: A handler that writes through the Inventory, Sales, Payments or CashManagement contracts is one transaction (it takes IAtomicOperation)")]
    public void MultiModuleWriteOrchestrations_TakeAnAtomicOperation()
    {
        var writeContracts = new[]
        {
            typeof(Inventory.Contracts.Interfaces.IStockIssueService),
            typeof(Inventory.Contracts.Interfaces.IStockReceiptService),
            typeof(Sales.Contracts.Interfaces.ISalesService),
            typeof(Payments.Contracts.Interfaces.IPaymentService),
            typeof(CashManagement.Contracts.Interfaces.ICashMovementRecorder)   // FIX-04: the cash sale goes into the drawer in the checkout transaction
        };

        var orchestrators = new List<Type>();
        foreach (var assembly in ApplicationAssemblies)
            foreach (var type in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract))
                if (type.GetConstructors().Any(c => c.GetParameters().Any(p => writeContracts.Contains(p.ParameterType))))
                    orchestrators.Add(type);

        Assert.Contains(typeof(POS.Application.Commands.CheckoutCartCommandHandler), orchestrators);
        Assert.Contains(typeof(Purchasing.Application.Commands.ReceivePurchaseOrderCommandHandler), orchestrators);
        foreach (var type in orchestrators)
            Assert.True(type.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAtomicOperation))),
                $"{type.FullName} writes through other modules' contracts but does not run as one transaction (IAtomicOperation).");
    }

    // ------------------------------------------------------------------ what must stay outside the transaction

    [Fact(DisplayName = "ARCH-RES-005: The checkout transaction contains no device call (a receipt cannot be rolled back, so it is printed after the commit)")]
    public void Checkout_TouchesNoDeviceInsideTheTransaction()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Modules", "POS", "POS.Application", "Commands", "CheckoutCartCommand.cs"));
        var start = text.IndexOf("private async Task<Result<Committed>> CommitSaleAsync", StringComparison.Ordinal);
        var end = text.IndexOf("RunPeripheralsAsync(", start, StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);

        // the commit step ends at the first peripheral call; everything between its start and the transaction call must be free of devices
        var transactional = text[start..text.IndexOf("private async Task<IReadOnlyList<POSHardwareNotice>> RunPeripheralsAsync", start, StringComparison.Ordinal)];
        foreach (var device in new[] { "receiptPrinter", "cashDrawer", "labelPrinter", "IReceiptPrinter", "ICashDrawer", "HardwareGuard" })
            Assert.DoesNotContain(device, transactional);
    }

    [Fact(DisplayName = "ARCH-RES-006: Business modules do not reference the cloud transports or any server assembly (the cloud cannot be a requirement of local work)")]
    public void BusinessModules_DoNotReferenceCloudTransports()
    {
        string[] forbidden = ["Client.Licensing.Http", "Client.Updater.Http", "Cloud.", "AdminPortal", "BackupServer", "LicenseServer", "UpdateServer", "Microsoft.Extensions.Http"];
        foreach (var project in Files("src/Modules", "*.csproj"))
        {
            var text = File.ReadAllText(project);
            foreach (var name in forbidden)
                Assert.False(text.Contains(name, StringComparison.Ordinal), $"{project} references {name}.");
        }
    }

    [Fact(DisplayName = "ARCH-RES-007: Production code simulates no failure (no random failure, no failure-injection switch outside tests)")]
    public void ProductionCode_ContainsNoFailureSimulation()
    {
        var forbidden = new Regex(@"new\s+Random\s*\(|Random\.Shared|FailureInjection|InjectFailure|SimulateFailure|FailNext|ChaosMonkey|Interrupt(Commit|Next)");
        foreach (var file in Files("src", "*.cs").Where(f => !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
            Assert.False(forbidden.IsMatch(File.ReadAllText(file)), $"{file} contains failure-simulation code; failure injection belongs in the tests.");
    }

    [Fact(DisplayName = "ARCH-RES-008: The database wait is bounded (a configured busy timeout can never be zero, which the driver treats as wait forever)")]
    public void TheDatabaseWait_IsAlwaysBounded()
    {
        var withoutLimit = new Platform.Infrastructure.Persistence.DatabaseOptions { DatabaseFolder = "Custom", CustomFolderPath = Path.GetTempPath(), BusyTimeoutSeconds = 0 };
        var huge = new Platform.Infrastructure.Persistence.DatabaseOptions { DatabaseFolder = "Custom", CustomFolderPath = Path.GetTempPath(), BusyTimeoutSeconds = 100000 };

        Assert.EndsWith("Default Timeout=1", withoutLimit.BuildConnectionString());
        Assert.EndsWith("Default Timeout=600", huge.BuildConnectionString());
    }
}
