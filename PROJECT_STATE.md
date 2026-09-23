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

**Stage 5B COMPLETE: Inventory Module**

The Inventory module is fully implemented following the canonical module pattern (established in Stage 5A).
All five Inventory layers (Domain, Application, Contracts, Infrastructure, UI) are implemented and tested.
Inventory.UI (WPF) is implemented but not architecture-tested due to the net10.0-windows TFM gap.
The Inventory module is integrated into the application host via InventoryHostingModule.
EF Core migration `InitialInventorySchema` is generated (all 6 tables with `inv_` prefix).
Architecture tests ARCH-INV-001 through ARCH-INV-011 are active and passing.

---

## Completed Work

### Stage 1 — Solution Foundation
- [x] Read all three architecture documents
- [x] Created GenericPOS.sln
- [x] Created src/Platform/{Core,Contracts,Application,Infrastructure}
- [x] Created tests/Architecture.Tests
- [x] Implemented Platform.Core primitives (Result, Error, EntityId, ModuleId, FeatureId, DomainEvent)
- [x] Implemented Platform.Contracts (IDomainEventPublisher, IDomainEventHandler)
- [x] Implemented Platform.Application (ICommand, IQuery, IRequest, IRequestHandler, IUnitOfWork, ICurrentUser)
- [x] Platform.Infrastructure: Assembly marker only (Stage 3 adds implementations)
- [x] 22 architecture tests passing

### Stage 2 — Client Host
- [x] Created src/Client/{Host,ModuleHost,Desktop,Licensing,Updater}
- [x] Implemented DI, configuration, logging, module discovery, WPF shell
- [x] Application starts, window appears, logs correctly, shuts down gracefully
- [x] Added 10 architecture tests (32 total passing)

### Stage 3 — Database Foundation
- [x] Added Microsoft.EntityFrameworkCore.Sqlite 10.0.11 to Platform.Infrastructure
- [x] Added Microsoft.EntityFrameworkCore.Design 10.0.11 to Platform.Infrastructure
- [x] Added Microsoft.Extensions.Configuration.Binder to Platform.Infrastructure
- [x] Added [InternalsVisibleTo("Platform.Infrastructure.Tests")] to Platform.Infrastructure
- [x] Implemented PlatformDbContext (empty — no business tables in Stage 3)
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

### Stage 4 — Module Contract
- [x] Implemented ModuleVersion (Major.Minor.Patch value type, custom — not NuGet)
- [x] Implemented VersionRange (constraint model: >=, <=, ==, >, <)
- [x] Implemented ModuleDependency (RequiredModuleId + VersionRange)
- [x] Implemented IFeatureDescriptor (FeatureId + DisplayName)
- [x] Implemented ModuleRuntimeStatus enum (runtime lifecycle states only)
- [x] Implemented IModuleManifest (runtime manifest contract — hash/signature excluded as Stage 7)
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

