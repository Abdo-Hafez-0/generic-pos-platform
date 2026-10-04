# PROJECT_STATE.md
# Generic Offline-First Inventory & POS Platform - Implementation State

---

## Project Purpose

A **generic, modular, offline-first business management platform** for inventory-based businesses
(retail, wholesale, pharmacy, restaurants, etc.). One application platform, multiple feature sets
determined by customer license entitlements.

---

## Current Architectural Approach

**Modular Monolith + Offline-First + Extensible**

- **Desktop Client:** Windows WPF application
- **Local Database:** SQLite via EF Core 10.0 (operational since Stage 3)
- **Cloud Backend:** ASP.NET Core (not yet created - Stage 6)
- **Cross-module communication:** Contracts only (never implementation references)
- **Layering:** UI -> Application -> Domain <- Infrastructure
- **Target framework:** net10.0 (net10.0-windows for WPF project)

---

## Current Implementation Phase

**Stage 5C COMPLETE: Sales Module**

The Sales module is implemented following the canonical module pattern (Stages 5A/5B).
All five Sales layers (Domain, Application, Contracts, Infrastructure, UI) exist and are tested.
Domain model: Sale, SaleItem, Return, ReturnItem, SalesTransaction (Payments is NOT part of Sales;
it remains a separate future module). Sales.UI (WPF) is implemented (SaleListViewModel) but not
architecture-tested due to the net10.0-windows TFM gap. SalesHostingModule is registered in the
desktop host (Catalog -> Inventory -> Sales). EF Core migration `InitialSalesSchema` creates the
5 `sal_` tables. Architecture tests ARCH-SAL-001 through ARCH-SAL-016 are active and passing.
`CompleteSaleCommandHandler` records a SalesTransaction only; inventory reduction and payment
processing are deliberately deferred (Stage 5D / Payments).

### Previous phase - Stage 5B COMPLETE: Inventory Module

The Inventory module is fully implemented following the canonical module pattern (established in Stage 5A).
All five Inventory layers (Domain, Application, Contracts, Infrastructure, UI) are implemented and tested.
Inventory.UI (WPF) is implemented but not architecture-tested due to the net10.0-windows TFM gap.
The Inventory module is integrated into the application host via InventoryHostingModule.
EF Core migration `InitialInventorySchema` is generated (all 6 tables with `inv_` prefix).
Architecture tests ARCH-INV-001 through ARCH-INV-011 are active and passing (12 test methods in total).

---

## Completed Work

### Stage 1 - Solution Foundation
- [x] Read all three architecture documents
- [x] Created GenericPOS.sln
- [x] Created src/Platform/{Core,Contracts,Application,Infrastructure}
- [x] Created tests/Architecture.Tests
- [x] Implemented Platform.Core primitives (Result, Error, EntityId, ModuleId, FeatureId, DomainEvent)
- [x] Implemented Platform.Contracts (IDomainEventPublisher, IDomainEventHandler)
- [x] Implemented Platform.Application (ICommand, IQuery, IRequest, IRequestHandler, IUnitOfWork, ICurrentUser)
- [x] Platform.Infrastructure: Assembly marker only (Stage 3 adds implementations)
- [x] 22 architecture tests passing

### Stage 2 - Client Host
- [x] Created src/Client/{Host,ModuleHost,Desktop,Licensing,Updater}
- [x] Implemented DI, configuration, logging, module discovery, WPF shell
- [x] Application starts, window appears, logs correctly, shuts down gracefully
- [x] Added 10 architecture tests (32 total passing)

### Stage 3 - Database Foundation
- [x] Added Microsoft.EntityFrameworkCore.Sqlite 10.0.11 to Platform.Infrastructure
- [x] Added Microsoft.EntityFrameworkCore.Design 10.0.11 to Platform.Infrastructure
- [x] Added Microsoft.Extensions.Configuration.Binder to Platform.Infrastructure
- [x] Added [InternalsVisibleTo("Platform.Infrastructure.Tests")] to Platform.Infrastructure
- [x] Implemented PlatformDbContext (empty - no business tables in Stage 3)
- [x] Implemented PlatformUnitOfWork (IUnitOfWork, IDisposable, IAsyncDisposable)
- [x] Implemented DatabaseOptions (SQLite path configuration, LocalAppData strategy)
- [x] Implemented DatabaseInitializer (EnsureCreated/MigrateAsync, safe, no destructive ops)
- [x] Implemented InfrastructureServicesExtensions.AddPlatformInfrastructure()
- [x] Implemented DatabaseInitializerService (IHostedService, runs during IHost.StartAsync())
- [x] Added Platform.Infrastructure reference to Client.Host (composition root justification)
- [x] Updated PlatformServicesExtensions to call AddPlatformInfrastructure(configuration)
- [x] Updated GenericApplicationHost to pass context.Configuration to AddPlatformServices()
- [x] Updated appsettings.json with Database configuration section
- [x] Created tests/Platform.Infrastructure.Tests (19 tests, all passing)
- [x] Created DatabaseBoundaryTests.cs in Architecture.Tests (9 new Stage 3 tests)
- [x] Build succeeded: 0 errors, 0 warnings (11 projects)
- [x] All 60 tests PASS (41 Architecture + 19 Infrastructure)
- [x] WPF application starts with database initialized (verified by log output)
- [x] Database created at %LOCALAPPDATA%\GenericPOS\genericpos.db (confirmed in startup logs)
- [x] Stage 2 desktop startup still works

### Stage 4 - Module Contract
- [x] Implemented ModuleVersion (Major.Minor.Patch value type, custom - not NuGet)
- [x] Implemented VersionRange (constraint model: >=, <=, ==, >, <)
- [x] Implemented ModuleDependency (RequiredModuleId + VersionRange)
- [x] Implemented IFeatureDescriptor (FeatureId + DisplayName)
- [x] Implemented ModuleRuntimeStatus enum (runtime lifecycle states only)
- [x] Implemented IModuleManifest (runtime manifest contract - hash/signature excluded as Stage 7)
- [x] Implemented IModule (runtime lifecycle contract: Manifest, Status, InitializeAsync, StartAsync, StopAsync)
- [x] Implemented IModuleRegistry + ModuleRegistry (thread-safe Singleton)
- [x] Implemented IModuleDependencyResolver + ModuleDependencyResolver (pure DFS algorithm)
- [x] Implemented DependencyResolutionResult (success/failure + activation order/errors)
- [x] Implemented ModuleRegistrationRecord (links ModuleCandidate to IModule)
- [x] Updated Client.ModuleHost.csproj: added Platform.Application reference
- [x] Updated ModuleHostRegistrar: registers IModuleRegistry + IModuleDependencyResolver
- [x] Created tests/Platform.ModuleContract.Tests (112 tests, all passing)
- [x] Created ModuleContractArchitectureTests in Architecture.Tests (7 new Stage 4 tests)
- [x] Build: 0 errors, 0 warnings (12 projects)
- [x] All 179 tests PASS (48 Architecture + 19 Infrastructure + 112 ModuleContract)
- [x] IHostingModule vs IModule decision documented below
- [x] Stage 3 desktop startup still works (no runtime changes to startup path)

