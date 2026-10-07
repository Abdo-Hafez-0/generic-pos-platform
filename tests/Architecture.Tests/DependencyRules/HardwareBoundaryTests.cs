using System.Reflection;
using NetArchTest.Rules;
using Platform.Application.Abstractions.Hardware;

namespace Architecture.Tests.DependencyRules;

/// <summary>
/// Architecture tests for Stage 10 (hardware and device integration). ARCH-HW-001 .. ARCH-HW-014.
///
/// Shape being protected:
///   Platform.Application.Abstractions.Hardware : IBarcodeScanner, IReceiptPrinter, ILabelPrinter, ICashDrawer, IScale (vendor-neutral, business vocabulary)
///   Client.Hardware                            : the ONLY place with protocols (ESC/POS, ZPL), transports and keyboard-wedge decoding
///   POS.Application / POS.Infrastructure       : the only business consumers; they see abstractions, never adapters; hardware is OPTIONAL
///   Domain, Contracts, UI, other modules       : no device technology, no concrete hardware, no direct construction of drivers
/// </summary>
public sealed class HardwareBoundaryTests
{
    private static readonly string Ns = typeof(IHardwareDevice).Namespace!;

    // Device technologies that business code must never touch.
    private static readonly string[] DeviceTechnology = ["System.IO.Ports", "System.Net.Sockets", "System.Windows", "System.Device", "Windows.Devices", "Microsoft.Win32", "System.Management"];

    private static readonly string[] ServerPrefixes = ["Cloud.", "AdminPortal", "BackupServer", "LicenseServer", "UpdateServer"];

    private static readonly string[] ConcreteAdapterTypes =
    [
        "EscPosReceiptPrinter", "EscPosCashDrawer", "ZplLabelPrinter", "KeyboardWedgeBarcodeScanner", "TcpDeviceTransport", "FileDeviceTransport",
        "NullReceiptPrinter", "NullCashDrawer", "NullLabelPrinter", "NullBarcodeScanner", "NullScale"
    ];

    private static IReadOnlyList<Assembly> ModuleAssemblies =>
    [
        .. Assemblies.AllCatalogAssemblies, .. Assemblies.AllInventoryAssemblies, .. Assemblies.AllSalesAssemblies, .. Assemblies.AllPOSAssemblies,
        .. Assemblies.AllCustomersAssemblies, .. Assemblies.AllSuppliersAssemblies, .. Assemblies.AllPurchasingAssemblies, .. Assemblies.AllPricingAssemblies,
        .. Assemblies.AllPaymentsAssemblies, .. Assemblies.AllUsersAssemblies, .. Assemblies.AllAuditAssemblies, .. Assemblies.AllCashManagementAssemblies,
        .. Assemblies.AllReportingAssemblies
    ];

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
    {
        var separator = Path.DirectorySeparatorChar;
        return patterns.SelectMany(p => Directory.EnumerateFiles(Path.Combine(RepoRoot(), relativeDirectory), p, SearchOption.AllDirectories))
                       .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"));
    }

    [Fact(DisplayName = "ARCH-HW-001: The five hardware abstractions are interfaces in Platform.Application, all extending IHardwareDevice")]
    public void Abstractions_LiveInPlatformApplication()
    {
        Type[] expected = [typeof(IBarcodeScanner), typeof(IReceiptPrinter), typeof(ILabelPrinter), typeof(ICashDrawer), typeof(IScale)];

        Assert.All(expected, t =>
        {
            Assert.True(t.IsInterface);
            Assert.Equal(Assemblies.PlatformApplication, t.Assembly);
            Assert.Equal(Ns, t.Namespace);
            Assert.True(typeof(IHardwareDevice).IsAssignableFrom(t));
        });
    }

    [Fact(DisplayName = "ARCH-HW-002: The Platform (Core, Contracts, Application, Infrastructure) knows no device technology and no concrete hardware")]
    public void Platform_IsDeviceTechnologyFree()
    {
        foreach (var a in Assemblies.AllPlatformAssemblies)
        {
            foreach (var tech in DeviceTechnology)
            {
                AssertNoDependency(a, tech, "Platform defines abstractions only.");
                Assert.DoesNotContain(Refs(a), n => n.StartsWith(tech, StringComparison.Ordinal));
            }

            AssertNoDependency(a, "Client.Hardware", "Platform never depends on adapters.");
        }
    }

    [Fact(DisplayName = "ARCH-HW-003: Business modules (Domain, Application, Contracts, Infrastructure) never touch device technology")]
    public void Modules_AreDeviceTechnologyFree()
    {
        foreach (var a in ModuleAssemblies)
        foreach (var tech in DeviceTechnology)
        {
            AssertNoDependency(a, tech, "Business code reaches devices only through the Platform abstractions.");
            Assert.DoesNotContain(Refs(a), n => n.StartsWith(tech, StringComparison.Ordinal));
        }
    }