### Stage 5B — Inventory Module
- [x] Created Inventory.Domain: Entities (Warehouse, Location, StockItem, StockMovement, StockAdjustment, InventoryBalance)
- [x] Created Inventory.Domain: Value Objects (WarehouseId, LocationId, StockItemId, StockMovementId, StockAdjustmentId, Quantity)
- [x] Created Inventory.Domain: Enums (MovementType, AdjustmentReason)
- [x] Created Inventory.Domain: Domain Events (WarehouseCreated, StockMovementRecorded, StockAdjusted)
- [x] Created Inventory.Contracts: DTOs (StockLevelDto, StockMovementDto, WarehouseDto)
- [x] Created Inventory.Contracts: Interfaces (IInventoryReader, IStockAvailabilityChecker, IStockMovementReader)
- [x] Created Inventory.Application: Repository interfaces (IWarehouseRepository, ILocationRepository, IStockItemRepository, IStockMovementRepository, IStockAdjustmentRepository, IInventoryBalanceRepository)
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
- [x] Updated GenericPOS.sln: added all Inventory projects + Inventory.Tests
- [x] Created tests/Inventory.Tests: InventoryTestDatabase + StubProductLookup
- [x] Created tests/Inventory.Tests: Domain tests (81 tests covering Warehouse, StockItem, Quantity, InventoryBalance, StockMovement, StockAdjustment)
- [x] Created tests/Inventory.Tests: Application tests (AddStockCommand, AdjustStockCommand integration tests)
- [x] Created tests/Inventory.Tests: Infrastructure tests (schema, contracts, availability checker)
- [x] Created tests/Inventory.Tests: Contracts tests (DTO boundary, interface shape)
- [x] Updated Architecture.Tests: added Inventory assemblies to Assemblies.cs and project references
- [x] Created Architecture.Tests/DependencyRules/InventoryBoundaryTests.cs (11 tests ARCH-INV-001 to ARCH-INV-011)
- [x] Generated EF Core migration: InitialInventorySchema (all 6 tables with inv_ prefix)
- [x] Build: 0 errors, 0 warnings
- [x] All 336 tests PASS (79 Architecture + 81 Inventory + 64 Catalog + 19 Infrastructure + 112 ModuleContract)
- [x] Inventory → Catalog.Contracts dependency only (Catalog.Domain never referenced)
- [x] Catalog → no Inventory dependency (no reverse)
- [x] Platform → no Inventory dependency
- [x] Inventory operates fully offline

---

## Current Task

**Stage 5B — COMPLETE. Stopping before Stage 5C — Sales.**

---

## Next Task

**Stage 5C - Sales Module (awaiting instruction)**

Sales module: SalesOrder, SalesOrderLine, SalePayment.
Sales consumes Catalog.Contracts (IProductLookup) and Inventory.Contracts (IStockAvailabilityChecker).
Follows the same canonical module pattern established in Stages 5A and 5B.

---

## Solution / Project Structure (Current State)

GenericPOS.sln (18 projects)

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
|   +-- Client.Desktop             [DONE] - WPF shell + Catalog module wired in (Stage 5A)
|   +-- Client.Licensing           [DONE] - Boundary marker only (Stage 6)
|   +-- Client.Updater             [DONE] - Boundary marker only (Stage 7)
|
+-- Modules/
    +-- Catalog/
        +-- Catalog.Domain         [DONE] - Product, Category, Unit, Barcode aggregates; domain events;
        |                                    strongly-typed IDs (ProductId, CategoryId, UnitId, BarcodeId)
        +-- Catalog.Contracts      [DONE] - IProductLookup, IProductBarcodeResolver, ProductLookupResult,
        |                                    ProductStatusContract (cross-module public API)
        +-- Catalog.Application    [DONE] - Commands: CreateProduct, UpdateProduct, DeactivateProduct,
        |                                    CreateCategory, CreateUnit, AssignBarcode
        |                                    Queries: GetProductById, GetProductBySku, FindProductByBarcode,
        |                                    GetAllCategories, GetAllUnits
        |                                    Repositories: IProductRepository, ICategoryRepository,
        |                                    IUnitRepository, IBarcodeRepository, ICatalogUnitOfWork
        |                                    DTOs: ProductDto, CategoryDto, UnitDto, BarcodeDto
        +-- Catalog.Infrastructure [DONE] - CatalogDbContext (cat_ prefix), EF configurations,
        |                                    EfProductRepository, EfCategoryRepository, EfUnitRepository,
        |                                    EfBarcodeRepository, CatalogUnitOfWork,
        |                                    CatalogProductLookup, CatalogBarcodeResolver,
        |                                    CatalogModule (IModule), CatalogModuleManifest,
        |                                    CatalogHostingModule (IHostingModule),
        |                                    CatalogDatabaseInitializer (IHostedService),
        |                                    CatalogServicesExtensions, Migration: CatalogInitialCreate
        +-- Catalog.UI             [DONE] - CreateProductViewModel, ProductListViewModel (net10.0-windows)