### Stage 5A - Catalog Module
- [x] Created Catalog.Domain: Product, Category, Unit, Barcode aggregates; domain events; strongly-typed IDs
- [x] Created Catalog.Contracts: IProductLookup, IProductBarcodeResolver, ProductLookupResult, ProductStatusContract
- [x] Created Catalog.Application: Commands + Queries + Repository interfaces + ICatalogUnitOfWork + DTOs
- [x] Created Catalog.Infrastructure: CatalogDbContext (cat_ prefix), EF configurations, repositories,
      CatalogModule (IModule), CatalogModuleManifest, CatalogHostingModule (IHostingModule),
      CatalogDatabaseInitializer (IHostedService), CatalogServicesExtensions, Migration: CatalogInitialCreate
- [x] Created Catalog.UI: CreateProductViewModel, ProductListViewModel (net10.0-windows)
- [x] Updated Client.Desktop.csproj: added Catalog.Infrastructure and Catalog.UI references
- [x] Updated App.xaml.cs: registered CatalogHostingModule
- [x] Updated GenericPOS.sln: added all Catalog projects + Catalog.Tests
- [x] Created tests/Catalog.Tests (64 tests, all passing)
- [x] Updated Architecture.Tests: added Catalog assemblies + 19 new Catalog boundary tests
- [x] Generated EF Core migration: CatalogInitialCreate (cat_Products, cat_Categories, cat_Units, cat_Barcodes)
- [x] Build: 0 errors, 0 warnings (18 projects)
- [x] All 262 tests PASS (67 Architecture + 64 Catalog + 19 Infrastructure + 112 ModuleContract)
- [x] Catalog -> no Inventory dependency
- [x] Platform -> no Catalog dependency

### Stage 5B - Inventory Module
- [x] Created Inventory.Domain: Entities (Warehouse, Location, StockItem, StockMovement, StockAdjustment, InventoryBalance)
- [x] Created Inventory.Domain: Value Objects (WarehouseId, LocationId, StockItemId, StockMovementId, StockAdjustmentId, Quantity)
- [x] Created Inventory.Domain: Enums (MovementType, AdjustmentReason)
- [x] Created Inventory.Domain: Domain Events (WarehouseCreated, StockMovementRecorded, StockAdjusted)
- [x] Created Inventory.Contracts: DTOs (StockLevelDto, StockMovementDto, WarehouseDto)
- [x] Created Inventory.Contracts: Interfaces (IInventoryReader, IStockAvailabilityChecker, IStockMovementReader)
- [x] Created Inventory.Application: Repository interfaces (IWarehouseRepository, ILocationRepository,
      IStockItemRepository, IStockMovementRepository, IStockAdjustmentRepository, IInventoryBalanceRepository)
- [x] Created Inventory.Application: IInventoryUnitOfWork
- [x] Created Inventory.Application: Commands (CreateWarehouse, CreateLocation, AddStock, AdjustStock)
- [x] Created Inventory.Application: Queries (GetWarehouses, GetStockLevel, GetAllStockLevels, GetStockMovements)
- [x] Created Inventory.Infrastructure: InventoryDbContext (owns inv_* tables, same SQLite file)
- [x] Created Inventory.Infrastructure: EF Configurations for all 6 entities
- [x] Created Inventory.Infrastructure: 6 EF Repository implementations (all internal sealed)
- [x] Created Inventory.Infrastructure: Contract services (InventoryReader, StockAvailabilityChecker, StockMovementReader)
- [x] Created Inventory.Infrastructure: InventoryModuleManifest, InventoryModule, InventoryHostingModule, InventoryDatabaseInitializer
- [x] Created Inventory.Infrastructure: InventoryServicesExtensions.AddInventoryModule()
- [x] Created Inventory.Infrastructure: [InternalsVisibleTo("Inventory.Tests")]
- [x] Created Inventory.UI: WarehouseListViewModel, StockLevelViewModel (net10.0-windows)
- [x] Updated Client.Desktop.csproj: added Inventory.Infrastructure and Inventory.UI references
- [x] Updated App.xaml.cs: registered InventoryHostingModule after CatalogHostingModule
- [x] Updated GenericPOS.sln: added all Inventory projects + Inventory.Tests (24 projects total)
- [x] Created tests/Inventory.Tests: InventoryTestDatabase + StubProductLookup
- [x] Created tests/Inventory.Tests: Domain tests (covering Warehouse, StockItem, Quantity, InventoryBalance, StockMovement)
- [x] Created tests/Inventory.Tests: Application tests (AddStockCommand, AdjustStockCommand integration tests)
- [x] Created tests/Inventory.Tests: Infrastructure tests (schema, contracts, availability checker)
- [x] Created tests/Inventory.Tests: Contracts tests (DTO boundary, interface shape)
- [x] Updated Architecture.Tests: added Inventory assemblies to Assemblies.cs and project references
- [x] Created Architecture.Tests/DependencyRules/InventoryBoundaryTests.cs (12 test methods, ARCH-INV-001 to ARCH-INV-011)
- [x] Generated EF Core migration: InitialInventorySchema (all 6 tables with inv_ prefix)
- [x] Build: 0 errors, 0 warnings (24 projects)
- [x] All 355 tests PASS (79 Architecture + 81 Inventory + 64 Catalog + 19 Infrastructure + 112 ModuleContract)
- [x] Inventory -> Catalog.Contracts dependency only (Catalog.Domain never referenced)
- [x] Catalog -> no Inventory dependency (no reverse)
- [x] Platform -> no Inventory dependency
- [x] Inventory operates fully offline

---

## Current Task

**Stage 5C - COMPLETE. Stopping before Stage 5D - POS.**

---

## Next Task

**Stage 5D - POS Module (awaiting instruction)**

POS consumes Catalog.Contracts, Inventory.Contracts and Sales.Contracts (ISalesService, ISalesReader).
Cross-module orchestration of the vertical slice (stock reduction, payment) is Stage 5D+ work.

---

## Solution / Project Structure (Current State)

GenericPOS.sln (30 projects)

