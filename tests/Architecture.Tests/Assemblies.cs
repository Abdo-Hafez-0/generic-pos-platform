using System.Reflection;

namespace Architecture.Tests;

/// <summary>
/// Provides a central registry of all assemblies that belong to the platform
/// and client layers. This registry is expanded as new assemblies are added.
///
/// NOTE: Client.Desktop targets net10.0-windows and cannot be directly referenced
/// by the Architecture.Tests project (which targets net10.0). Client.Desktop boundary
/// rules are enforced by build-time inspection of its .csproj references rather than
/// by NetArchTest. This is documented in the ClientLayerTests class.
/// </summary>
internal static class Assemblies
{
    // -----------------------------------------------------------------------
    // Platform assemblies (Stage 1)
    // -----------------------------------------------------------------------

    internal static readonly Assembly PlatformCore =
        typeof(Platform.Core.Results.Result).Assembly;

    internal static readonly Assembly PlatformContracts =
        typeof(Platform.Contracts.Events.IDomainEventPublisher).Assembly;

    internal static readonly Assembly PlatformApplication =
        typeof(Platform.Application.Abstractions.Messaging.ICommand).Assembly;

    internal static readonly Assembly PlatformInfrastructure =
        typeof(Platform.Infrastructure.PlatformInfrastructureAssemblyMarker).Assembly;

    // -----------------------------------------------------------------------
    // Client assemblies (Stage 2)
    // -----------------------------------------------------------------------

    internal static readonly Assembly ClientHost =
        typeof(Client.Host.Hosting.IApplicationHost).Assembly;

    internal static readonly Assembly ClientModuleHost =
        typeof(Client.ModuleHost.ModuleHostRegistrar).Assembly;

    internal static readonly Assembly ClientLicensing =
        typeof(Client.Licensing.ClientLicensingAssemblyMarker).Assembly;

    internal static readonly Assembly ClientUpdater =
        typeof(Client.Updater.ClientUpdaterAssemblyMarker).Assembly;

    // Note: Client.Desktop (net10.0-windows) cannot be referenced here.
    // See ClientLayerTests for documentation of how Desktop boundaries are verified.

    // -----------------------------------------------------------------------
    // Module assemblies (Stage 5+)
    // -----------------------------------------------------------------------

    // Catalog module assemblies (Stage 5A)
    // Note: Catalog.UI is net10.0-windows and cannot be referenced here (TFM gap).
    // Its boundary rules are enforced via .csproj reference inspection.
    internal static readonly Assembly CatalogDomain =
        typeof(Catalog.Domain.Entities.Product).Assembly;

    internal static readonly Assembly CatalogContracts =
        typeof(Catalog.Contracts.Interfaces.IProductLookup).Assembly;

    internal static readonly Assembly CatalogApplication =
        typeof(Catalog.Application.Commands.CreateProductCommandHandler).Assembly;

    internal static readonly Assembly CatalogInfrastructure =
        typeof(Catalog.Infrastructure.Module.CatalogModule).Assembly;

    // -----------------------------------------------------------------------
    // Convenience groupings
    // -----------------------------------------------------------------------

    internal static IReadOnlyList<Assembly> AllPlatformAssemblies =>
    [
        PlatformCore,
        PlatformContracts,
        PlatformApplication,
        PlatformInfrastructure
    ];

    internal static IReadOnlyList<Assembly> AllClientAssemblies =>
    [
        ClientHost,
        ClientModuleHost,
        ClientLicensing,
        ClientUpdater
        // Client.Desktop excluded — see note above
    ];

    internal static IReadOnlyList<Assembly> AllCatalogAssemblies =>
    [
        CatalogDomain,
        CatalogContracts,
        CatalogApplication,
        CatalogInfrastructure
        // Catalog.UI excluded — net10.0-windows TFM gap
    ];

    // Inventory module assemblies (Stage 5B)
    // Note: Inventory.UI is net10.0-windows and cannot be referenced here (TFM gap).
    internal static readonly Assembly InventoryDomain =
        typeof(Inventory.Domain.Entities.Warehouse).Assembly;