tests/
+-- Architecture.Tests             [DONE] - 67 tests, all passing (Stages 1-5A)
+-- Platform.Infrastructure.Tests  [DONE] - 19 tests, all passing (Stage 3)
+-- Platform.ModuleContract.Tests  [DONE] - 112 tests, all passing (Stage 4)
+-- Catalog.Tests                  [DONE] - 64 tests, all passing (Stage 5A)

Planned:
src/Modules/Inventory/ - Stage 5B
src/OptionalModules/   - Stage 8
src/Cloud/             - Stage 6
tests/Integration.Tests - TBD
tools/                  - TBD

---

## Stage 4 Architectural Decisions

### Decision 1: IHostingModule vs IModule — REMAIN SEPARATE

These are intentionally different concerns and must not be conflated:

| Concern | Interface | Layer | Purpose |
|---------|-----------|-------|---------|
| DI registration at host-build time | IHostingModule | Client.Host | Wires services into DI container |
| Business module runtime lifecycle | IModule | Platform.Core | Initialize/Start/Stop the module |

A future business module will implement both:
- `IHostingModule.RegisterServices()` — wires its services, DbContext, repositories into DI
- `IModule.InitializeAsync/StartAsync/StopAsync` — manages runtime operation

### Decision 2: Layer placement for module contracts

Per architecture §6.1, Platform.Core contains IModule, IModuleManifest, IFeature.
Platform.Application contains application-level services (IModuleRegistry, IModuleDependencyResolver).
This is correct per the dependency direction: UI → Application → Domain ← Infrastructure.

### Decision 3: ModuleVersion — custom value type, not NuGet

Architecture requires modules to be versionable independently from any package manager.
ModuleVersion is a simple Major.Minor.Patch value type. It:
- Supports comparison operators for dependency range checks
- Does NOT couple to NuGet, SemVer libraries, or any package manager
- Supports TryParse/Parse from strings

### Decision 4: IModuleManifest — runtime-only; no PackageHash/Signature

Architecture §38 includes PackageHash and Signature in the manifest concept. However:
- PackageHash/Signature are package verification concerns (Stage 7, Client.Updater)
- They do NOT belong in the runtime module contract
- The runtime manifest is what a running module exposes to the platform

Stage 7 will define IModulePackageMetadata or similar for the installable package layer.

### Decision 5: ModuleRuntimeStatus — runtime lifecycle only

Architecture §39 defines the full module lifecycle:
  Available → Downloaded → Verified → Installed → Registered → Licensed → Enabled → Running

This was decomposed:
- **Package lifecycle**: Available → Downloaded → Verified → Installed → Stage 7 (Client.Updater)
- **Runtime lifecycle**: Registered → Enabled → Running → Stopped/Disabled/Suspended/Faulted → Stage 4
- **Licensing lifecycle**: Licensed/Unlicensed → Stage 6 (Client.Licensing)

### Decision 6: IModule has no ConfigureServices()

Keeping Platform.Core free of Microsoft.Extensions.DependencyInjection was essential.
DI wiring stays exclusively with IHostingModule. This preserves Platform.Core portability.

### Decision 7: ModuleDependencyResolver is pure in-memory

The resolver operates entirely on IModuleManifest objects with no I/O, file system access,
or network calls. It is a deterministic DFS topological sort. This satisfies §41's requirement
for dependency resolution while deliberately NOT being a package manager.

---

## Database Architecture Decision (Stage 3)

DECISION: Multiple DbContexts sharing one SQLite file (Option B)

Rationale: Architecture doc §32 ("logical ownership belongs to modules") and §36
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