src/
+-- Platform/
|   +-- Platform.Core              [DONE] - Primitives + Module contracts (IModule, IModuleManifest,
|   |                                        ModuleVersion, VersionRange, ModuleDependency,
|   |                                        IFeatureDescriptor, ModuleRuntimeStatus)
|   +-- Platform.Contracts         [DONE] - IDomainEventPublisher, IDomainEventHandler
|   +-- Platform.Application       [DONE] - CQRS abstractions + IModuleRegistry,
|   |                                        IModuleDependencyResolver, ModuleDependencyResolver,
|   |                                        ModuleRegistry, DependencyResolutionResult
|   +-- Platform.Infrastructure    [DONE] - PlatformDbContext, PlatformUnitOfWork, DatabaseInitializer
|
+-- Client/
|   +-- Client.Host                [DONE] - IApplicationHost, IHostingModule, Config, Logging, DI
|   +-- Client.ModuleHost          [DONE] - IModuleDiscoveryService, FileSystemModuleDiscoveryService,
|   |                                        ModuleCandidate, ModuleHostRegistrar (registers IModuleRegistry,
|   |                                        IModuleDependencyResolver), ModuleRegistrationRecord
|   +-- Client.Desktop             [DONE] - WPF shell + Catalog + Inventory + Sales modules wired in (Stage 5C)
|   +-- Client.Licensing           [DONE] - Boundary marker only (Stage 6)
|   +-- Client.Updater             [DONE] - Boundary marker only (Stage 7)
|
+-- Modules/
    +-- Catalog/
    |   +-- Catalog.Domain         [DONE] - Product, Category, Unit, Barcode aggregates; domain events;
    |   |                                    strongly-typed IDs (ProductId, CategoryId, UnitId, BarcodeId)
    |   +-- Catalog.Contracts      [DONE] - IProductLookup, IProductBarcodeResolver, ProductLookupResult,
    |   |                                    ProductStatusContract (cross-module public API)
    |   +-- Catalog.Application    [DONE] - Commands: CreateProduct, UpdateProduct, DeactivateProduct,
    |   |                                    CreateCategory, CreateUnit, AssignBarcode
    |   |                                    Queries: GetProductById, GetProductBySku, FindProductByBarcode,
    |   |                                    GetAllCategories, GetAllUnits
    |   |                                    Repositories: IProductRepository, ICategoryRepository,
    |   |                                    IUnitRepository, IBarcodeRepository, ICatalogUnitOfWork
    |   |                                    DTOs: ProductDto, CategoryDto, UnitDto, BarcodeDto
    |   +-- Catalog.Infrastructure [DONE] - CatalogDbContext (cat_ prefix), EF configurations,
    |   |                                    EfProductRepository, EfCategoryRepository, EfUnitRepository,
    |   |                                    EfBarcodeRepository, CatalogUnitOfWork,
    |   |                                    CatalogProductLookup, CatalogBarcodeResolver,
    |   |                                    CatalogModule (IModule), CatalogModuleManifest,
    |   |                                    CatalogHostingModule (IHostingModule),
    |   |                                    CatalogDatabaseInitializer (IHostedService),
    |   |                                    CatalogServicesExtensions, Migration: CatalogInitialCreate
    |   +-- Catalog.UI             [DONE] - CreateProductViewModel, ProductListViewModel (net10.0-windows)
    |
    +-- Inventory/
        +-- Inventory.Domain       [DONE] - Warehouse, Location, StockItem, StockMovement,
        |                                    StockAdjustment, InventoryBalance entities;
        |                                    strongly-typed IDs (WarehouseId, LocationId,
        |                                    StockItemId, StockMovementId, StockAdjustmentId);
        |                                    Quantity value object; MovementType, AdjustmentReason enums;
        |                                    domain events (WarehouseCreated, StockMovementRecorded,
        |                                    StockAdjusted)
        +-- Inventory.Contracts    [DONE] - IInventoryReader, IStockAvailabilityChecker,
        |                                    IStockMovementReader (cross-module public API);
        |                                    DTOs: StockLevelDto, StockMovementDto, WarehouseDto
        +-- Inventory.Application  [DONE] - Commands: CreateWarehouse, CreateLocation, AddStock, AdjustStock
        |                                    Queries: GetWarehouses, GetStockLevel, GetAllStockLevels,
        |                                    GetStockMovements
        |                                    Repositories: IWarehouseRepository, ILocationRepository,
        |                                    IStockItemRepository, IStockMovementRepository,
        |                                    IStockAdjustmentRepository, IInventoryBalanceRepository
        |                                    IInventoryUnitOfWork
        +-- Inventory.Infrastructure [DONE] - InventoryDbContext (inv_ prefix), EF configurations
        |                                    (all 6 entities), internal EF repository implementations,
        |                                    InventoryReader, StockAvailabilityChecker, StockMovementReader,
        |                                    InventoryModule (IModule), InventoryModuleManifest,
        |                                    InventoryHostingModule (IHostingModule),
        |                                    InventoryDatabaseInitializer (IHostedService),
        |                                    InventoryServicesExtensions,
        |                                    Migration: InitialInventorySchema
        +-- Inventory.UI           [DONE] - WarehouseListViewModel, StockLevelViewModel (net10.0-windows)

    +-- Sales/
        +-- Sales.Domain           [DONE] - Sale, SaleItem, Return, ReturnItem, SalesTransaction entities;
        |                                    value objects Money, SaleQuantity, SaleId, SaleItemId,
        |                                    ReturnId, ReturnItemId; enums SaleStatus (Draft, Confirmed,
        |                                    Completed, Cancelled), ReturnStatus (Pending, Processed,
        |                                    Rejected); events SaleCreated/SaleCompleted/SaleCancelled
        +-- Sales.Contracts        [DONE] - ISalesService, ISalesReader (cross-module public API);
        |                                    CreateSaleResult, AddSaleItemResult, SaleOperationResult,
        |                                    SaleSummaryResult, SaleStatusContract
        +-- Sales.Application      [DONE] - Commands: CreateSale, AddSaleItem, ConfirmSale, CompleteSale,
        |                                    CancelSale; Queries: GetSaleById, GetAllSales;
        |                                    Repositories: ISaleRepository, IReturnRepository,
        |                                    ISalesTransactionRepository; ISalesUnitOfWork; SaleDto
        +-- Sales.Infrastructure   [DONE] - SalesDbContext (sal_ prefix), 5 EF configurations,
        |                                    internal EF repositories, SalesUnitOfWork, SalesReader,
        |                                    SalesService, SalesModule (IModule), SalesModuleManifest,
        |                                    SalesHostingModule (IHostingModule),
        |                                    SalesDatabaseInitializer (IHostedService),
        |                                    SalesServicesExtensions, Migration: InitialSalesSchema
        +-- Sales.UI               [DONE] - SaleListViewModel (net10.0-windows)

tests/
+-- Architecture.Tests             [DONE] - 95 tests, all passing (Stages 1-5C)
+-- Platform.Infrastructure.Tests  [DONE] - 19 tests, all passing (Stage 3)
+-- Platform.ModuleContract.Tests  [DONE] - 112 tests, all passing (Stage 4)
+-- Catalog.Tests                  [DONE] - 64 tests, all passing (Stage 5A)
+-- Inventory.Tests                [DONE] - 81 tests, all passing (Stage 5B)
+-- Sales.Tests                    [DONE] - 103 tests, all passing (Stage 5C)