    [Fact(DisplayName = "ARCH-HW-004: No business module depends on the concrete hardware adapters (Client.Hardware)")]
    public void Modules_DoNotDependOnConcreteHardware()
    {
        foreach (var a in ModuleAssemblies)
        {
            AssertNoDependency(a, "Client.Hardware", "Business logic depends on abstractions, never concrete hardware.");
            Assert.DoesNotContain(Refs(a), n => n == "Client.Hardware");
        }
    }

    [Fact(DisplayName = "ARCH-HW-005: Only the composition root references Client.Hardware (no source project, no UI project, no other adapter)")]
    public void OnlyTheCompositionRoot_ReferencesTheAdapters()
    {
        foreach (var a in Assemblies.AllProjectAssemblies.Where(a => a != Assemblies.ClientHardware))
            Assert.DoesNotContain(Refs(a), n => n == "Client.Hardware");

        var referencing = Files("src", "*.csproj")
            .Where(f => File.ReadAllText(f).Contains("Client.Hardware.csproj", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        Assert.Equal(["Client.Desktop"], referencing);
    }

    [Fact(DisplayName = "ARCH-HW-006: Domain assemblies never use the hardware abstractions")]
    public void Domains_DoNotUseHardware()
    {
        var domains = new[]
        {
            Assemblies.CatalogDomain, Assemblies.InventoryDomain, Assemblies.SalesDomain, Assemblies.POSDomain, Assemblies.CustomersDomain,
            Assemblies.SuppliersDomain, Assemblies.PurchasingDomain, Assemblies.PricingDomain, Assemblies.PaymentsDomain, Assemblies.UsersDomain,
            Assemblies.AuditDomain, Assemblies.CashManagementDomain, Assemblies.ReportingDomain
        };

        foreach (var domain in domains)
            AssertNoDependency(domain, Ns, "Domain stays free of every infrastructure concern, including peripherals.");
    }

    [Fact(DisplayName = "ARCH-HW-007: Only the platform, the adapters and POS use the hardware abstractions (modules cannot reach into each other's hardware)")]
    public void OnlyApprovedAssemblies_UseHardwareAbstractions()
    {
        string[] approved = ["Platform.Application", "Client.Hardware", "POS.Application", "POS.Infrastructure"];

        foreach (var a in Assemblies.AllProjectAssemblies.Concat(ModuleAssemblies).Distinct())
        {
            if (approved.Contains(a.GetName().Name)) continue;

            AssertNoDependency(a, Ns, "Adding a hardware consumer is a deliberate architectural decision: extend this list in the same change.");
        }

        // The approved consumers do exist (the rule is not vacuous) and the contracts assembly stays free of them.
        Assert.True(Types.InAssembly(Assemblies.POSApplication).That().HaveDependencyOn(Ns).GetTypes().Any());
        AssertNoDependency(Assemblies.POSContracts, Ns, "POS.Contracts exposes plain records and results, never a device abstraction.");
    }

    [Fact(DisplayName = "ARCH-HW-008: Client.Hardware has no WPF, EF Core, ASP.NET Core, HTTP, business-module or server dependency")]
    public void ClientHardware_Boundaries()
    {
        var a = Assemblies.ClientHardware;

        foreach (var forbidden in new[] { "System.Windows", "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "System.Net.Http", "Platform.Infrastructure" })
        {
            AssertNoDependency(a, forbidden, "Adapters are plain, offline, local-device code.");
            Assert.DoesNotContain(Refs(a), n => n.StartsWith(forbidden, StringComparison.Ordinal));
        }

        foreach (var prefix in ServerPrefixes)
            Assert.DoesNotContain(Refs(a), n => n.StartsWith(prefix, StringComparison.Ordinal));

        foreach (var module in new[] { "Catalog", "Inventory", "Sales", "POS", "Customers", "Suppliers", "Purchasing", "Pricing", "Payments", "Users", "Audit", "CashManagement", "Reporting" })
            Assert.DoesNotContain(Refs(a), n => n.StartsWith(module + ".", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-HW-009: Client.Hardware uses no vendor SDK or serial-port package (only the approved Microsoft.Extensions packages)")]
    public void ClientHardware_HasNoVendorSdk()
    {
        var csproj = Files("src/Client/Client.Hardware", "*.csproj").Single();
        var packages = File.ReadAllLines(csproj)
            .Where(l => l.Contains("<PackageReference", StringComparison.Ordinal))
            .Select(l => l.Split('"')[1])
            .Order()
            .ToList();

        Assert.Equal(
            ["Microsoft.Extensions.Configuration.Binder", "Microsoft.Extensions.Hosting.Abstractions", "Microsoft.Extensions.Logging.Abstractions"],
            packages);
        Assert.DoesNotContain(Refs(Assemblies.ClientHardware), n => n.StartsWith("System.IO.Ports", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "ARCH-HW-010: UI code never references or constructs hardware (UI projects and Client.Desktop sources)")]
    public void Ui_NeverTouchesHardwareDrivers()
    {
        string[] forbidden = ["Client.Hardware", "SerialPort", "System.IO.Ports", "TcpClient", "EscPos", "Zpl", "IReceiptPrinter", "ICashDrawer", "ILabelPrinter", "IScale", "IBarcodeScanner"];
        var scanned = 0;

        foreach (var file in Files("src", "*.cs", "*.xaml", "*.csproj").Where(f => f.Contains(".UI" + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            scanned++;
            var text = File.ReadAllText(file);
            foreach (var token in forbidden)
                Assert.False(text.Contains(token, StringComparison.Ordinal), $"{file} mentions '{token}': UI must go through POS contracts (IPOSDevices / IPOSBarcodeInput).");
        }

        Assert.True(scanned > 20, "The rule would pass vacuously.");

        // The desktop shell may only register the hardware hosting module (composition root), nothing else.
        foreach (var file in Files("src/Client/Client.Desktop", "*.cs", "*.xaml"))
        {
            var text = File.ReadAllText(file);
            foreach (var token in forbidden.Where(t => t != "Client.Hardware"))
                Assert.False(text.Contains(token, StringComparison.Ordinal), $"{file} mentions '{token}'.");

            if (text.Contains("Client.Hardware", StringComparison.Ordinal))
                Assert.EndsWith("DesktopComposition.cs", file); // the composition root's module list (FIX-01a; was App.xaml.cs)
        }
    }

    [Fact(DisplayName = "ARCH-HW-011: Concrete adapters are constructed only inside Client.Hardware (never by business code, UI or other projects)")]
    public void Adapters_AreNeverConstructedOutsideTheHardwareProject()
    {
        var offenders = new List<string>();

        foreach (var file in Files("src", "*.cs").Where(f => !f.Contains(Path.Combine("src", "Client", "Client.Hardware") + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file);
            offenders.AddRange(ConcreteAdapterTypes.Where(t => text.Contains("new " + t + "(", StringComparison.Ordinal)).Select(t => $"{Path.GetFileName(file)}: new {t}"));
        }

        Assert.True(offenders.Count == 0, "Concrete adapters must be created by HardwareFactory only: " + string.Join("; ", offenders));
    }

    [Fact(DisplayName = "ARCH-HW-012: Only HardwareFactory chooses and constructs adapters inside Client.Hardware")]
    public void InsideTheHardwareProject_OnlyTheFactoryConstructsAdapters()
    {
        foreach (var file in Files("src/Client/Client.Hardware", "*.cs"))
        {
            var name = Path.GetFileName(file);
            if (name is "HardwareFactory.cs") continue;

            var text = File.ReadAllText(file);
            foreach (var type in new[] { "EscPosReceiptPrinter", "EscPosCashDrawer", "ZplLabelPrinter", "KeyboardWedgeBarcodeScanner", "TcpDeviceTransport", "FileDeviceTransport" })
                Assert.False(text.Contains("new " + type + "(", StringComparison.Ordinal), $"{name} constructs {type}; only HardwareFactory may.");
        }
    }

    [Fact(DisplayName = "ARCH-HW-013: Hardware configuration is its own section and no internet endpoint is configured or hard-coded (offline-first)")]
    public void HardwareConfiguration_IsSeparate_AndOffline()
    {
        Assert.Equal("Hardware", Client.Hardware.Configuration.HardwareOptions.SectionName);

        foreach (var file in Files("src/Client/Client.Hardware", "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("http://", text);
            Assert.DoesNotContain("https://", text);
            foreach (var line in text.Split('\n').Where(l => l.Contains("GetSection(", StringComparison.Ordinal)))
                Assert.Contains("HardwareOptions.SectionName", line);
        }

        // Business receipt settings live with the business module, not with hardware.
        Assert.Equal("PosReceipt", POS.Application.Devices.PosReceiptOptions.SectionName);
        Assert.NotEqual(Assemblies.ClientHardware, typeof(POS.Application.Devices.PosReceiptOptions).Assembly);
    }

    [Fact(DisplayName = "ARCH-HW-014: Business code treats hardware as optional (every device dependency of POS is a nullable constructor parameter)")]
    public void PosDeviceDependencies_AreOptional()
    {
        var deviceTypes = new[] { typeof(IBarcodeScanner), typeof(IReceiptPrinter), typeof(ILabelPrinter), typeof(ICashDrawer), typeof(IScale) };
        var checkedParameters = 0;

        foreach (var assembly in new[] { Assemblies.POSApplication, Assemblies.POSInfrastructure })
        foreach (var type in assembly.GetTypes().Where(t => t.IsClass))
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        foreach (var parameter in constructor.GetParameters().Where(p => deviceTypes.Contains(p.ParameterType)))
        {
            checkedParameters++;
            Assert.True(parameter.HasDefaultValue && parameter.DefaultValue is null,
                $"{type.Name} requires {parameter.ParameterType.Name}; hardware must be optional (nullable, default null).");
        }

        Assert.True(checkedParameters >= 6, "The rule would pass vacuously.");
    }
}