Client.ModuleHost -> Client.Host, Platform.Core, Platform.Application   ← Platform.Application added Stage 4
Client.Desktop -> Client.Host, Client.ModuleHost
Client.Licensing -> Platform.Core (boundary only)
Client.Updater -> Platform.Core (boundary only)

TESTS:
Architecture.Tests -> all Platform + non-WPF Client + non-WPF Catalog projects (reads assemblies for NetArchTest)
Platform.Infrastructure.Tests -> Platform.Infrastructure, Platform.Application
Platform.ModuleContract.Tests -> Platform.Core, Platform.Application, Client.ModuleHost
Catalog.Tests -> Catalog.Domain, Catalog.Application, Catalog.Infrastructure, Catalog.Contracts  ← NEW

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

Stage 5A (added via CatalogHostingModule / CatalogServicesExtensions):  ← NEW
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

---

## Migration Strategy

PlatformDbContext: No EF Core migrations created. PlatformDbContext has no entities.
EnsureCreated() is used as a fallback. When first platform entity is needed:
  dotnet ef migrations add InitialCreate -p src/Platform/Platform.Infrastructure -s src/Client/Client.Desktop

Catalog migrations (Stage 5A): ← ACTIVE
  Migration: CatalogInitialCreate (20260915165518_CatalogInitialCreate)
  Tables: cat_Categories, cat_Products, cat_Units, cat_Barcodes
  Location: src/Modules/Catalog/Catalog.Infrastructure/Migrations/
  Command to regenerate:
    dotnet ef migrations add {Name} -p src/Modules/Catalog/Catalog.Infrastructure -s src/Client/Client.Desktop --context CatalogDbContext
  Applied by: CatalogDatabaseInitializer (IHostedService) at startup

Future module migrations follow the same pattern with their own prefix:
  Inventory: inv_ prefix, Inventory.Infrastructure/Migrations/
  Sales: sal_ prefix, Sales.Infrastructure/Migrations/

---

## Startup Sequence (Updated for Stage 5A)

WPF App.OnStartup
  -> ApplicationHostBuilder.Create()
     .WithModule(new DesktopServicesRegistrar())    // registers MainWindow
     .WithModule(new ModuleHostRegistrar())          // registers IModuleDiscoveryService,
     .WithModule(new CatalogHostingModule())         //   IModuleRegistry, IModuleDependencyResolver  ← NEW
     .Build()                                        // registers all Catalog services
  -> host.StartAsync()
       -> DatabaseInitializerService.StartAsync()   // Platform DB (EnsureCreated, no migrations)
            -> DatabaseInitializer.InitializeAsync()
            -> SQLite database created at %LOCALAPPDATA%\GenericPOS\genericpos.db
       -> CatalogDatabaseInitializer.StartAsync()   // Catalog DB migrations  ← NEW
            -> Applies CatalogInitialCreate migration (cat_Products, cat_Categories, etc.)
  -> Services.GetRequiredService<MainWindow>()
  -> mainWindow.Show()

WPF App.OnExit
  -> host.StopAsync()

---

## Architecture Tests Status (Cumulative)

Stage 1 Tests (22):
  ARCH-001: Platform cannot reference business modules (4 tests)
  ARCH-002: Domain cannot reference Infrastructure/EF Core/SQLite (4 tests)
  ARCH-003: Domain cannot reference UI (3 tests)
  ARCH-004: Application cannot reference UI (3 tests)
  ARCH-009: Business modules cannot require HTTP (3 tests)
  ARCH-010: Circular dependencies forbidden (1 test — expanded to all assemblies in Stage 2)
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

Stage 4 Tests (7) — NEW:
  Stage4: Platform.Core module contracts must not reference EF Core
  Stage4: Platform.Core module contracts must not reference WPF
  Stage4: Platform.Core module contracts must not reference ASP.NET Core
  Stage4: Platform.Application module services must not reference EF Core
  Stage4: Platform.Application module services must not reference WPF
  Stage4: Client.ModuleHost must still not reference Platform.Infrastructure
  Stage4: Client.ModuleHost must still not reference EF Core