Planned:
src/Modules/POS/      - Stage 5D
src/OptionalModules/  - Stage 8
src/Cloud/            - Stage 6
tests/Integration.Tests - TBD
tools/                  - TBD

---

## Stage 4 Architectural Decisions

### Decision 1: IHostingModule vs IModule - REMAIN SEPARATE

These are intentionally different concerns and must not be conflated:

| Concern | Interface | Layer | Purpose |
|---------|-----------|-------|---------|
| DI registration at host-build time | IHostingModule | Client.Host | Wires services into DI container |
| Business module runtime lifecycle | IModule | Platform.Core | Initialize/Start/Stop the module |

A future business module will implement both:
- `IHostingModule.RegisterServices()` - wires its services, DbContext, repositories into DI
- `IModule.InitializeAsync/StartAsync/StopAsync` - manages runtime operation

### Decision 2: Layer placement for module contracts

Per architecture section 6.1, Platform.Core contains IModule, IModuleManifest, IFeature.
Platform.Application contains application-level services (IModuleRegistry, IModuleDependencyResolver).
This is correct per the dependency direction: UI -> Application -> Domain <- Infrastructure.

### Decision 3: ModuleVersion - custom value type, not NuGet

Architecture requires modules to be versionable independently from any package manager.
ModuleVersion is a simple Major.Minor.Patch value type. It:
- Supports comparison operators for dependency range checks
- Does NOT couple to NuGet, SemVer libraries, or any package manager
- Supports TryParse/Parse from strings

### Decision 4: IModuleManifest - runtime-only; no PackageHash/Signature

Architecture section 38 includes PackageHash and Signature in the manifest concept. However:
- PackageHash/Signature are package verification concerns (Stage 7, Client.Updater)
- They do NOT belong in the runtime module contract
- The runtime manifest is what a running module exposes to the platform

Stage 7 will define IModulePackageMetadata or similar for the installable package layer.

### Decision 5: ModuleRuntimeStatus - runtime lifecycle only

Architecture section 39 defines the full module lifecycle:
  Available -> Downloaded -> Verified -> Installed -> Registered -> Licensed -> Enabled -> Running

This was decomposed:
- Package lifecycle: Available -> Downloaded -> Verified -> Installed -> Stage 7 (Client.Updater)
- Runtime lifecycle: Registered -> Enabled -> Running -> Stopped/Disabled/Suspended/Faulted -> Stage 4
- Licensing lifecycle: Licensed/Unlicensed -> Stage 6 (Client.Licensing)

### Decision 6: IModule has no ConfigureServices()

Keeping Platform.Core free of Microsoft.Extensions.DependencyInjection was essential.
DI wiring stays exclusively with IHostingModule. This preserves Platform.Core portability.

### Decision 7: ModuleDependencyResolver is pure in-memory

The resolver operates entirely on IModuleManifest objects with no I/O, file system access,
or network calls. It is a deterministic DFS topological sort. This satisfies section 41 requirement
for dependency resolution while deliberately NOT being a package manager.

---

## Database Architecture Decision (Stage 3)

DECISION: Multiple DbContexts sharing one SQLite file (Option B)

Rationale: Architecture doc section 32 ("logical ownership belongs to modules") and section 36
("each module owns its schema migrations") mandate module-owned persistence.

- One physical SQLite file: %LOCALAPPDATA%\GenericPOS\genericpos.db
- PlatformDbContext: currently empty. Future: platform-level tables (audit, migration log) if needed.
- Future module DbContexts: each module owns its own DbContext and migrations (Stage 5+).
- Platform coordinates migration execution but does NOT own business schema.

Option A (one global AppDbContext) was REJECTED: breaks module ownership.
Option C (single shared AppDbContext) was REJECTED: same reasons as A.

---

## Database Configuration

Location: %LOCALAPPDATA%\GenericPOS\genericpos.db
Strategy: DatabaseFolder = "LocalAppData" (Environment.SpecialFolder.LocalApplicationData)
Reason: Always writable without elevation; survives application updates; standard Windows pattern.

Configuration section (appsettings.json):
{
  "Database": {
    "DatabaseFileName": "genericpos.db",
    "DatabaseFolder": "LocalAppData",
    "ApplicationSubDirectory": "GenericPOS"
  }
}

Alternative supported: "CommonApplicationData" for shared multi-user scenarios.
Alternative supported: "Custom" with absolute path for enterprise deployments.

---

## Project References (Architecture-Justified)

PLATFORM (Stages 1-4):
Platform.Contracts -> Platform.Core
Platform.Application -> Platform.Core, Platform.Contracts
Platform.Infrastructure -> Platform.Core, Platform.Application, Microsoft.EntityFrameworkCore.Sqlite

CLIENT (Stages 2-4):
Client.Host -> Platform.Core, Platform.Application, Platform.Infrastructure
  Reason: Client.Host IS the composition root. The composition root is permitted to
  reference implementation assemblies. Client.Host types do NOT use EF Core directly.

Client.ModuleHost -> Client.Host, Platform.Core, Platform.Application
Client.Desktop -> Client.Host, Client.ModuleHost, Catalog.Infrastructure, Catalog.UI,
                  Inventory.Infrastructure, Inventory.UI  <- Inventory references added Stage 5B
                  Sales.Infrastructure, Sales.UI          <- Sales references added Stage 5C
Client.Licensing -> Platform.Core (boundary only)
Client.Updater -> Platform.Core (boundary only)

INVENTORY MODULE (Stage 5B):
Inventory.Domain -> Platform.Core
Inventory.Contracts -> Platform.Core
Inventory.Application -> Inventory.Domain, Inventory.Contracts, Platform.Core, Platform.Application,
                          Catalog.Contracts  <- cross-module contract dependency (allowed)
Inventory.Infrastructure -> Inventory.Domain, Inventory.Application, Inventory.Contracts,
                             Platform.Core, Platform.Infrastructure, Client.Host,
                             Microsoft.EntityFrameworkCore.Sqlite

SALES MODULE (Stage 5C):
Sales.Domain -> Platform.Core
Sales.Contracts -> Platform.Core
Sales.Application -> Sales.Domain, Sales.Contracts, Platform.Core, Platform.Application,
                      Catalog.Contracts, Inventory.Contracts  <- cross-module contract dependencies (allowed)
Sales.Infrastructure -> Sales.Domain, Sales.Application, Sales.Contracts, Platform.Core,
                         Platform.Infrastructure, Client.Host, Catalog.Contracts, Inventory.Contracts,
                         Microsoft.EntityFrameworkCore.Sqlite
Sales.UI -> Sales.Application, Sales.Contracts, Platform.Core