    internal static readonly Assembly InventoryContracts =
        typeof(Inventory.Contracts.Interfaces.IInventoryReader).Assembly;

    internal static readonly Assembly InventoryApplication =
        typeof(Inventory.Application.Commands.AddStockCommandHandler).Assembly;

    internal static readonly Assembly InventoryInfrastructure =
        typeof(Inventory.Infrastructure.InventoryInfrastructureAssemblyMarker).Assembly;

    internal static IReadOnlyList<Assembly> AllInventoryAssemblies =>
    [
        InventoryDomain,
        InventoryContracts,
        InventoryApplication,
        InventoryInfrastructure
        // Inventory.UI excluded — net10.0-windows TFM gap
    ];

    // Sales module assemblies (Stage 5C)
    // Note: Sales.UI is net10.0-windows and cannot be referenced here (TFM gap).
    internal static readonly Assembly SalesDomain =
        typeof(Sales.Domain.Entities.Sale).Assembly;

    internal static readonly Assembly SalesContracts =
        typeof(Sales.Contracts.Interfaces.ISalesService).Assembly;

    internal static readonly Assembly SalesApplication =
        typeof(Sales.Application.Commands.CreateSaleCommandHandler).Assembly;

    internal static readonly Assembly SalesInfrastructure =
        typeof(Sales.Infrastructure.SalesInfrastructureAssemblyMarker).Assembly;

    internal static IReadOnlyList<Assembly> AllSalesAssemblies =>
    [
        SalesDomain,
        SalesContracts,
        SalesApplication,
        SalesInfrastructure
        // Sales.UI excluded — net10.0-windows TFM gap
    ];

    // POS module assemblies (Stage 5D)
    // Note: POS.UI is net10.0-windows and cannot be referenced here (TFM gap).
    internal static readonly Assembly POSDomain =
        typeof(POS.Domain.Entities.PosCart).Assembly;

    internal static readonly Assembly POSContracts =
        typeof(POS.Contracts.Interfaces.IPOSService).Assembly;

    internal static readonly Assembly POSApplication =
        typeof(POS.Application.Commands.CheckoutCartCommandHandler).Assembly;

    internal static readonly Assembly POSInfrastructure =
        typeof(POS.Infrastructure.POSInfrastructureAssemblyMarker).Assembly;

    internal static IReadOnlyList<Assembly> AllPOSAssemblies =>
    [
        POSDomain,
        POSContracts,
        POSApplication,
        POSInfrastructure
        // POS.UI excluded - net10.0-windows TFM gap
    ];

    // Licensing assemblies (Stage 6)
    // Note: LicenseServer.Api is an ASP.NET Core host and is not referenced here.
    internal static readonly Assembly LicensingContracts =
        typeof(Licensing.Contracts.SignedLicense).Assembly;

    internal static readonly Assembly ClientLicensingHttp =
        typeof(Client.Licensing.Http.HttpLicenseClient).Assembly;

    internal static readonly Assembly LicenseServerApplication =
        typeof(LicenseServer.Application.LicenseIssuanceService).Assembly;

    internal static readonly Assembly LicenseServerInfrastructure =
        typeof(LicenseServer.Infrastructure.EcdsaLicenseSigner).Assembly;

    internal static IReadOnlyList<Assembly> AllLicenseServerAssemblies =>
    [
        LicenseServerApplication,
        LicenseServerInfrastructure
    ];

    internal static IReadOnlyList<Assembly> AllBusinessModuleAssemblies =>
    [
        .. AllCatalogAssemblies,
        .. AllInventoryAssemblies,
        .. AllSalesAssemblies,
        .. AllPOSAssemblies,
        .. AllCustomersAssemblies
    ];

    // Update system + shared security assemblies (Stage 7)
    // Note: UpdateServer.Api is an ASP.NET Core host and is not referenced here.
    internal static readonly Assembly SecurityEs256 =
        typeof(Security.Es256.Es256Verifier).Assembly;