Stage 5A Tests (19) — NEW:
  ARCH-007a-g: Catalog layer dependency direction (7 tests)
  ARCH-DB-01/02/03: Catalog Domain/Application/Contracts must not reference EF Core (3 tests)
  ARCH-WPF-01/02/03: Catalog Domain/Application/Contracts must not reference WPF (3 tests)
  ARCH-001e/f/g/h: Platform assemblies must not reference Catalog (4 tests)
  ARCH-005: Catalog.Infrastructure must not reference Inventory/Sales (2 tests)

Total Architecture.Tests: 67 tests, all PASSING.
Platform.Infrastructure.Tests: 19 tests, all PASSING.
Platform.ModuleContract.Tests: 112 tests, all PASSING.
Catalog.Tests: 64 tests, all PASSING.
Grand total: 262 tests, 0 failures.

ARCH-005, ARCH-006, ARCH-007: NOW ACTIVE and passing (activated with Stage 5A).

---

## Packages Added (Cumulative)

Project | Package | Version | Reason
--------|---------|---------|-------
Client.Host | Microsoft.Extensions.Hosting | 10.0.x | DI, config, logging host
Architecture.Tests | NetArchTest.Rules | 1.3.2 | Architecture boundary testing
Platform.Infrastructure | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | SQLite provider
Platform.Infrastructure | Microsoft.EntityFrameworkCore.Design | 10.0.11 | Migration tooling (PrivateAssets=all)
Platform.Infrastructure | Microsoft.Extensions.Configuration.Binder | 10.0.x | DatabaseOptions binding
Platform.Infrastructure.Tests | Microsoft.EntityFrameworkCore.InMemory | 10.0.11 | (added but unused — using file:memory SQLite instead)

No new NuGet packages were added in Stage 4.

---

## Architectural Implementation Stages

Stage | Name                                                  | Status
------|-------------------------------------------------------|------------
1     | Solution Foundation (Platform projects)               | COMPLETE
2     | Client Host (DI, config, logging, module discovery)   | COMPLETE
3     | Database Foundation (SQLite, EF Core, migrations)     | COMPLETE
4     | Module Contract (IModule, IModuleManifest, lifecycle) | COMPLETE
5A    | Catalog Module (canonical module pattern)             | COMPLETE
5B    | Inventory Module                                      | Not started
5C    | Sales Module                                          | Not started
5D    | POS Module                                            | Not started
6     | Licensing (LicenseServer, Client.Licensing)           | Not started
7     | Update System (packages, signatures, rollback)        | Not started
8     | Additional Business Modules                           | Not started

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
7. IHostingModule vs IModule: separate concerns — both are needed (see Decision 1 above).
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
    do NOT use EF Core — only PlatformServicesExtensions calls AddPlatformInfrastructure().
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

### Decision 1: CatalogHostingModule vs CatalogModule — same pattern as Stage 4

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

Value object properties accessed as p.Id == new ProductId(guid) — not p.Id.Value == guid.
Reason: EF Core can translate typed value object comparisons (it has a registered converter)
but cannot translate .Value property access inside LINQ predicates.

### Decision 5: Catalog.UI excluded from Architecture.Tests

Catalog.UI targets net10.0-windows. Architecture.Tests targets net10.0.
The TFM gap prevents direct assembly load. Catalog.UI boundaries are enforced via .csproj inspection.
This is the same documented exception as Client.Desktop.

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

## Known Issues / Blockers

None. Stage 5A is complete.
Build: 0 errors, 0 warnings (18 projects).
All 262 tests pass. Application starts correctly. Catalog database schema applied at startup.
Database: %LOCALAPPDATA%\GenericPOS\genericpos.db (Platform + Catalog tables in same file).

---

Last updated: 2026-09-15 - Stage 5A complete. Catalog module implemented and tested.