TESTS:
Architecture.Tests -> all Platform + non-WPF Client + non-WPF Catalog + Inventory + Sales projects
Platform.Infrastructure.Tests -> Platform.Infrastructure, Platform.Application
Platform.ModuleContract.Tests -> Platform.Core, Platform.Application, Client.ModuleHost
Catalog.Tests -> Catalog.Domain, Catalog.Application, Catalog.Infrastructure, Catalog.Contracts
Sales.Tests -> Sales.Domain, Sales.Application, Sales.Infrastructure, Sales.Contracts,
               Catalog.Contracts, Inventory.Contracts, Platform.Infrastructure (Catalog/Inventory stubbed)
Inventory.Tests -> Inventory.Domain, Inventory.Application, Inventory.Infrastructure,
                   Inventory.Contracts, Catalog.Contracts, Platform.Infrastructure

---

## DI Registrations (Cumulative)

Stage 2:
- IModuleDiscoveryService -> FileSystemModuleDiscoveryService (Singleton)
- MainWindow (Transient)

Stage 3 (added via AddPlatformInfrastructure):
- PlatformDbContext (Scoped, SQLite-backed, path from DatabaseOptions)
- IUnitOfWork -> PlatformUnitOfWork (Scoped)
- PlatformUnitOfWork (Scoped, also registered directly for testing)
- DatabaseInitializer (Scoped)
- DatabaseInitializerService (IHostedService, runs during IHost.StartAsync())

Stage 4 (added via ModuleHostRegistrar):
- IModuleRegistry -> ModuleRegistry (Singleton)
- IModuleDependencyResolver -> ModuleDependencyResolver (Singleton)

Stage 5A (added via CatalogHostingModule / CatalogServicesExtensions):
- CatalogDbContext (Scoped, same SQLite file as PlatformDbContext)
- ICatalogUnitOfWork -> CatalogUnitOfWork (Scoped)
- IProductRepository -> EfProductRepository (Scoped)
- ICategoryRepository -> EfCategoryRepository (Scoped)
- IUnitRepository -> EfUnitRepository (Scoped)
- IBarcodeRepository -> EfBarcodeRepository (Scoped)
- IProductLookup -> CatalogProductLookup (Scoped)
- IProductBarcodeResolver -> CatalogBarcodeResolver (Scoped)
- IModule -> CatalogModule (Singleton)
- CatalogDatabaseInitializer (IHostedService, Singleton)
- CreateProductCommandHandler, UpdateProductCommandHandler, DeactivateProductCommandHandler (Transient)
- CreateCategoryCommandHandler, CreateUnitCommandHandler, AssignBarcodeCommandHandler (Transient)
- GetProductByIdQueryHandler, GetProductBySkuQueryHandler, FindProductByBarcodeQueryHandler (Transient)
- GetAllCategoriesQueryHandler, GetAllUnitsQueryHandler (Transient)

Stage 5B (added via InventoryHostingModule / InventoryServicesExtensions):
- InventoryDbContext (Scoped, same SQLite file as PlatformDbContext and CatalogDbContext)
- IInventoryUnitOfWork -> InventoryUnitOfWork (Scoped)
- IWarehouseRepository -> EfWarehouseRepository (Scoped)
- ILocationRepository -> EfLocationRepository (Scoped)
- IStockItemRepository -> EfStockItemRepository (Scoped)
- IStockMovementRepository -> EfStockMovementRepository (Scoped)
- IStockAdjustmentRepository -> EfStockAdjustmentRepository (Scoped)
- IInventoryBalanceRepository -> EfInventoryBalanceRepository (Scoped)
- IInventoryReader -> InventoryReader (Scoped)
- IStockAvailabilityChecker -> StockAvailabilityChecker (Scoped)
- IStockMovementReader -> StockMovementReader (Scoped)
- IModule -> InventoryModule (Singleton)
- InventoryDatabaseInitializer (IHostedService, Singleton)
- CreateWarehouseCommandHandler (Transient)
- CreateLocationCommandHandler (Transient)
- AddStockCommandHandler (Transient)
- AdjustStockCommandHandler (Transient)
- GetWarehousesQueryHandler (Transient)
- GetStockLevelQueryHandler (Transient)
- GetAllStockLevelsQueryHandler (Transient)
- GetStockMovementsQueryHandler (Transient)

Stage 5C (added via SalesHostingModule / SalesServicesExtensions):
- SalesDbContext (Scoped, same SQLite file as the other module DbContexts)
- ISalesUnitOfWork -> SalesUnitOfWork (Scoped)
- ISaleRepository -> EfSaleRepository, IReturnRepository -> EfReturnRepository,
  ISalesTransactionRepository -> EfSalesTransactionRepository (Scoped)
- ISalesReader -> SalesReader, ISalesService -> SalesService (Scoped)
- IModule -> SalesModule (Singleton)
- SalesDatabaseInitializer (IHostedService, Singleton)
- CreateSale/AddSaleItem/ConfirmSale/CompleteSale/CancelSale command handlers (Transient)
- GetSaleById/GetAllSales query handlers (Transient)

---

## Migration Strategy

PlatformDbContext: No EF Core migrations created. PlatformDbContext has no entities.
EnsureCreated() is used as a fallback. When first platform entity is needed:
  dotnet ef migrations add InitialCreate -p src/Platform/Platform.Infrastructure -s src/Client/Client.Desktop

Catalog migrations (Stage 5A): <- ACTIVE
  Migration: CatalogInitialCreate (20260915165518_CatalogInitialCreate)
  Tables: cat_Categories, cat_Products, cat_Units, cat_Barcodes
  Location: src/Modules/Catalog/Catalog.Infrastructure/Migrations/
  Command to regenerate:
    dotnet ef migrations add {Name} -p src/Modules/Catalog/Catalog.Infrastructure -s src/Client/Client.Desktop --context CatalogDbContext
  Applied by: CatalogDatabaseInitializer (IHostedService) at startup

Inventory migrations (Stage 5B): <- ACTIVE
  Migration: InitialInventorySchema (20260923132735_InitialInventorySchema)
  Tables: inv_Warehouses, inv_Locations, inv_StockItems, inv_StockMovements,
          inv_StockAdjustments, inv_InventoryBalances
  Table ownership: all inv_* tables are exclusively owned by InventoryDbContext.
                   No other module may read or write these tables directly.
  Location: src/Modules/Inventory/Inventory.Infrastructure/Migrations/
  Command to regenerate:
    dotnet ef migrations add {Name} -p src/Modules/Inventory/Inventory.Infrastructure -s src/Client/Client.Desktop --context InventoryDbContext
  Applied by: InventoryDatabaseInitializer (IHostedService) at startup