    internal static readonly Assembly SecurityEs256Signing =
        typeof(Security.Es256.Signing.Es256Signer).Assembly;

    internal static readonly Assembly UpdatesContracts =
        typeof(Updates.Contracts.PackageManifest).Assembly;

    internal static readonly Assembly UpdatesPackage =
        typeof(Updates.Package.PackageFormat).Assembly;

    internal static readonly Assembly ClientUpdaterHttp =
        typeof(Client.Updater.Http.HttpUpdateClient).Assembly;

    internal static readonly Assembly UpdateServerApplication =
        typeof(UpdateServer.Application.UpdateDiscoveryService).Assembly;

    internal static readonly Assembly ModulePackager =
        typeof(Tools.ModulePackager.ModulePackager).Assembly;

    internal static readonly Assembly UpdatePublisher =
        typeof(Tools.UpdatePublisher.UpdatePublisher).Assembly;

    internal static IReadOnlyList<Assembly> AllUpdateAssemblies =>
    [
        UpdatesContracts,
        UpdatesPackage,
        ClientUpdater,
        ClientUpdaterHttp,
        UpdateServerApplication,
        ModulePackager,
        UpdatePublisher,
        .. AllCustomersAssemblies
    ];

    // Customers module assemblies (Stage 8)
    // Note: Customers.UI is net10.0-windows and cannot be referenced here (TFM gap).
    internal static readonly Assembly CustomersDomain =
        typeof(Customers.Domain.Entities.Customer).Assembly;

    internal static readonly Assembly CustomersContracts =
        typeof(Customers.Contracts.Interfaces.ICustomerLookup).Assembly;

    internal static readonly Assembly CustomersApplication =
        typeof(Customers.Application.Commands.CreateCustomerCommandHandler).Assembly;

    internal static readonly Assembly CustomersInfrastructure =
        typeof(Customers.Infrastructure.CustomersInfrastructureAssemblyMarker).Assembly;

    internal static IReadOnlyList<Assembly> AllCustomersAssemblies =>
    [
        CustomersDomain,
        CustomersContracts,
        CustomersApplication,
        CustomersInfrastructure
        // Customers.UI excluded - net10.0-windows TFM gap
    ];

    // Suppliers module assemblies (Stage 8)
    // Note: Suppliers.UI is net10.0-windows and cannot be referenced here (TFM gap).
    internal static readonly Assembly SuppliersDomain =
        typeof(Suppliers.Domain.Entities.Supplier).Assembly;

    internal static readonly Assembly SuppliersContracts =
        typeof(Suppliers.Contracts.Interfaces.ISupplierLookup).Assembly;

    internal static readonly Assembly SuppliersApplication =
        typeof(Suppliers.Application.Commands.CreateSupplierCommandHandler).Assembly;

    internal static readonly Assembly SuppliersInfrastructure =
        typeof(Suppliers.Infrastructure.SuppliersInfrastructureAssemblyMarker).Assembly;

    internal static IReadOnlyList<Assembly> AllSuppliersAssemblies =>
    [
        SuppliersDomain,
        SuppliersContracts,
        SuppliersApplication,
        SuppliersInfrastructure
        // Suppliers.UI excluded - net10.0-windows TFM gap
    ];

    internal static IReadOnlyList<Assembly> AllProjectAssemblies =>
    [
        .. AllPlatformAssemblies,
        .. AllClientAssemblies,
        .. AllCatalogAssemblies,
        .. AllInventoryAssemblies,
        .. AllSalesAssemblies,
        .. AllPOSAssemblies,
        LicensingContracts,
        ClientLicensingHttp,
        .. AllLicenseServerAssemblies,
        SecurityEs256,
        SecurityEs256Signing,
        UpdatesContracts,
        UpdatesPackage,
        ClientUpdaterHttp,
        UpdateServerApplication,
        ModulePackager,
        UpdatePublisher,
        .. AllSuppliersAssemblies
    ];
}
