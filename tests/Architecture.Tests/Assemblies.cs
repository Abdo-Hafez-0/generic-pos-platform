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

    internal static IReadOnlyList<Assembly> AllProjectAssemblies =>
    [
        .. AllPlatformAssemblies,
        .. AllClientAssemblies,
        .. AllCatalogAssemblies,
        .. AllInventoryAssemblies,
        .. AllSalesAssemblies,
        .. AllPOSAssemblies
    ];
}