Sales migrations (Stage 5C): <- ACTIVE
  Migration: InitialSalesSchema (20261004185047_InitialSalesSchema)
  Tables: sal_Sales, sal_SaleItems, sal_Returns, sal_ReturnItems, sal_SalesTransactions
  Table ownership: all sal_* tables are exclusively owned by SalesDbContext.
                   Foreign keys exist only between Sales-owned tables (no cross-module FKs).
  Location: src/Modules/Sales/Sales.Infrastructure/Migrations/
  Command to regenerate:
    dotnet ef migrations add {Name} -p src/Modules/Sales/Sales.Infrastructure -s src/Client/Client.Desktop --context SalesDbContext
  Applied by: SalesDatabaseInitializer (IHostedService) at startup

Future module migrations follow the same pattern with their own prefix:
  POS:   pos_ prefix, POS.Infrastructure/Migrations/

---

## Startup Sequence (Updated for Stage 5C)

WPF App.OnStartup
  -> ApplicationHostBuilder.Create()
     .WithModule(new DesktopServicesRegistrar())    // registers MainWindow
     .WithModule(new ModuleHostRegistrar())          // registers IModuleDiscoveryService,
     .WithModule(new CatalogHostingModule())         //   IModuleRegistry, IModuleDependencyResolver
     .WithModule(new InventoryHostingModule())       // registers all Inventory services  <- Stage 5B
     .WithModule(new SalesHostingModule())           // registers all Sales services      <- Stage 5C
     .Build()
  -> host.StartAsync()
       -> DatabaseInitializerService.StartAsync()   // Platform DB (EnsureCreated, no migrations)
            -> DatabaseInitializer.InitializeAsync()
            -> SQLite database created at %LOCALAPPDATA%\GenericPOS\genericpos.db
       -> CatalogDatabaseInitializer.StartAsync()   // Catalog DB migrations
            -> Applies CatalogInitialCreate migration (cat_Products, cat_Categories, etc.)
       -> InventoryDatabaseInitializer.StartAsync() // Inventory DB migrations  <- Stage 5B
            -> Applies InitialInventorySchema migration (inv_Warehouses, inv_Locations, etc.)
       -> SalesDatabaseInitializer.StartAsync()     // Sales DB migrations  <- Stage 5C
            -> Applies InitialSalesSchema migration (sal_Sales, sal_SaleItems, etc.)
  -> Services.GetRequiredService<MainWindow>()
  -> mainWindow.Show()

WPF App.OnExit
  -> host.StopAsync()

Note: Hosted service execution order is determined by registration order in DI.
DatabaseInitializerService is registered by AddPlatformInfrastructure (Stage 3).
CatalogDatabaseInitializer is registered by AddCatalogModule (Stage 5A).
InventoryDatabaseInitializer is registered by AddInventoryModule (Stage 5B).
SalesDatabaseInitializer is registered by AddSalesModule (Stage 5C).

---

## Architecture Tests Status (Cumulative)

Stage 1 Tests (22):
  ARCH-001: Platform cannot reference business modules (4 tests)
  ARCH-002: Domain cannot reference Infrastructure/EF Core/SQLite (4 tests)
  ARCH-003: Domain cannot reference UI (3 tests)
  ARCH-004: Application cannot reference UI (3 tests)
  ARCH-009: Business modules cannot require HTTP (3 tests)
  ARCH-010: Circular dependencies forbidden (1 test - expanded to all assemblies in Stage 2)
  Deferred stubs: ARCH-005, ARCH-006, ARCH-007, ARCH-008 (4 stubs)

Stage 2 Tests (10):
  Client.Host boundaries (EF Core, SQLite, ASP.NET Core)
  Client.ModuleHost boundaries (EF Core, Infrastructure)
  Client.Licensing boundaries (EF Core, ASP.NET Core)
  Client.Updater boundaries (EF Core)
  ARCH-008 stub (Client.Desktop TFM deferred)

Stage 3 Tests (9):
  Stage3-DB-001: Platform.Core must not reference EF Core (reconfirmed)
  Stage3-DB-002: Platform.Application must not reference EF Core (reconfirmed)
  Stage3-DB-003: Platform.Contracts must not reference EF Core
  Stage3-DB-004: Client.ModuleHost must not reference EF Core (with EF Core present)
  Stage3-DB-005: Client.Licensing must not reference EF Core (with EF Core present)
  Stage3-DB-006: Client.Updater must not reference EF Core (with EF Core present)
  ARCH-008 (active): Client.ModuleHost must not reference Platform.Infrastructure
  ARCH-008 (active): Client.Host types must not use EF Core directly (composition root boundary)
  ARCH-008 (deferred): Client.Desktop deferred (TFM gap)

Stage 4 Tests (7):
  Stage4: Platform.Core module contracts must not reference EF Core
  Stage4: Platform.Core module contracts must not reference WPF
  Stage4: Platform.Core module contracts must not reference ASP.NET Core
  Stage4: Platform.Application module services must not reference EF Core
  Stage4: Platform.Application module services must not reference WPF
  Stage4: Client.ModuleHost must still not reference Platform.Infrastructure
  Stage4: Client.ModuleHost must still not reference EF Core

Stage 5A Tests (19):
  ARCH-007a-g: Catalog layer dependency direction (7 tests)
  ARCH-DB-01/02/03: Catalog Domain/Application/Contracts must not reference EF Core (3 tests)
  ARCH-WPF-01/02/03: Catalog Domain/Application/Contracts must not reference WPF (3 tests)
  ARCH-001e/f/g/h: Platform assemblies must not reference Catalog (4 tests)
  ARCH-005: Catalog.Infrastructure must not reference Inventory/Sales (2 tests)

Stage 5B Tests (12) - NEW:
  ARCH-INV-001: Inventory.Domain must not depend on EF Core
  ARCH-INV-002: Inventory.Domain must not depend on WPF
  ARCH-INV-003: Inventory.Domain must not depend on Catalog.Domain
  ARCH-INV-004: Inventory.Contracts must not depend on Catalog.Domain
  ARCH-INV-005: Inventory.Application must not depend on Catalog.Application
  ARCH-INV-006: Inventory.Application must not depend on Catalog.Infrastructure
  ARCH-INV-007: Inventory.Infrastructure must not depend on Catalog.Domain
  ARCH-INV-008: Inventory.Infrastructure must not depend on Catalog.Application
  ARCH-INV-009: Catalog must not depend on Inventory (2 test methods: CatalogDomain + CatalogApplication)
  ARCH-INV-010: Platform must not depend on Inventory (iterates all Platform assemblies)
  ARCH-INV-011: Inventory.Domain must not depend on HTTP

Stage 5C Tests (16) - NEW (tests/Architecture.Tests/DependencyRules/SalesBoundaryTests.cs):
  ARCH-SAL-001/002/003/004: Sales.Domain must not depend on EF Core / WPF / HTTP+ASP.NET / Sales.Infrastructure
  ARCH-SAL-005/006: Sales.Application must not depend on Sales.Infrastructure / Sales.UI / EF Core
  ARCH-SAL-007: Sales.Contracts must not depend on Sales.Domain / Application / Infrastructure
  ARCH-SAL-008: Sales must not reference Catalog/Inventory Domain, Application, Infrastructure or UI
  ARCH-SAL-009: Sales.Domain and Sales.Contracts must not reference Catalog or Inventory at all
  ARCH-SAL-010: Sales.Application consumes Catalog/Inventory through Contracts only
  ARCH-SAL-011/012: Sales.Infrastructure depends on Sales inner layers; not on Sales.UI
  ARCH-SAL-013/014/015: Platform, Catalog and Inventory must not depend on Sales
  ARCH-SAL-016: Sales.Application and Sales.Domain must not depend on HTTP

Total Architecture.Tests: 95 tests, all PASSING.
Platform.Infrastructure.Tests: 19 tests, all PASSING.
Platform.ModuleContract.Tests: 112 tests, all PASSING.
Catalog.Tests: 64 tests, all PASSING.
Inventory.Tests: 81 tests, all PASSING.
Sales.Tests: 103 tests, all PASSING (Domain, Application, Infrastructure incl. migration, Contracts).
Grand total: 474 tests, 0 failures.

ARCH-005, ARCH-006, ARCH-007: ACTIVE and passing (activated with Stage 5A).
ARCH-INV-001 through ARCH-INV-011: ACTIVE and passing (activated with Stage 5B).
ARCH-SAL-001 through ARCH-SAL-016: ACTIVE and passing (activated with Stage 5C).

---

## Packages Added (Cumulative)

Project | Package | Version | Reason
--------|---------|---------|-------
Client.Host | Microsoft.Extensions.Hosting | 10.0.x | DI, config, logging host
Architecture.Tests | NetArchTest.Rules | 1.3.2 | Architecture boundary testing
Platform.Infrastructure | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | SQLite provider
Platform.Infrastructure | Microsoft.EntityFrameworkCore.Design | 10.0.11 | Migration tooling (PrivateAssets=all)
Platform.Infrastructure | Microsoft.Extensions.Configuration.Binder | 10.0.x | DatabaseOptions binding
Platform.Infrastructure.Tests | Microsoft.EntityFrameworkCore.InMemory | 10.0.11 | (added but unused - using file:memory SQLite instead)

No new NuGet packages were added in Stage 4.

---

## Packages Added in Stage 5A

Project | Package | Version | Reason
--------|---------|---------|-------
Catalog.Infrastructure | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | Module-owned SQLite persistence
Catalog.Infrastructure | Microsoft.EntityFrameworkCore | 10.0.11 | (transitive)
Catalog.Infrastructure | Microsoft.Extensions.Hosting.Abstractions | 10.0.11 | IHostedService
Catalog.Tests | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | In-memory SQLite tests
Client.Desktop | Microsoft.EntityFrameworkCore.Design | 10.0.11 | EF migration tool support (PrivateAssets=all)

---

## Packages Added in Stage 5B

Project | Package | Version | Reason
--------|---------|---------|-------
Inventory.Infrastructure | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | Module-owned SQLite persistence
Inventory.Infrastructure | Microsoft.EntityFrameworkCore.Design | 10.0.11 | Migration tooling (PrivateAssets=all)
Inventory.Infrastructure | Microsoft.Extensions.Configuration.Binder | 10.0.11 | DatabaseOptions binding
Inventory.Infrastructure | Microsoft.Extensions.Hosting.Abstractions | 10.0.11 | IHostedService
Inventory.Tests | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | In-memory SQLite tests
Inventory.Tests | Microsoft.Extensions.DependencyInjection | 10.0.11 | DI container for integration tests
Inventory.Tests | Microsoft.Extensions.Logging.Abstractions | 10.0.11 | ILogger for InventoryDatabaseInitializer
Inventory.Tests | Microsoft.Extensions.Configuration.Json | 10.0.11 | Configuration loading in tests

---

## Packages Added in Stage 5C

Project | Package | Version | Reason
--------|---------|---------|-------
Sales.Infrastructure | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | Module-owned SQLite persistence
Sales.Infrastructure | Microsoft.EntityFrameworkCore.Design | 10.0.11 | Migration tooling (PrivateAssets=all)
Sales.Infrastructure | Microsoft.Extensions.Configuration.Binder | 10.0.11 | DatabaseOptions binding
Sales.Infrastructure | Microsoft.Extensions.Hosting.Abstractions | 10.0.11 | IHostedService
Sales.Tests | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | In-memory SQLite tests
Sales.Tests | Microsoft.Extensions.DependencyInjection | 10.0.11 | DI container for integration tests
Sales.Tests | Microsoft.Extensions.Logging.Abstractions | 10.0.11 | ILogger for SalesDatabaseInitializer

---

## Architectural Implementation Stages

Stage | Name                                                  | Status
------|-------------------------------------------------------|------------
1     | Solution Foundation (Platform projects)               | COMPLETE
2     | Client Host (DI, config, logging, module discovery)   | COMPLETE
3     | Database Foundation (SQLite, EF Core, migrations)     | COMPLETE
4     | Module Contract (IModule, IModuleManifest, lifecycle) | COMPLETE
5A    | Catalog Module (canonical module pattern)             | COMPLETE
5B    | Inventory Module                                      | COMPLETE
5C    | Sales Module                                          | COMPLETE
5D    | POS Module                                            | Not Started
6     | Licensing (LicenseServer, Client.Licensing)           | Not Started
7     | Update System (packages, signatures, rollback)        | Not Started
8     | Additional Business Modules                           | Not Started

---

## Important Architectural Constraints (Unchanged)

1. Platform must NEVER depend on business modules.
2. Domain must NEVER depend on Infrastructure, UI, EF Core, SQLite, WPF, HTTP.
3. Cross-module communication through Contracts only.
4. UI must NEVER directly access DbContext or repositories.
5. Business logic must NEVER depend on HTTP.
6. Cloud availability must NEVER be required for offline POS operation.
7. A module must NEVER modify another module's database tables directly.
8. Circular module dependencies are forbidden.
9. Optional modules must not be required by the core.
10. Data must never be destroyed due to license expiration.
11. Modules and updates must be cryptographically signed.
12. Each module owns its own EF Core migrations.
13. Cross-module entity rule: never pass another module's domain entity across a boundary.
14. Historical data rule: sale prices must snapshot values at transaction time.

---

## Important Decisions Made

1. TARGET FRAMEWORK: net10.0 (net10.0-windows for WPF).
2. SOLUTION FORMAT: Traditional .sln.
3. IModule/IModuleManifest: COMPLETE in Stage 4. See Stage 4 Architectural Decisions above.
4. CQRS: ICommand, IQuery, IRequest, IRequestHandler in Platform.Application.
5. RESULT PATTERN: Result<T> for all expected business failures.
6. NetArchTest.Rules 1.3.2 for architecture tests.
7. IHostingModule vs IModule: separate concerns - both are needed (see Decision 1 above).
8. WPF STARTUP PATTERN: Removed StartupUri, DI host first.
9. CLIENT.DESKTOP TFM GAP: net10.0-windows cannot be tested by net10.0 Architecture.Tests.
10. MODULE DISCOVERY CONVENTION: modules/{Name}/{Name}.Infrastructure.dll.
11. DATABASE ARCHITECTURE: Multiple DbContexts per module, one SQLite file.
12. DATABASE LOCATION: LocalAppData\GenericPOS\genericpos.db (writable, survives updates).
13. PLATFORMUNITOFWORK DISPOSAL: implements both IDisposable and IAsyncDisposable.
    Sync Dispose() delegates to DisposeAsync().AsTask().GetAwaiter().GetResult().
    This is safe because scope disposal occurs on normal threads, not in async contexts.
14. EF CORE EMPTY MODEL: PlatformDbContext has no entities in Stage 3/4. EnsureCreated()
    creates the database file. When migrations are added later, MigrateAsync() takes over.
15. COMPOSITION ROOT REFERENCE: Client.Host references Platform.Infrastructure as the
    composition root. This is architecturally correct per DIP. Client.Host types themselves
    do NOT use EF Core - only PlatformServicesExtensions calls AddPlatformInfrastructure().
16. MODULE VERSION: Custom ModuleVersion value type (Major.Minor.Patch), NOT NuGet.
    Reason: architecture must not couple module versioning to any package manager.
17. MANIFEST HASH/SIGNATURE EXCLUSION: PackageHash and Signature are NOT in IModuleManifest.
    They belong in the package/update layer (Stage 7, Client.Updater).
18. DEPENDENCY RESOLVER: Pure in-memory DFS algorithm with cycle detection.
    NOT a package manager. Operates entirely on IModuleManifest instances.
19. IMODULE NO ConfigureServices: DI wiring stays with IHostingModule to keep
    Platform.Core free of Microsoft.Extensions.DependencyInjection dependency.
20. MODULERUNTIME STATUS: Runtime lifecycle only (Registered/Enabled/Running/Stopped/
    Disabled/Suspended/Faulted). Package lifecycle (Stage 7) and License lifecycle
    (Stage 6) are separate enums in their respective stages.

---

## Stage 5A Architectural Decisions

### Decision 1: CatalogHostingModule vs CatalogModule - same pattern as Stage 4

CatalogHostingModule (IHostingModule): Registered in App.xaml.cs, wires all Catalog DI services.
CatalogModule (IModule): Runtime lifecycle (Initialize/Start/Stop). Singleton, no async init.
CatalogDatabaseInitializer (IHostedService): Applies Catalog EF migrations at startup.

### Decision 2: CatalogDbContext owns cat_ tables exclusively

All Catalog tables use the cat_ prefix: cat_Products, cat_Categories, cat_Units, cat_Barcodes.
No other module may reference or modify these tables directly.
Other modules access product data only through Catalog.Contracts (IProductLookup, IProductBarcodeResolver).

### Decision 3: InternalsVisibleTo("Catalog.Tests") in Catalog.Infrastructure

CatalogTestDatabase directly registers concrete implementations (EfProductRepository, etc.)
for integration tests. InternalsVisibleTo is the same pattern used in Platform.Infrastructure.

### Decision 4: EF LINQ with value objects

Value object properties accessed as p.Id == new ProductId(guid) - not p.Id.Value == guid.
Reason: EF Core can translate typed value object comparisons (it has a registered converter)
but cannot translate .Value property access inside LINQ predicates.

### Decision 5: Catalog.UI excluded from Architecture.Tests

Catalog.UI targets net10.0-windows. Architecture.Tests targets net10.0.
The TFM gap prevents direct assembly load. Catalog.UI boundaries are enforced via .csproj inspection.
This is the same documented exception as Client.Desktop.

---

## Stage 5B Architectural Decisions

### Decision 1: InventoryHostingModule vs InventoryModule - same pattern as Catalog

InventoryHostingModule (IHostingModule): Registered in App.xaml.cs, wires all Inventory DI services.
InventoryModule (IModule): Runtime lifecycle (Initialize/Start/Stop). Singleton, no async init.
InventoryDatabaseInitializer (IHostedService): Applies Inventory EF Core migrations at startup.

### Decision 2: InventoryDbContext owns inv_* tables exclusively

All Inventory tables use the inv_ prefix:
  inv_Warehouses, inv_Locations, inv_StockItems, inv_StockMovements,
  inv_StockAdjustments, inv_InventoryBalances
No other module may reference or modify these tables directly.
Other modules access inventory data only through Inventory.Contracts
(IInventoryReader, IStockAvailabilityChecker, IStockMovementReader).

### Decision 3: Inventory -> Catalog.Contracts only (never Catalog.Domain/Application/Infrastructure)

Inventory.Application references Catalog.Contracts for product identity used in stock items.
This is the correct cross-module dependency: contracts only, never internal layers.
Verified by ARCH-INV-003 through ARCH-INV-008 (all passing).

### Decision 4: InternalsVisibleTo("Inventory.Tests") in Inventory.Infrastructure

InventoryTestDatabase directly registers concrete EF repositories for integration tests.
Same pattern as Catalog.Tests and Platform.Infrastructure.Tests.

### Decision 5: Inventory.UI excluded from Architecture.Tests

Inventory.UI targets net10.0-windows. Architecture.Tests targets net10.0.
Same documented TFM gap exception as Catalog.UI and Client.Desktop.

---

## Known Issues / Blockers

None blocking. Stage 5C is complete.
Build: 0 errors, 0 warnings (30 projects).
All 474 tests pass.
Catalog, Inventory and Sales database schemas are applied at startup by their initializers.
Database: %LOCALAPPDATA%\GenericPOS\genericpos.db (Platform + Catalog + Inventory + Sales tables in same file).

Notes:
- Stage 5C fixed a defect in SalesReader.FindByIdAsync (EF could not translate `s.Id.Value == guid`
  against the SaleId value converter; now compares `s.Id == new SaleId(guid)`).
- Sales.UI is not covered by Architecture.Tests (net10.0-windows TFM gap, same as other modules).
- CompleteSaleCommandHandler does not yet reduce stock or process payments (Stage 5D / Payments).
- Sales design: Sale/SaleItem/Return/ReturnItem/SalesTransaction. The earlier planning names
  SalesOrder/SalesOrderLine/SalePayment were superseded; Payments is a separate future module.
- Header text "Cloud Backend ... Stage 6" is stale: roadmap is 6 Licensing, 7 Updates, 9 Cloud.

---

Last updated: 2026-10-04 - Stage 5C complete. Sales module implemented, migrated and tested.
