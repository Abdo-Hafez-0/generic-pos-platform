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
- **Cloud Backend:** ASP.NET Core (Stage 6 license server, Stage 7 update server, Stage 9 administration + backup servers on a durable server database; all optional for the desktop)
- **Cross-module communication:** Contracts only (never implementation references)
- **Layering:** UI -> Application -> Domain <- Infrastructure
- **Target framework:** net10.0 (net10.0-windows for WPF project)

---

## Current Implementation Phase

**Stage 9 COMPLETE (cloud services & administration): AdminPortal (vendor administration API), BackupServer (cloud backup API), durable server persistence for the license and update servers**

The vendor now administers the cloud ecosystem through one authenticated API, and the license, update and backup servers read and write
ONE durable server database. The desktop POS is untouched: nothing in Platform, Client or any business module references server code,
and the whole POS path (start, load modules, open POS, read local catalog, sell, update local stock, persist the sale) was re-verified
in a process where no server assembly is even loaded.

    AdminPortal.Api  (vendor, API key)   LicenseServer.Api   UpdateServer.Api   BackupServer.Api (customer, per-license token)
          \                                   |                    |                 /
           +-----------------  Cloud.Infrastructure (EF Core SQLite SERVER database, WAL, migrations) ----------------+
                                                     + file stores: packages (.gpkg) and backups (opaque bytes)
    Desktop client (WPF + local SQLite): unchanged; reaches the license/update servers only through Client.Licensing.Http / Client.Updater.Http

Implemented and tested: customer management, license management (create, suspend, reinstate, revoke, extend, entitlements, release
installation, installations list), module registry, package publishing/withdrawal with a publication gate, cloud backup server (opaque
backups, per-license retention), administrative audit log, dashboard, API-key administrator authentication, durable licenses/packages.
Full decisions, endpoints, configuration and limitations: "Stage 9 Summary" below.

### Previous phase - Stage 8 COMPLETE (additional business modules): Customers, Suppliers, Purchasing, Pricing, Payments, Users, Audit, CashManagement, Reporting

Stage 7 (update system foundation: secure, recoverable updates) is complete and unchanged.

Core and individual modules can be updated from signed packages without ever endangering the working installation or the
customer's data. The update server is optional: nothing in normal POS operation depends on it.

    UpdatePublisher (private key, offline) -> signed .gpkg --> UpdateServer (serves bytes, holds no keys)
                                                                    |  HTTPS (Client.Updater.Http / IUpdateClient)
    Client.Updater:  Discover -> Download -> VERIFY (14 steps, mutates nothing) -> Stage -> [Migrate] -> Deploy side by side
                     -> ATOMIC active-pointer switch -> Activated -> Confirmed (healthy start) | auto/explicit Rollback
    Trust: Security.Es256 (same ES256 primitive as licensing) with trusted PUBLIC keys; license checks via ILicenseEntitlementService

Implemented and tested: package format (.gpkg ZIP, signed manifest + hashed payload), ES256 signing/verification with
KeyId + key rotation + unknown-key rejection, SHA-256 per-file and payload hashes, version semantics (upgrade only),
host/runtime/module/dependency/license/migration compatibility checks, ModulePackager (validate before signing),
UpdatePublisher (sign + write), discovery, download, staging, restore points, module-owned migration orchestration,
side-by-side deployment with atomic activation, persisted update state machine, startup recovery, explicit rollback,
minimal UpdateServer. Deliberately NOT done: see "Remaining limitations after Stage 7" (notably: nothing yet LOADS the
activated core/module directories at runtime - launcher/ModuleHost adoption is later work).
Verified with the real host: update server unreachable -> POS checkout still completes; a signed module package with
migration metadata installs against the real database, a restore point is taken, and sale/stock data stay intact.

### Previous phase - Stage 6 COMPLETE (licensing foundation): offline-first signed licensing

The client evaluates a cryptographically signed license LOCALLY; a license server (minimal foundation) issues and
renews it. No network is needed for any evaluation or POS operation.

    LicenseServer.Api  --HTTPS-->  Client.Licensing.Http (ILicenseClient)
                                          |
    Client.Licensing: LicenseService -> verify ES256 signature -> bind to installation/product -> LicenseEvaluator
                                          |                                  |
                      file store (license.json)              LicenseState + entitlements
                                                                   |
        Platform.Application.Abstractions.Licensing.ILicenseEntitlementService  <- all business code sees only this

Status of the "current" licensing scope: implemented and tested = installation identity, signed license model,
ES256 verification with trusted PUBLIC keys (rotation-ready), local storage, deterministic state evaluation
(Unlicensed/Active/GracePeriod/Expired/Suspended/Revoked/Invalid), module + feature entitlements, activation,
renewal, offline lease + grace, host integration, minimal license server (activate/renew/sign, in-memory store).
Deliberately NOT enforced yet: no business module or UI gates on the license (see Stage 6 decisions / limitations).
Business data is never touched by licensing (Client.Licensing has no DB or business module access).

### Previous phase - Stage 5D COMPLETE: POS Module

The POS module is the cashier-facing ORCHESTRATION layer. It owns sessions and carts (the transaction being
built) and coordinates Catalog, Inventory and Sales exclusively through their Contracts:

    POS -> Catalog.Contracts   (IProductLookup, IProductBarcodeResolver)
    POS -> Inventory.Contracts (IStockAvailabilityChecker, IStockIssueService  <- added in Stage 5D)
    POS -> Sales.Contracts     (ISalesService)

POS persists only its own data (`pos_Sessions`, `pos_Carts`, `pos_CartItems`; migration `InitialPOSSchema`),
holding plain Guid references to products, warehouses and sales (no cross-module FKs). Cart lines snapshot
product SKU, name and unit price at add time; those values are what Sales receives at checkout.
POSHostingModule is registered last (Catalog -> Inventory -> Sales -> POS). POS.UI contains PosViewModel and a
minimal PosView (not yet hosted in MainWindow). NO Payments module exists and none was simulated: checkout
completes the sale and issues stock, and the payment step is a documented integration point (see below).
Verified end-to-end with the real modules on a temporary SQLite file (barcode add, over-stock rejection,
checkout -> sale Completed, stock 10 -> 7, cart CheckedOut with SaleId).

### Previous phase - Stage 5C COMPLETE: Sales Module

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

### Stage 5D - POS Module
- [x] Created POS.Domain: PosSession (Open/Closed), PosCart (Open/CheckedOut), PosCartItem (snapshot SKU/name/price);
      value objects PosSessionId, PosCartId, PosCartItemId, Money, CartQuantity; enums PosSessionStatus, PosCartStatus
- [x] Created POS.Contracts: IPOSService, IPOSReader; POSOperationResult, POSOpenSessionResult, POSStartCartResult,
      POSAddItemResult, POSCheckoutResult; read models POSSessionResult, POSCartResult, POSCartItemResult (+ status enums)
- [x] Created POS.Application: commands OpenPosSession, ClosePosSession, StartCart, AddProductToCart, RemoveProductFromCart,
      ChangeCartQuantity, ClearCart, CheckoutCart; queries GetPosSession, GetCart, GetCurrentCart;
      IPosSessionRepository, IPosCartRepository, IPosUnitOfWork; PosMapping
- [x] Created POS.Infrastructure: POSDbContext (pos_ prefix), 3 EF configurations, internal EF repositories, PosUnitOfWork,
      POSService, POSReader, POSModule, POSModuleManifest (depends on catalog, inventory, sales), POSHostingModule,
      POSDatabaseInitializer, POSServicesExtensions, Migration: InitialPOSSchema, [InternalsVisibleTo("POS.Tests")]
- [x] Created POS.UI: PosViewModel (talks only to POS.Contracts), PosView.xaml (barcode input, cart grid, totals, checkout)
- [x] Extended Inventory (smallest change that keeps the module boundary): Inventory.Contracts.IStockIssueService +
      IssueStockResult; Inventory.Application IssueStockCommandHandler (StockOut movement + balance decrease, atomic in
      Inventory); Inventory.Infrastructure StockIssueService; 8 new Inventory tests
- [x] Updated Client.Desktop.csproj/App.xaml.cs (POSHostingModule registered after Sales), GenericPOS.sln (30 -> 36 projects)
- [x] Created tests/POS.Tests (84 tests; Catalog/Inventory/Sales are stubs of their Contracts)
- [x] Created Architecture.Tests/DependencyRules/POSBoundaryTests.cs (ARCH-POS-001..020); Assemblies.cs updated
- [x] Build: 0 errors, 0 warnings (36 projects); all 586 tests pass
- [x] No Payments module, no Licensing/Updates/Cloud, no HTTP anywhere in POS

### Stage 6 - Licensing
- [x] Platform.Core: LicenseState enum (Unlicensed, Active, GracePeriod, Expired, Suspended, Revoked, Invalid)
- [x] Platform.Application: ILicenseEntitlementService (State, IsModuleLicensed(ModuleId), IsFeatureLicensed(FeatureId))
      + IsLicensed(IModuleManifest) extension - the only licensing type business code may use
- [x] Created Licensing.Contracts (shared wire model): LicensePayload, SignedLicense, LicenseSerializer, LicenseSigning,
      LicenseStatusClaim, ActivationRequest/Response, RenewalRequest/Response, LicenseErrorCodes
- [x] Client.Licensing (was a marker): Domain (InstallationIdentity, LicenseEvaluator, LicenseEvaluation, LicensePolicy,
      InvalidReason, ExpiryKind), Application (ILicenseService/LicenseService, InstallationIdentityService, ILicenseClient,
      ILicenseStore, IInstallationIdentityStore, ILicenseVerifier), Infrastructure (EcdsaLicenseVerifier, FileLicenseStore,
      FileInstallationIdentityStore, LicensingHostingModule, AddClientLicensing, LicensingInitializer)
- [x] Created Client.Licensing.Http: HttpLicenseClient (ILicenseClient over HttpClient), AddLicenseHttpClient,
      LicenseHttpHostingModule (HTTPS required except loopback)
- [x] Created LicenseServer.Application (LicenseRecord, ILicenseRepository, ILicenseSigner, LicenseIssuanceService,
      LicenseServerOptions), LicenseServer.Infrastructure (EcdsaLicenseSigner, InMemoryLicenseRepository),
      LicenseServer.Api (minimal API: POST /api/licenses/activate, POST /api/licenses/renew)
- [x] Client.Desktop: references Client.Licensing + Client.Licensing.Http; App.xaml.cs registers LicensingHostingModule and
      LicenseHttpHostingModule; appsettings.json has a Licensing section (no keys)
- [x] Created tests/Licensing.Tests (109 tests) and Architecture.Tests/DependencyRules/LicensingBoundaryTests.cs
      (ARCH-LIC-001..018); Assemblies.cs updated
- [x] Build: 0 errors, 0 warnings (42 projects); all 713 tests pass
- [x] Verified with the real host: licensing modules + Catalog/Inventory/Sales/POS start together, state Unlicensed,
      installation.json created in the licensing folder, POS checkout unaffected

### Stage 7 - Update System
- [x] Security.Es256 (neutral, public keys only): Es256Verifier, TrustedPublicKey, SignatureCheck, Sha256Hex, Es256Info.
      Security.Es256.Signing (private keys): Es256Signer - referenced ONLY by LicenseServer.Infrastructure and UpdatePublisher.
      Stage 6 licensing was refactored onto these (EcdsaLicenseVerifier/EcdsaLicenseSigner are thin wrappers); all licensing tests still pass.
- [x] Updates.Contracts: PackageType (Core, Module), PackageManifest, PackageFile, PackageDependency, PackageMigration,
      SignedPackageManifest + PackageManifestSerializer, UpdateCheckRequest/Response, UpdateInfo, UpdateErrorCodes
- [x] Updates.Package: PackageFormat (limits, safe-path + forbidden-file rules), PayloadDigest, PackageWriter (deterministic),
      PackageReader (safe open), PackageContents, ManifestRules (shared structural validation)
- [x] Platform.Application: IModuleMigrator + ModuleMigrationResult (contract for a module to run ITS OWN migrations; none implemented yet)
- [x] Client.Updater (was a marker): Domain (UpdateState, UpdateJournal, ActivePointer, InstalledModule, VersionSemantics),
      Application (PackageVerifier, UpdateService/IUpdateService, IUpdateClient, IUpdateStore, IInstalledStateProvider, IDataSafeguard,
      IMigrationCoordinator, UpdaterOptions), Infrastructure (UpdateStore, SqliteDataSafeguard, ModuleOwnedMigrationCoordinator,
      InstalledStateProvider, UpdaterHostingModule, UpdaterInitializer)
- [x] Client.Updater.Http: HttpUpdateClient (IUpdateClient), AddUpdateHttpClient, UpdateHttpHostingModule (HTTPS except loopback)
- [x] UpdateServer.Application (DirectoryPackageRepository, UpdateDiscoveryService) + UpdateServer.Api (POST /api/updates/check, GET /api/updates/packages/{id})
- [x] tools/ModulePackager (CreateDraft: validate + hash, rejects before signing) and tools/UpdatePublisher (Publish: re-verify, sign, write .gpkg)
- [x] Client.Desktop: references Client.Updater + Client.Updater.Http; App.xaml.cs registers UpdaterHostingModule + UpdateHttpHostingModule
      (after Licensing); appsettings.json has an Updater section (no keys)
- [x] tests/Updater.Tests (168 tests); Architecture.Tests/DependencyRules/UpdateBoundaryTests.cs (ARCH-UPD-001..022); Assemblies.cs updated
- [x] Build: 0 errors, 0 warnings (52 projects); all 903 tests pass
- [x] Real-host verification (scratch harness, not committed): Catalog+Inventory+Sales+POS+Licensing+Updater with an unreachable update server:
      startup fine, POS checkout completes, discovery returns Update.ServerUnavailable, a signed module package installs against the real DB with a
      restore point, and sale/stock rows are intact afterwards

### Stage 8 - Additional Business Modules (COMPLETE)
- [x] 8A Customers (cus_*): Customer aggregate (+CustomerAddress, CustomerContact, CustomerStatus), create/update/deactivate/reactivate, address+contact management, get/list(paged)/search; contracts ICustomerLookup, ICustomerReader; migration InitialCustomersSchema; Customers.Tests (38); ARCH-CUS-001..016
- [x] 8B Suppliers (sup_*): Supplier aggregate (+SupplierAddress, SupplierContact, SupplierStatus), same capabilities as Customers; contracts ISupplierLookup, ISupplierReader (Purchasing depends on these); migration InitialSuppliersSchema; Suppliers.Tests (38); ARCH-SUP-001..016
- [x] 8C Purchasing (pur_*): PurchaseOrder (+PurchaseOrderLine; Draft/Submitted/Received/Cancelled) with create/add-remove-change lines/submit/receive/cancel, get/list; depends on Catalog.Contracts, Suppliers.Contracts, Inventory.Contracts ONLY; Inventory.Contracts extended with IStockReceiptService (StockReceiptService delegating to AddStock; 3 new Inventory tests); receiving is line-by-line and resumable (no cross-module transaction); contract IPurchaseOrderReader; migration InitialPurchasingSchema; Purchasing.Tests (45); ARCH-PUR-001..016
- [x] 8D Pricing (pri_*): PriceList + Price (effective period, minimum-quantity breaks, overlap prevention, deactivate), CreatePrice/UpdatePrice/DeactivatePrice, get/list, current-price resolution; depends on Catalog.Contracts; contract IPriceResolver; migration InitialPricingSchema; Pricing.Tests (41); ARCH-PRI-001..016. POS integration (OPTIONAL, minimal): AddProductToCart resolves the price through Pricing.Contracts when the module is installed (snapshotted on the cart line), else the Catalog price; 5 new POS tests
- [x] 8E Payments (pay_*): Payment record (reference type + id, amount, Cash/Card/Other, tendered + change for cash, void with reason; split payments per reference), RecordPayment/VoidPayment, get/list-for-reference; no dependencies; contracts IPaymentService/IPaymentReader; migration InitialPaymentsSchema; Payments.Tests (29); ARCH-PAY-001..016. No real gateways/hardware are involved - payments are records only. POS integration (OPTIONAL, minimal): CheckoutAsync accepts an optional POSPaymentRequest; the payment for the cart total is recorded through Payments.Contracts after the sale is confirmed and before stock is issued (payment voided if stock issue fails, sale cancelled if the payment fails; rejected up front if Payments is not installed or the cash tendered is too low); 6 new POS tests; ARCH-POS-019 relaxed to allow Payments.Contracts only. Also fixed a CS8631 warning in Inventory ReceiveStockTests
- [x] 8F Users (usr_*): User (unique case-insensitive username, display name, optional email, active/inactive) and Role (unique name, permission codes such as sales.refund) with role assignment; Create/Update/Deactivate/Reactivate user, Assign/Remove role, Create role, Grant/Revoke permission, get/list/permissions queries; no dependencies; contracts IUserLookup and IUserPermissionChecker (inactive/unknown users hold no permissions); migration InitialUsersSchema (usr_Users, usr_Roles, usr_UserRoles, usr_RolePermissions; FKs only inside the module); Users.Tests (50); ARCH-USR-001..016. NOT authentication: no passwords, credentials, sessions or sign-in; no module enforces permissions yet (deferred to the auth/security stage)
- [x] 8G Audit (aud_*): append-only AuditEntry (module, action, entity type/id, actor id/name, summary, details, UTC time) - no update or delete exists at any layer; RecordAuditEntry, GetAuditEntry, QueryAuditEntries (module/action/entity/actor/time-range filters, newest first, paged, MaxPageSize 200); no dependencies (every value is a plain string/Guid); contracts IAuditRecorder (append) and IAuditReader (read-only); migration InitialAuditSchema (aud_AuditEntries); Audit.Tests (26); ARCH-AUD-001..016. Adoption by other modules is deferred: no module records to Audit yet (they would take an optional IAuditRecorder, like POS does for Pricing/Payments)
- [x] 8H CashManagement (cash_*): CashSession (one shift per drawer: opening float, PayIn/PayOut with reasons, CashSale/CashRefund movements, counted close with stored expected amount and variance) and CashMovement; outflows cannot take the drawer below zero; a movement reference (type + id) is idempotent per kind so retries never double-count; only one open session per drawer (application check plus a filtered unique index); OpenCashSession, RecordCashMovement, CloseCashSession, get/get-open/list queries; no dependencies; contracts ICashMovementRecorder and ICashSessionReader; migration InitialCashManagementSchema (cash_Sessions, cash_Movements); CashManagement.Tests (34); ARCH-CASH-001..016. Module id cash-management. Tracks cash only - no hardware, no real money. POS/Payments do not record cash sales into it yet (deferred; they would take an optional ICashMovementRecorder)
- [x] 8I Reporting (no tables, no DbContext, no migration - it stores nothing): read-only reports built from other modules' read contracts - sales report (completed sales in a range with daily breakdown, bounded scan window flagged IsTruncated), inventory snapshot, purchasing overview, customer and supplier summaries, and a business overview where each section stands alone; pure calculators in Reporting.Domain (DateRange max 366 days, SalesCalculator, StockCalculator); SOFT dependencies on Sales/Inventory/Purchasing/Customers/Suppliers contracts: each source reader is an optional constructor parameter, so a missing module makes only its own section Unavailable (never an error); contract IReportProvider; Reporting.Tests (34); ARCH-REP-001..016. Limitation: Sales.Contracts only exposes a recent-sales list, so the sales report scans at most 2000 sales (IsTruncated tells the caller). No export, scheduling or advanced analytics (Stage 8 minimum)
- [x] Integration tests (tests/Integration.Tests, 32 tests): the REAL host (ApplicationHostBuilder + ModuleHostRegistrar + the real hosting modules) against a throw-away SQLite file (location set through GENERICPOS_Database__* environment variables, tests serialised in one xUnit collection). Proves: every module creates only tables under its own prefix, no foreign key crosses a module boundary, every Stage 8 migration is recorded, a restart on the same database applies nothing new and keeps data; the core modules alone start with no Stage 8 table or contract; each Stage 8 module can be added alone (with its Stage 8 dependency) and each can be removed from the full set; Reporting alone reports every section Unavailable (not an error); POS vertical slice (create product, add stock, open POS, find product, add to cart, checkout -> sale Completed, stock reduced) with and without Stage 8 modules, cash payment recorded with change, Pricing overriding the Catalog price, rejected payment request without Payments, purchase order receiving increasing real stock
- [x] Final verification: `dotnet build GenericPOS.sln` -> 0 errors, 0 warnings (108 projects); `dotnet test` -> 1428 tests, 0 failures; the working tree is clean and pushed to origin/master. One commit per module (`feat(stage8): add customers|suppliers|purchasing|pricing|payments|users|audit|cash management|reporting module`)
- [x] Not done by design (out of scope for Stage 8): Accounting, Loyalty, Advanced Reports/Inventory, Multi-Branch, Manufacturing, Restaurant, E-Commerce, Cloud Backup, Synchronization, Employee Management, authentication, real payment gateways/hardware, launcher/runtime update adoption, update CLI wrappers, license enforcement, Stage 9+

### Stage 9 - Cloud Services & Administration (COMPLETE)
Scope source: the roadmap ("POS Platform - Implementation Roadmap", Stage 9): components LicenseServer, UpdateServer, BackupServer, AdminPortal;
capabilities customer management, license management, module registry, package/update management, cloud backup, administrative operations;
"Cloud must remain optional for normal offline POS operation". (The roadmap file is not in the repository; it was read from the technical
lead's copy. Architecture & Solution Design.md sections 45-51, 63-67 and 83 agree.)
- [x] Cloud.Contracts (new): ApiError, CloudErrorCodes (+ HTTP mapping), ServiceResult<T>, PagedResult<T>, Paging, admin DTOs, backup DTOs/headers
- [x] LicenseServer.Application (extended, backward compatible): LicenseRecord fields now settable + ActivatedAt, LastIssuedAt, RowVersion; ILicenseQuery,
      LicenseFilter/LicensePage, LicenseConcurrencyException, ActivationKeys (generate + SHA-256 hash); issuance records ActivatedAt/LastIssuedAt;
      InMemoryLicenseRepository also implements ILicenseQuery. LicenseServer.Api: durable store when CloudDatabase is configured, HTTPS/HSTS outside Development
- [x] UpdateServer.Application (extended): PackageStatus, ManagedPackage, IPackageCatalog, IPackageFileStore, PackageSigningPolicy, PackageInspector (publication gate),
      PackageVersions. UpdateServer.Api: durable catalog when CloudDatabase is configured (only PUBLISHED packages offered/downloadable), else the Stage 7 directory repository
- [x] BackupServer.Application (new): BackupRecord, IBackupCatalog/IBackupBlobStore/IBackupAccessTokenStore, BackupAccessService (token issue/rotate/revoke/authenticate),
      BackupService (upload with size limit + hash check, list, download, delete, per-license retention, admin list/delete/usage), BackupServerOptions
- [x] BackupServer.Api (new): POST/GET /api/backups, GET /api/backups/{id}, GET /api/backups/{id}/content, DELETE /api/backups/{id}, GET /health
- [x] AdminPortal.Application (new): Customer, RegisteredModule, AdminAuditEntry, ICustomerRepository, IModuleRegistryRepository, IAdminAuditLog, AdminAuditRecorder,
      AdminKeyAuthenticator/AdminKeys, CustomerAdminService, LicenseAdminService, ModuleRegistryService, PackageAdminService, AdminOperationsService
- [x] AdminPortal.Api (new): /api/admin/* (dashboard, audit, customers, licenses, installations, modules, packages, backups) behind API-key authentication, GET /health
- [x] Cloud.Infrastructure (new): CloudDbContext (EF Core SQLite) + migration InitialCloudSchema, EF repositories, FilePackageStore, FileBackupBlobStore, composition helpers
      (AddCloudDatabase, AddLicenseStore, AddPackageStore, AddBackupStore, AddBackupServices, AddAdminPortalServices)
- [x] tests/Cloud.Tests (new, 187 tests): application behaviour on the REAL SQLite server database + real file stores, in-process API tests for AdminPortal.Api and
      BackupServer.Api, and end-to-end tests that host all four servers over one database (vendor creates a license -> client activates/renews against LicenseServer.Api
      with a verified ES256 signature; backup upload/download; package publish -> UpdateServer.Api offers/withdraws; restart durability)
- [x] Architecture.Tests: ARCH-CLD-001..016 (CloudBoundaryTests); Assemblies.cs + Architecture.Tests.csproj reference the new server assemblies
- [x] Integration.Tests: ServerIndependenceTests (2) - the full POS path on the real host with no server assembly loaded in the process
- [x] Real-process smoke run of AdminPortal.Api (Development: ephemeral key logged, 401 without key, dashboard + customer creation with it, database/WAL files created)
- [x] Build: 0 errors, 0 warnings (115 projects); all 1633 tests pass; Cloud.Tests were stress-run repeatedly (30 consecutive clean runs after the fix described in Known Issues)
- [x] Not done by design (later stages / not in Stage 9): the client-side CloudBackup module and IBackupClient/HttpBackupClient, synchronization, a browser UI for the portal, enterprise
      identity (SSO/2FA/roles), billing/payments/support/telemetry areas of the vendor platform, backup encryption and key management, hardware, security hardening campaign, packaging

---

## Current Task

**Stage 9 - COMPLETE (Cloud Services & Administration). Stopped: Stage 10 has not been started.**

---

## Next Task

**Awaiting instruction (technical lead decides).**

Next roadmap stage: Stage 10 (Hardware & Device Integration) - only when instructed. Follow-ups that are NOT part of any completed stage: license ENFORCEMENT points; a launcher that starts the ACTIVE core version and ModuleHost loading modules from the active deployment directories (so activated updates take effect at runtime); IModuleMigrator implementations in the business modules; CLI wrappers for ModulePackager/UpdatePublisher; the client-side CloudBackup module (optional module that talks to BackupServer.Api through an IBackupClient); a browser UI for AdminPortal; stock-reversal contract; hosting PosView and the Stage 8 view models in MainWindow; authentication on top of Users (Stage 11); adoption of Audit / CashManagement / Customers by POS and Sales (see "Stage 8 limitations" and "Stage 9 limitations").

---

## Solution / Project Structure (Current State)

GenericPOS.sln (115 projects)

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
|   +-- Client.Desktop             [DONE] - WPF shell + Licensing + Catalog + Inventory + Sales + POS modules wired in (Stage 6)
|   +-- Client.Licensing           [DONE] - Stage 6: Domain (InstallationIdentity, LicenseEvaluator, LicenseEvaluation,
|   |                                        LicensePolicy), Application (LicenseService, ILicenseClient, ILicenseStore,
|   |                                        IInstallationIdentityStore, ILicenseVerifier), Infrastructure (EcdsaLicenseVerifier,
|   |                                        file stores, LicensingHostingModule). No HTTP/EF/WPF/business modules.
|   +-- Client.Licensing.Http      [DONE] - Stage 6: HttpLicenseClient + LicenseHttpHostingModule (only HttpClient user)
|   +-- Client.Updater             [DONE] - Stage 7: Domain (UpdateState, UpdateJournal, ActivePointer, VersionSemantics), Application
|   |                                        (PackageVerifier, UpdateService, IUpdateClient, IUpdateStore, IDataSafeguard, IMigrationCoordinator),
|   |                                        Infrastructure (UpdateStore, SqliteDataSafeguard, ModuleOwnedMigrationCoordinator,
|   |                                        InstalledStateProvider, UpdaterHostingModule). No HTTP/EF/WPF/signing/business modules.
|   +-- Client.Updater.Http        [DONE] - Stage 7: HttpUpdateClient + UpdateHttpHostingModule (only HttpClient user of the updater)
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

    +-- POS/
        +-- POS.Domain             [DONE] - PosSession, PosCart, PosCartItem; value objects PosSessionId, PosCartId,
        |                                    PosCartItemId, Money, CartQuantity; enums PosSessionStatus, PosCartStatus
        +-- POS.Contracts          [DONE] - IPOSService, IPOSReader; POS*Result records and read models
        +-- POS.Application        [DONE] - Commands: OpenPosSession, ClosePosSession, StartCart, AddProductToCart,
        |                                    RemoveProductFromCart, ChangeCartQuantity, ClearCart, CheckoutCart
        |                                    Queries: GetPosSession, GetCart, GetCurrentCart
        |                                    Repositories: IPosSessionRepository, IPosCartRepository; IPosUnitOfWork
        +-- POS.Infrastructure     [DONE] - POSDbContext (pos_ prefix), EF configurations, internal EF repositories,
        |                                    PosUnitOfWork, POSService, POSReader, POSModule, POSModuleManifest,
        |                                    POSHostingModule, POSDatabaseInitializer, POSServicesExtensions,
        |                                    Migration: InitialPOSSchema
        +-- POS.UI                 [DONE] - PosViewModel, PosView.xaml (net10.0-windows)

    +-- Stage 8 modules (each: <M>.Domain, <M>.Contracts, <M>.Application, <M>.Infrastructure, <M>.UI - same five-layer pattern as Catalog)
        +-- Customers              [DONE] - cus_*   Customer (+addresses, contacts); ICustomerLookup, ICustomerReader
        +-- Suppliers              [DONE] - sup_*   Supplier (+addresses, contacts); ISupplierLookup, ISupplierReader
        +-- Purchasing             [DONE] - pur_*   PurchaseOrder (+lines); IPurchaseOrderReader; uses Catalog/Suppliers/Inventory contracts
        +-- Pricing                [DONE] - pri_*   PriceList, Price; IPriceResolver; uses Catalog.Contracts
        +-- Payments               [DONE] - pay_*   Payment records; IPaymentService, IPaymentReader
        +-- Users                  [DONE] - usr_*   User, Role, permission codes; IUserLookup, IUserPermissionChecker (no authentication)
        +-- Audit                  [DONE] - aud_*   append-only AuditEntry; IAuditRecorder, IAuditReader
        +-- CashManagement         [DONE] - cash_*  CashSession, CashMovement; ICashMovementRecorder, ICashSessionReader
        +-- Reporting              [DONE] - (no tables) read-only reports; IReportProvider; optional read contracts of Sales/Inventory/Purchasing/Customers/Suppliers

tests/
+-- Architecture.Tests             [DONE] - 299 tests, all passing (Stages 1-8)
+-- Platform.Infrastructure.Tests  [DONE] - 19 tests, all passing (Stage 3)
+-- Platform.ModuleContract.Tests  [DONE] - 112 tests, all passing (Stage 4)
+-- Catalog.Tests                  [DONE] - 64 tests, all passing (Stage 5A)
+-- Inventory.Tests                [DONE] - 92 tests, all passing (Stages 5B, 5D, 8C)
+-- Sales.Tests                    [DONE] - 103 tests, all passing (Stage 5C)
+-- POS.Tests                      [DONE] - 95 tests, all passing (Stages 5D, 8D, 8E)
+-- Tests.Common                   [DONE] - Stage 8: shared in-memory SQLite test database helper (TestModuleDatabase) + RepoPaths
+-- Customers.Tests (38), Suppliers.Tests (38), Purchasing.Tests (45), Pricing.Tests (41), Payments.Tests (29), Users.Tests (50),
    Audit.Tests (26), CashManagement.Tests (34), Reporting.Tests (34)   [DONE] - Stage 8
+-- Integration.Tests              [DONE] - Stage 8: 32 tests against the real host and real SQLite migrations

src/Licensing/
+-- Licensing.Contracts            [DONE] - Stage 6: shared signed-license wire model (no keys, no HTTP)
src/Cloud/LicenseServer/
+-- LicenseServer.Application      [DONE] - Stage 6: LicenseIssuanceService, ILicenseRepository, ILicenseSigner
+-- LicenseServer.Infrastructure   [DONE] - Stage 6: EcdsaLicenseSigner (owns private key), InMemoryLicenseRepository
+-- LicenseServer.Api              [DONE] - Stage 6: minimal ASP.NET Core API (activate, renew)
tests/Licensing.Tests              [DONE] - 109 tests, all passing (Stage 6)

src/Security/
+-- Security.Es256                 [DONE] - Stage 7: shared verification primitives (public keys only) used by licensing AND updates
+-- Security.Es256.Signing         [DONE] - Stage 7: Es256Signer (private keys); referenced only by LicenseServer.Infrastructure and UpdatePublisher
src/Updates/
+-- Updates.Contracts              [DONE] - Stage 7: package manifest, signed envelope, discovery messages, error codes
+-- Updates.Package                [DONE] - Stage 7: .gpkg format reader/writer, payload digest, shared manifest rules
src/Cloud/UpdateServer/
+-- UpdateServer.Application       [DONE] - Stage 7: DirectoryPackageRepository, UpdateDiscoveryService (no keys); Stage 9: package catalog ports + PackageInspector
+-- UpdateServer.Api               [DONE] - Stage 7: minimal ASP.NET Core API (check, download); Stage 9: durable catalog when CloudDatabase is configured
src/Cloud/
+-- Cloud.Contracts                [DONE] - Stage 9: server wire/result model (ApiError, ServiceResult, PagedResult, admin + backup DTOs)
+-- Cloud.Infrastructure           [DONE] - Stage 9: CloudDbContext (EF Core SQLite SERVER database), migration InitialCloudSchema, EF repositories, file stores, composition helpers
src/Cloud/AdminPortal/
+-- AdminPortal.Application        [DONE] - Stage 9: customers, licenses, module registry, packages, backups, audit, dashboard; API-key authentication
+-- AdminPortal.Api                [DONE] - Stage 9: /api/admin/* ASP.NET Core API (vendor administration)
src/Cloud/BackupServer/
+-- BackupServer.Application       [DONE] - Stage 9: opaque backup storage rules, per-license access tokens, retention
+-- BackupServer.Api               [DONE] - Stage 9: /api/backups ASP.NET Core API (customer side)
tests/Cloud.Tests                  [DONE] - 187 tests, all passing (Stage 9)
tools/
+-- ModulePackager                 [DONE] - Stage 7: validates + hashes a package spec, rejects before signing
+-- UpdatePublisher                [DONE] - Stage 7: signs a validated draft and writes the distributable package
tests/Updater.Tests                [DONE] - 168 tests, all passing (Stage 7)

Planned:
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
                  POS.Infrastructure, POS.UI              <- POS references added Stage 5D
Client.Licensing -> Platform.Core, Platform.Application, Licensing.Contracts, Client.Host (IHostingModule only)
                    + Microsoft.Extensions.Hosting.Abstractions / Configuration.Binder (no HTTP, EF, WPF, business modules)
Client.Licensing.Http -> Client.Licensing, Licensing.Contracts, Client.Host + Microsoft.Extensions.Http
Client.Desktop also references Client.Licensing and Client.Licensing.Http  <- Stage 6
Licensing.Contracts -> (nothing)
LicenseServer.Application -> Licensing.Contracts
LicenseServer.Infrastructure -> LicenseServer.Application, Licensing.Contracts
LicenseServer.Api -> LicenseServer.Application, LicenseServer.Infrastructure, Licensing.Contracts (ASP.NET Core)
(The server never references client or business code; business modules never reference licensing implementations.)
Client.Updater -> Platform.Core, Platform.Application, Updates.Contracts, Updates.Package, Security.Es256, Client.Host
                  + Microsoft.Data.Sqlite (restore points only) + Hosting.Abstractions / Configuration.Binder
                  (no HTTP, EF, WPF, signing, business modules, Client.Licensing)
Client.Updater.Http -> Client.Updater, Updates.Contracts, Client.Host + Microsoft.Extensions.Http
Client.Desktop also references Client.Updater and Client.Updater.Http  <- Stage 7
Security.Es256 -> (nothing); Security.Es256.Signing -> Security.Es256
Updates.Contracts -> (nothing); Updates.Package -> Updates.Contracts, Security.Es256
UpdateServer.Application -> Updates.Contracts, Updates.Package, Security.Es256; UpdateServer.Api -> UpdateServer.Application, Updates.Contracts
ModulePackager -> Updates.Contracts, Updates.Package, Security.Es256
UpdatePublisher -> ModulePackager, Updates.Contracts, Updates.Package, Security.Es256, Security.Es256.Signing
Client.Licensing -> also Security.Es256; LicenseServer.Infrastructure -> also Security.Es256, Security.Es256.Signing  <- Stage 7 refactor

STAGE 9 SERVER PROJECTS (never referenced by Platform, Client or any business module):
Cloud.Contracts -> (nothing)
LicenseServer.Application -> Licensing.Contracts, (unchanged references)
UpdateServer.Application -> also Cloud.Contracts
BackupServer.Application -> Cloud.Contracts, LicenseServer.Application, Licensing.Contracts
AdminPortal.Application -> Cloud.Contracts, LicenseServer.Application, UpdateServer.Application, BackupServer.Application,
                           Licensing.Contracts, Updates.Contracts, Updates.Package
Cloud.Infrastructure -> Cloud.Contracts, LicenseServer.Application, UpdateServer.Application, BackupServer.Application, AdminPortal.Application,
                        Licensing.Contracts, Updates.Contracts, Updates.Package, Security.Es256 + Microsoft.EntityFrameworkCore.Sqlite / .Design,
                        Configuration.Binder, Hosting.Abstractions   (the ONLY server project using EF Core; no ASP.NET, no signing)
AdminPortal.Api -> AdminPortal.Application, Cloud.Contracts, Cloud.Infrastructure (ASP.NET Core; no EF reference, no DbContext)
BackupServer.Api -> BackupServer.Application, Cloud.Contracts, Cloud.Infrastructure (ASP.NET Core; no EF reference, no DbContext)
LicenseServer.Api / UpdateServer.Api -> also Cloud.Infrastructure (durable store when CloudDatabase is configured)

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

POS MODULE (Stage 5D):
POS.Domain -> Platform.Core
POS.Contracts -> Platform.Core
POS.Application -> POS.Domain, POS.Contracts, Platform.Core, Platform.Application,
                    Catalog.Contracts, Inventory.Contracts, Sales.Contracts  <- Contracts ONLY
POS.Infrastructure -> POS.Domain, POS.Application, POS.Contracts, Platform.Core, Platform.Infrastructure,
                       Client.Host, Catalog.Contracts, Inventory.Contracts, Sales.Contracts,
                       Microsoft.EntityFrameworkCore.Sqlite
POS.UI -> POS.Application, POS.Contracts, Platform.Core
(POS never references Catalog/Inventory/Sales Domain, Application, Infrastructure or UI.)

TESTS:
Updater.Tests -> Updates.*, Security.Es256(.Signing), Client.Updater(.Http), UpdateServer.*, ModulePackager, UpdatePublisher,
               Client.Licensing + LicenseServer.* (real licensing states), Platform.Application
Licensing.Tests -> Licensing.Contracts, Client.Licensing, Client.Licensing.Http, LicenseServer.*, Platform.Application
Cloud.Tests -> Cloud.Contracts, Cloud.Infrastructure, AdminPortal.*, BackupServer.*, LicenseServer.* (Api aliased LicenseApi), UpdateServer.* (Api aliased UpdateApi),
               Licensing.Contracts, Updates.*, Security.Es256(.Signing), ModulePackager, UpdatePublisher, Microsoft.AspNetCore.Mvc.Testing
Architecture.Tests -> all Platform + non-WPF Client + non-WPF Catalog + Inventory + Sales + POS projects (+ later modules, update/licensing and Stage 9 server assemblies)
Platform.Infrastructure.Tests -> Platform.Infrastructure, Platform.Application
Platform.ModuleContract.Tests -> Platform.Core, Platform.Application, Client.ModuleHost
Catalog.Tests -> Catalog.Domain, Catalog.Application, Catalog.Infrastructure, Catalog.Contracts
POS.Tests -> POS.Domain, POS.Application, POS.Infrastructure, POS.Contracts,
               Catalog.Contracts, Inventory.Contracts, Sales.Contracts, Platform.Infrastructure
               (no Catalog/Inventory/Sales implementation assemblies; their contracts are stubbed)
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

Stage 5D:
Inventory (extension, registered by InventoryServicesExtensions):
- IStockIssueService -> StockIssueService (Scoped); IssueStockCommandHandler (Transient)
POS (added via POSHostingModule / POSServicesExtensions):
- POSDbContext (Scoped, same SQLite file as the other module DbContexts)
- IPosUnitOfWork -> PosUnitOfWork (Scoped)
- IPosSessionRepository -> EfPosSessionRepository, IPosCartRepository -> EfPosCartRepository (Scoped)
- IPOSService -> POSService, IPOSReader -> POSReader (Scoped)
- IModule -> POSModule (Singleton)
- POSDatabaseInitializer (IHostedService, Singleton)
- OpenPosSession/ClosePosSession/StartCart/AddProductToCart/RemoveProductFromCart/ChangeCartQuantity/
  ClearCart/CheckoutCart command handlers (Transient)
- GetPosSession/GetCart/GetCurrentCart query handlers (Transient)
POS expects Catalog, Inventory and Sales contract implementations to be registered before it (host order).

Stage 7 (added via UpdaterHostingModule / AddClientUpdater; requires licensing + ModuleHostRegistrar first):
- UpdateStore (Singleton; also as IUpdateStore), UpdaterOptions, Es256Verifier (trusted keys from Updater:TrustedKeys)
- IInstalledStateProvider -> InstalledStateProvider (reads IModuleRegistry + active pointers)
- IDataSafeguard -> SqliteDataSafeguard (database located through the Database configuration section)
- IMigrationCoordinator -> ModuleOwnedMigrationCoordinator (uses any registered IModuleMigrator)
- IUpdateClient -> NullUpdateClient by default; HttpUpdateClient when UpdateHttpHostingModule is registered
- PackageVerifier, UpdateService / IUpdateService (Singletons); UpdaterInitializer (IHostedService: local recovery only, no network)

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

POS migrations (Stage 5D): <- ACTIVE
  Migration: InitialPOSSchema (20261004191500_InitialPOSSchema)
  Tables: pos_Sessions, pos_Carts, pos_CartItems
  Table ownership: all pos_* tables are exclusively owned by POSDbContext.
                   Only FK: pos_CartItems -> pos_Carts. Product, warehouse and sale references are plain Guids.
  Location: src/Modules/POS/POS.Infrastructure/Migrations/
  Command to regenerate:
    dotnet ef migrations add {Name} -p src/Modules/POS/POS.Infrastructure -s src/Client/Client.Desktop --context POSDbContext
  Applied by: POSDatabaseInitializer (IHostedService) at startup
  (Inventory has no new migration in Stage 5D: IStockIssueService reuses existing tables.)

Future module migrations follow the same pattern with their own prefix.

---

## Startup Sequence (Updated for Stage 7)

WPF App.OnStartup
  -> ApplicationHostBuilder.Create()
     .WithModule(new DesktopServicesRegistrar())    // registers MainWindow
     .WithModule(new ModuleHostRegistrar())          // registers IModuleDiscoveryService,
     .WithModule(new LicensingHostingModule())       // Stage 6: offline license evaluation services
     .WithModule(new LicenseHttpHostingModule())     // Stage 6: HTTP transport to the license server
     .WithModule(new UpdaterHostingModule())         // Stage 7: update verification, staging, recovery (local only)
     .WithModule(new UpdateHttpHostingModule())      // Stage 7: HTTP transport to the update server
     .WithModule(new CatalogHostingModule())         //   IModuleRegistry, IModuleDependencyResolver
     .WithModule(new InventoryHostingModule())       // registers all Inventory services  <- Stage 5B
     .WithModule(new SalesHostingModule())           // registers all Sales services      <- Stage 5C
     .WithModule(new POSHostingModule())             // registers all POS services        <- Stage 5D
     .Build()
  -> host.StartAsync()
       -> DatabaseInitializerService.StartAsync()   // Platform DB (EnsureCreated, no migrations)
            -> DatabaseInitializer.InitializeAsync()
            -> SQLite database created at %LOCALAPPDATA%\GenericPOS\genericpos.db
       -> UpdaterInitializer.StartAsync()           // Stage 7: resolve interrupted/unconfirmed updates from local state (no network)
       -> LicensingInitializer.StartAsync()         // Stage 6: load identity + local license, verify (offline; never blocks startup)
       -> CatalogDatabaseInitializer.StartAsync()   // Catalog DB migrations
            -> Applies CatalogInitialCreate migration (cat_Products, cat_Categories, etc.)
       -> InventoryDatabaseInitializer.StartAsync() // Inventory DB migrations  <- Stage 5B
            -> Applies InitialInventorySchema migration (inv_Warehouses, inv_Locations, etc.)
       -> SalesDatabaseInitializer.StartAsync()     // Sales DB migrations  <- Stage 5C
            -> Applies InitialSalesSchema migration (sal_Sales, sal_SaleItems, etc.)
       -> POSDatabaseInitializer.StartAsync()       // POS DB migrations  <- Stage 5D
            -> Applies InitialPOSSchema migration (pos_Sessions, pos_Carts, pos_CartItems)
  -> Services.GetRequiredService<MainWindow>()
  -> mainWindow.Show()

WPF App.OnExit
  -> host.StopAsync()

Note: Hosted service execution order is determined by registration order in DI.
DatabaseInitializerService is registered by AddPlatformInfrastructure (Stage 3).
CatalogDatabaseInitializer is registered by AddCatalogModule (Stage 5A).
InventoryDatabaseInitializer is registered by AddInventoryModule (Stage 5B).
SalesDatabaseInitializer is registered by AddSalesModule (Stage 5C).
POSDatabaseInitializer is registered by AddPOSModule (Stage 5D).

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

Stage 5D Tests (20) - NEW (tests/Architecture.Tests/DependencyRules/POSBoundaryTests.cs):
  ARCH-POS-001/002/003/004: POS.Domain must not depend on EF Core / WPF / HTTP+ASP.NET / POS.Infrastructure
  ARCH-POS-005/006: POS.Application must not depend on POS.Infrastructure / POS.UI / EF Core
  ARCH-POS-007: POS.Contracts must not depend on POS.Domain / Application / Infrastructure
  ARCH-POS-008/009/010: POS must not reference Catalog / Inventory / Sales Domain, Application, Infrastructure, UI
  ARCH-POS-011/012/013: POS.Application uses Catalog.Contracts / Inventory.Contracts / Sales.Contracts
  ARCH-POS-014: POS references other modules only through *.Contracts assemblies
  ARCH-POS-015: POS.Domain and POS.Contracts do not reference Catalog/Inventory/Sales at all
  ARCH-POS-016: POS.Infrastructure depends on POS inner layers, not on POS.UI
  ARCH-POS-017/018: Platform, Catalog, Inventory and Sales must not depend on POS
  ARCH-POS-019: POS has no HTTP dependency and references no Payments assembly
  ARCH-POS-020: the project assembly graph has no circular dependencies

Stage 6 Tests (18) - NEW (tests/Architecture.Tests/DependencyRules/LicensingBoundaryTests.cs):
  ARCH-LIC-001/002: licensing domain / application namespaces have no HTTP, EF, WPF, infrastructure dependency
  ARCH-LIC-003: Client.Licensing assembly has no HTTP, WPF, EF Core or SQLite reference
  ARCH-LIC-004/005/006: client licensing does not depend on business modules / license server / EF-WPF-ASP.NET (Http)
  ARCH-LIC-007: Licensing.Contracts is a pure wire model
  ARCH-LIC-008/009/010: license server has no ASP.NET/HTTP/EF in application, no client, business-module or Platform dependency
  ARCH-LIC-011/012/013: business modules and Platform do not depend on the license server or client licensing
  ARCH-LIC-014: Platform abstraction ILicenseEntitlementService is implemented by Client.Licensing
  ARCH-LIC-015: no signing capability in client assemblies (private key stays on the server)
  ARCH-LIC-016: client licensing does not use ASP.NET Core
  ARCH-LIC-017: acyclic project graph including licensing; ARCH-LIC-018: no Stage 7 updater/packaging references

Stage 7 Tests (22) - NEW (tests/Architecture.Tests/DependencyRules/UpdateBoundaryTests.cs):
  ARCH-UPD-001: Client.Updater has no HTTP, EF Core, WPF or ASP.NET reference
  ARCH-UPD-002/003: updater does not depend on business modules, Client.Licensing or the license server (licensing only via ILicenseEntitlementService)
  ARCH-UPD-004: updater Domain/Application layering (no infrastructure, HTTP, EF, SQLite in the application layer)
  ARCH-UPD-005: no signing capability in updater/contract/package assemblies
  ARCH-UPD-006: Client.Updater.Http boundaries
  ARCH-UPD-007/008/009: business modules, business domains and Platform do not depend on the update system
  ARCH-UPD-010: UpdateServer has no WPF/EF/client/business/signing dependency
  ARCH-UPD-011/012: ModulePackager / UpdatePublisher do not depend on Client.* (incl. Client.Desktop), WPF, HTTP or business modules
  ARCH-UPD-013: Updates.Contracts / Updates.Package are pure
  ARCH-UPD-014: only LicenseServer.Infrastructure and UpdatePublisher reference Security.Es256.Signing
  ARCH-UPD-015/016: PackageHash/Signature/SigningKeyId are NOT in IModuleManifest; they live in PackageManifest; PackageType is an explicit enum
  ARCH-UPD-017: updater does not redefine licensing or implement IModule/IModuleManifest publicly; UpdateState != ModuleRuntimeStatus
  ARCH-UPD-018/019: licensing and updates share ONE trust primitive (Security.Es256); no duplicated cryptography; Security.Es256 is verification-only
  ARCH-UPD-020: acyclic project graph incl. update/security assemblies
  ARCH-UPD-021: no process execution / assembly loading / scripts from package content
  ARCH-UPD-022: migrations are orchestrated, not executed, by the updater (module-owned)

Total Architecture.Tests: 155 tests, all PASSING.
Platform.Infrastructure.Tests: 19 tests, all PASSING.
Platform.ModuleContract.Tests: 112 tests, all PASSING.
Catalog.Tests: 64 tests, all PASSING.
Inventory.Tests: 89 tests, all PASSING (8 new in Stage 5D for stock issue).
Sales.Tests: 103 tests, all PASSING (Domain, Application, Infrastructure incl. migration, Contracts).
POS.Tests: 84 tests, all PASSING (Domain, Application incl. checkout orchestration, Infrastructure incl. migration, Contracts).
Licensing.Tests: 109 tests, all PASSING (evaluator states/boundaries, signatures, tampering, identity, activation, renewal,
  offline/restart, suspension/revocation, file stores, expiration safety with SQLite, host integration, server issuance,
  HttpLicenseClient, in-process ASP.NET Core API integration).
Updater.Tests: 168 tests, all PASSING (package format & rejection, hashing, signatures/key rotation, version semantics, host/runtime/module/
  dependency/migration compatibility, license checks incl. all 7 real LicenseStates, install/stage/activate, confirm, rollback, migration
  failures, activation failure (real file lock), restart recovery, data preservation on real SQLite, offline/unavailable server, host
  integration, in-process UpdateServer API end to end incl. tampered-package scenarios).
Grand total: 903 tests, 0 failures.

ARCH-005, ARCH-006, ARCH-007: ACTIVE and passing (activated with Stage 5A).
ARCH-INV-001 through ARCH-INV-011: ACTIVE and passing (activated with Stage 5B).
ARCH-SAL-001 through ARCH-SAL-016: ACTIVE and passing (activated with Stage 5C).
ARCH-POS-001 through ARCH-POS-020: ACTIVE and passing (activated with Stage 5D).
ARCH-LIC-001 through ARCH-LIC-018: ACTIVE and passing (activated with Stage 6).
ARCH-UPD-001 through ARCH-UPD-022: ACTIVE and passing (activated with Stage 7).
(Stage 8 module rules ARCH-CUS/SUP/PUR/PRI/PAY/USR/AUD/CASH/REP-001..016: see "Stage 8 Summary".)
ARCH-CLD-001 through ARCH-CLD-016: ACTIVE and passing (activated with Stage 9): Cloud.Contracts pure; server application layers free of ASP.NET/EF/HTTP/WPF/SQLite;
  server application layers depend inward only; no server project depends on the desktop (Client, Platform, business modules); Cloud.Infrastructure has no
  ASP.NET/WPF/signing; desktop assemblies and desktop project files never reference server code (offline-first); API hosts are thin (no EF, no DbContext);
  server administration is separate from the desktop Users module; the admin audit log is append-only; no Stage 9 assembly can sign; assembly graph acyclic;
  no secrets/private keys in server sources; admin services return contract DTOs only; server tables use server prefixes (lic_/upd_/bak_/adm_); no hard-coded
  environment values in server code.
Stage 9 test totals: Cloud.Tests 187, Architecture.Tests 315 (299 + 16), Integration.Tests 34 (32 + 2). Grand total after Stage 9: 1633 tests, 0 failures.

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

## Packages Added in Stage 5D

Project | Package | Version | Reason
--------|---------|---------|-------
POS.Infrastructure | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | Module-owned SQLite persistence
POS.Infrastructure | Microsoft.EntityFrameworkCore.Design | 10.0.11 | Migration tooling (PrivateAssets=all)
POS.Infrastructure | Microsoft.Extensions.Configuration.Binder | 10.0.11 | DatabaseOptions binding
POS.Infrastructure | Microsoft.Extensions.Hosting.Abstractions | 10.0.11 | IHostedService
POS.Tests | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | In-memory SQLite tests
POS.Tests | Microsoft.Extensions.DependencyInjection | 10.0.11 | DI container for integration tests
POS.Tests | Microsoft.Extensions.Logging.Abstractions | 10.0.11 | ILogger for POSDatabaseInitializer

---

## Packages Added in Stage 6

Project | Package | Version | Reason
--------|---------|---------|-------
Client.Licensing | Microsoft.Extensions.Hosting.Abstractions | 10.0.11 | IHostedService / HostBuilderContext
Client.Licensing | Microsoft.Extensions.Configuration.Binder | 10.0.11 | Licensing configuration binding
Client.Licensing.Http | Microsoft.Extensions.Http | 10.0.11 | AddHttpClient typed client
Licensing.Tests | Microsoft.AspNetCore.Mvc.Testing | 10.0.0 | host the license API in-process (no external network)
Licensing.Tests | Microsoft.Data.Sqlite | 10.0.11 | stand-in business DB for the expiration-safety test
Licensing.Tests | Microsoft.Extensions.DependencyInjection / Logging.Abstractions | 10.0.11 | host-integration tests
(Cryptography uses the built-in System.Security.Cryptography ECDsa; no third-party crypto.)

---

## Packages Added in Stage 7

Project | Package | Version | Reason
--------|---------|---------|-------
Client.Updater | Microsoft.Data.Sqlite | 10.0.11 | database restore points (online backup API) - no table access
Client.Updater | Microsoft.Extensions.Hosting.Abstractions / Configuration.Binder | 10.0.11 | host registration + configuration
Client.Updater.Http | Microsoft.Extensions.Http | 10.0.11 | AddHttpClient typed client
Updater.Tests | Microsoft.AspNetCore.Mvc.Testing | 10.0.0 | host the update API in-process
Updater.Tests | Microsoft.Data.Sqlite | 10.0.11 | real-database data-preservation tests
(Package format uses System.IO.Compression; cryptography uses System.Security.Cryptography via Security.Es256; no third-party crypto/zip.)

---

## Packages Added in Stage 9

Project | Package | Version | Reason
--------|---------|---------|-------
Cloud.Infrastructure | Microsoft.EntityFrameworkCore.Sqlite | 10.0.11 | Server database provider (the server's OWN database, never the desktop file)
Cloud.Infrastructure | Microsoft.EntityFrameworkCore.Design | 10.0.11 | Migration tooling (PrivateAssets=all)
Cloud.Infrastructure | Microsoft.Extensions.Configuration.Binder / Hosting.Abstractions | 10.0.11 | Configuration binding, database initializer hosted service
Cloud.Tests | Microsoft.AspNetCore.Mvc.Testing, Microsoft.Data.Sqlite, Microsoft.Extensions.DependencyInjection, xunit stack | existing versions | In-process API hosting, real SQLite
(No new third-party packages: same packages and versions already used by the desktop persistence layer.)

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
5D    | POS Module                                            | COMPLETE
6     | Licensing (LicenseServer, Client.Licensing)           | COMPLETE (foundation; enforcement points deferred)
7     | Update System (packages, signatures, rollback)        | COMPLETE (foundation; runtime adoption of activated versions deferred)
8     | Additional Business Modules                           | COMPLETE
9     | Cloud Services & Administration (AdminPortal, BackupServer, durable License/UpdateServer) | COMPLETE (foundation; see Stage 9 limitations)
10    | Hardware & Device Integration                         | Not Started

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

## Stage 5D Architectural Decisions

1. POS is an orchestration module. Sales keeps sales persistence/lifecycle; Inventory keeps stock; Catalog keeps products.
   POS holds only IDs and snapshots of other modules' data.
2. Inventory had no public write capability, so POS could not reduce stock without breaking boundaries. The smallest
   extension was made: Inventory.Contracts.IStockIssueService (+ IssueStockResult), implemented via a new
   Inventory.Application IssueStockCommandHandler. Sales was NOT changed and still does not touch stock or payments.
3. Handlers follow the established plain-class `HandleAsync` pattern of Catalog/Inventory/Sales. The Platform.Application
   ICommand/IQuery abstractions exist but no module uses them; no MediatR.
4. Checkout order: validate -> re-check stock -> create sale -> add lines (price snapshot) -> confirm -> issue stock ->
   complete sale -> mark cart checked out. Catalog/Inventory/Sales/POS use separate DbContexts, so there is no
   distributed transaction. Failures before stock issue cancel the sale. A failure during stock issue cancels the sale
   but cannot undo lines already issued (no reversal contract yet) - the error message says so. A failure completing
   the sale after stock issue leaves the sale Confirmed and the cart open - the error message says so.
5. Payments: no Payments module exists and nothing was simulated. The payment step belongs between "confirm" and
   "complete" in CheckoutCartCommandHandler once Payments.Contracts exists.
6. A cart holds one line per product (adding again merges quantity; the first price snapshot is kept). Closing a session
   is refused while its open cart still has items. StartCart returns the session's existing open cart (idempotent).
7. Stock is validated against the session's warehouse (WarehouseId is a plain Guid chosen when the session opens).
8. Cashier identity is a free-text reference until a Users module exists.
9. POS.UI is excluded from Architecture.Tests (net10.0-windows TFM gap, same as other modules); its csproj references
   only POS.Application, POS.Contracts and Platform.Core. PosView is not yet hosted in MainWindow.

---

## Stage 6 Architectural Decisions

1. **Installation identity.** `InstallationIdentity(InstallationId GUID, CreatedAt)`: a random GUID generated once and stored in
   `%LOCALAPPDATA%\GenericPOS\Licensing\installation.json` (outside the app directory, so it survives updates; separate from
   the business DB). Not a hardware fingerprint. A corrupt/invalid file is regenerated, which breaks binding to the old license
   (the license then evaluates Invalid/WrongInstallation) - fail closed.
2. **Payload.** `LicensePayload` JSON: LicenseId, CustomerId, InstallationId, ProductId, LicenseVersion, IssuedAt, ValidFrom,
   ValidUntil, LeaseValidUntil, GracePeriodUntil, Status (Active/Suspended/Revoked), Modules[], Features[] (string IDs matching
   ModuleId/FeatureId), Issuer, KeyId. License and lease are ONE signed document: every renewal issues a new signed payload
   with a higher LicenseVersion and a new lease (a replayed older version is rejected).
3. **Signature.** ES256 = ECDSA P-256 + SHA-256, IEEE P1363 signature, via System.Security.Cryptography. The signed bytes are the
   exact UTF-8 JSON bytes transmitted (`SignedLicense.Payload` = Base64 of those bytes), so no canonicalization is needed. The
   payload is parsed/trusted ONLY after the signature verifies. Signing provides authenticity and integrity; no confidentiality
   is claimed or needed (a license is not secret).
4. **Keys.** The server owns the private key (`ILicenseSigner`); clients hold only trusted PUBLIC keys (Base64 SPKI) from the
   `Licensing:TrustedKeys` configuration, each with a KeyId, so several keys can be trusted during rotation. No keys exist in the
   repository. Server key: `LicenseServer:SigningKeyPemPath` (PKCS#8 PEM outside the repo; required outside Development). In
   Development ONLY, an ephemeral key is generated at startup and its public key is logged. Tests generate ephemeral keys.
   No trusted keys configured = nothing verifies = Unlicensed/Invalid (fails closed).
5. **Local storage.** `license.json` + `installation.json` in the licensing folder, written atomically. Integrity comes from
   signature verification on every load: edited data is detected and rejected. This detects tampering; it does not make a client
   tamper-proof. DPAPI/secure-store wrapping is deferred (not needed for authenticity).
6. **States** (distinct conditions, deterministic order in `LicenseEvaluator`): Unlicensed (nothing stored) -> Invalid (bad
   signature / unknown key / malformed / wrong installation / wrong product / not yet valid; carries InvalidReason) -> Revoked /
   Suspended (signed status) -> Expired (now > ValidUntil, or lease+grace elapsed; carries ExpiryKind) -> Active (now <=
   LeaseValidUntil) -> GracePeriod (now <= GracePeriodUntil). All end instants are inclusive.
7. **Lease/grace policy.** Durations are server configuration (`LicenseServerOptions`: LeaseDuration, GraceDuration; Api defaults
   LeaseDays=30, GraceDays=7 are configuration defaults, not commercial policy). Lease and grace are clamped to ValidUntil.
   Client policy `GraceGrantsEntitlements` (default true) decides whether entitlements stay granted during grace.
   Entitlements are granted only in Active (and GracePeriod when the policy allows).
8. **Client/server boundary.** The application layer depends on `ILicenseClient`; HTTP lives only in Client.Licensing.Http (HTTPS
   required except loopback). Network failures become `Licensing.Server.Unreachable` results and never alter the stored license.
   A "successful" server response is never trusted: the signed license must verify, be bound to this installation/product, and
   (for renewal) be the same license with a newer version before it is stored. Licensing.Contracts holds the shared wire model.
9. **Entitlements.** String IDs for modules and features in the signed payload, queried through `ILicenseEntitlementService`
   (Platform.Application). Licensing never references a module implementation; "accounting" is answerable with no Accounting module.
10. **Module runtime integration.** IModule/IModuleManifest are unchanged. `ILicenseEntitlementService.IsLicensed(manifest)` maps a
    manifest's ModuleId to entitlements. LicenseService is one DI singleton (no static state) initialised by LicensingInitializer at
    host start; evaluation is offline, synchronous and follows the clock.
11. **Expiration safety.** Licensing has no database, EF, or business-module reference (enforced by ARCH-LIC-003/004) and only
    answers entitlement queries, so no licensing state can delete or modify business data; a test proves tables and rows are intact
    after expiry, revocation and failed renewals.
12. **Server scope.** Minimal: activate, renew, status changes (`SetStatusAsync`, suspension/revocation reaches clients at their next
    renewal), in-memory repository, Development-only config seeding (`LicenseServer:DevLicenses`). No authentication, admin UI,
    billing, durable storage or multi-tenancy.

---

## Stage 7 Architectural Decisions

1. **Package format** (`.gpkg`, a ZIP): `manifest.json` (a SignedPackageManifest envelope) + `payload/<files>`. Nothing else is allowed in the
   archive. No scripts, installers or SQL: scripts/installers are rejected everywhere (packager, verifier); module packages may not contain
   native executables (only core may). Packages are built deterministically (fixed timestamps, sorted entries).
2. **What is hashed/signed.** The manifest lists SHA-256 + length for every payload file and a `PayloadHash` = SHA-256 over the sorted lines
   `path TAB sha256 TAB length LF`. The signature (ES256, same as licenses) covers the exact UTF-8 JSON bytes of the manifest, so it transitively
   protects every payload byte, path and the file set. `UpdateInfo.DownloadSha256` (hash of the whole file) is ADVISORY early corruption detection only;
   authenticity always comes from the signed manifest.
3. **Trust model reuse.** Licensing and updates share `Security.Es256` (verify with trusted PUBLIC keys by KeyId; unknown key = rejected; several keys
   trusted at once = rotation) and `Security.Es256.Signing` (private keys; only LicenseServer.Infrastructure and UpdatePublisher reference it). Updater
   trusted keys: `Updater:TrustedKeys` (none = nothing verifies = fail closed). No keys are committed; production keys are PEM files outside the repo;
   tests use ephemeral keys. The updater does NOT reference Client.Licensing; it asks `ILicenseEntitlementService` ("is this entitled?") and never
   re-implements LicenseState/Evaluator/Policy.
4. **Package vs runtime manifest.** `PackageManifest` (package layer) holds PackageHash/PayloadHash, KeyId, files, migration metadata. `IModuleManifest`
   and `IModule` were NOT touched and carry no package/update fields (ARCH-UPD-015). `UpdateState` is its own lifecycle, separate from ModuleRuntimeStatus
   and LicenseState.
5. **Verification pipeline** (`PackageVerifier`, mutates nothing): package exists -> opens safely (size/entry/zip-bomb limits, safe unique paths, only
   manifest.json + payload/) -> envelope well-formed -> signing key trusted -> signature valid -> (only now) manifest parsed and identity validated
   (schema, package type, target id, versions, deps, entitlements, file list, migration metadata via the shared ManifestRules) -> payload exactly the listed
   files with matching per-file hashes/lengths and PayloadHash -> target framework + host version range -> version semantics -> module compatibility +
   dependencies (reuses ModuleDependencyResolver on the post-update module set: missing, version conflict, cycles, breaking an installed dependent) ->
   license entitlements -> migration metadata (module packages only; schema never moves backwards) -> installation plan. The same pipeline minus the payload
   step verifies DISCOVERED manifests, so only verified, installable, entitled updates are ever offered or downloaded. (The signature check deliberately
   precedes manifest parsing: the manifest is untrusted until it verifies.)
6. **Versions.** Core and every module version independently using the existing ModuleVersion/VersionRange. A package is accepted only as an upgrade (or a
   new module); same version = Update.AlreadyInstalled, older = Update.Downgrade. Rollback is a separate explicit operation (a pointer switch), never a
   downgrade install. `MinimumHostVersion` means: module package = minimum installed core; core package = minimum installed core it can upgrade from.
7. **On-disk model** (outside the SQLite DB, default `%LOCALAPPDATA%\GenericPOS\Updates`): `downloads/`, `staging/<id>/`, `installed/<target>/<version>/`
   (side by side, never overwritten), `installed/<target>/active.json` (the ONLY switch), `journal/<id>.json`, `restore/<id>/`. Activation = one atomic
   pointer write (temp file + move), so there is never a half-old/half-new installation and no locked running executable is overwritten. The previous version
   stays on disk as the known-good fallback; confirmation prunes versions older than the previous one, never the active or previous version.
8. **State machine** (persisted per update): Discovered, Downloaded, Verified, Staged, MigrationPending, Migrating, ReadyToActivate, Activated, Confirmed;
   failure branches VerificationFailed, MigrationFailed, ActivationFailed, Failed, RecoveryRequired, RolledBack. Activated means "pointer switched, not yet
   confirmed healthy"; the host confirms after a healthy start (`ConfirmHealthyAsync`).
9. **Migrations: orchestrated, not executed.** Packages carry only metadata (`PackageMigration`: from/to schema, `OldBinaryCompatibleWithNewSchema`); the
   migrations themselves live in the module's own assemblies. The updater creates a database restore point (SQLite online backup) BEFORE a migration, then
   asks the module's `IModuleMigrator` (Platform.Application contract, module-owned, touches only that module's tables) to migrate. If no migrator exists the
   migration is DEFERRED to the module's own startup initializer (how all four current modules migrate). The updater never runs SQL and never touches tables.
10. **Binary rollback != database rollback.** Failure matrix: migration fails without modifying data -> MigrationFailed (nothing activates); migration fails
    after modifying data -> RecoveryRequired (restore point kept, NO silent restore); crash/activation failure after an incompatible migration ->
    RecoveryRequired; same with a forward-compatible migration -> safe binary-only recovery (ActivationFailed / auto rollback). Automatic rollback (an
    unconfirmed update after `MaxStartupAttempts` starts) happens only when binaries can be rolled back safely. Restoring the database requires the explicit
    `RollbackAsync(target, restoreData: true)` and discards changes made after the restore point. The database file is never deleted or recreated.
11. **Core updates** use the same mechanism (a separate PackageType): the new core is deployed side by side and activated by pointer switch, so the updater
    never overwrites itself or a running executable. Taking effect at runtime requires a launcher/ModuleHost that follows the pointer (deferred, below).
    Core packages cannot carry migration metadata (modules own schemas).
12. **Failure/offline.** Discovery and download failures are ordinary results (`Update.ServerUnavailable` / `Update.DownloadFailed`), never exceptions;
    `UpdaterInitializer` (startup recovery) is purely local and cannot fail or block startup; downloads are written to `.part` files and only moved into
    place after the advisory hash check; transports require HTTPS except loopback.
13. **Server.** `UpdateServer` serves a directory of `.gpkg` files (read-only repository), answers `check` with the newest newer package per INSTALLED
    target built for the client's runtime and host version, and streams packages. It holds no keys, does no signing, and is unauthenticated (foundation scope).

---

## Stage 8 Summary

### Dependency graph (cross-module references are Contracts-only)
```
Customers        -> (none)
Suppliers        -> (none)
Payments         -> (none)
Users            -> (none)
Audit            -> (none)         every value it stores is a plain string/Guid
CashManagement   -> (none)
Pricing          -> Catalog.Contracts
Purchasing       -> Catalog.Contracts, Suppliers.Contracts, Inventory.Contracts
Reporting        -> optional (soft): Sales.Contracts, Inventory.Contracts, Purchasing.Contracts, Customers.Contracts, Suppliers.Contracts
POS              -> Catalog/Inventory/Sales contracts (unchanged manifest) + OPTIONAL Pricing.Contracts, Payments.Contracts (constructor parameters defaulting to null)
```
Manifest dependencies are declared only for the hard dependencies (Purchasing: catalog, suppliers, inventory; Pricing: catalog). The optional ones (POS -> Pricing/Payments,
Reporting -> everything) are deliberately NOT in any manifest, so no module is forced to exist. `IModuleManifest` was not changed. No cycles (ARCH-POS-020 covers the whole graph).

### Tables and migrations
| Module | Tables | Migration |
|---|---|---|
| Customers | cus_Customers, cus_CustomerAddresses, cus_CustomerContacts | InitialCustomersSchema |
| Suppliers | sup_Suppliers, sup_SupplierAddresses, sup_SupplierContacts | InitialSuppliersSchema |
| Purchasing | pur_PurchaseOrders, pur_PurchaseOrderLines | InitialPurchasingSchema |
| Pricing | pri_PriceLists, pri_Prices | InitialPricingSchema |
| Payments | pay_Payments | InitialPaymentsSchema |
| Users | usr_Users, usr_Roles, usr_UserRoles, usr_RolePermissions | InitialUsersSchema |
| Audit | aud_AuditEntries | InitialAuditSchema |
| CashManagement | cash_Sessions, cash_Movements (filtered unique index: one OPEN session per drawer) | InitialCashManagementSchema |
| Reporting | none | none (stores nothing; DatabaseSchemaVersion 0) |

Every module has its own DbContext and its own migrations; all share the one SQLite file. Foreign keys exist only inside a module (verified against the real database by
Integration.Tests). No migration modifies or drops an existing table.

### New architecture rules
ARCH-CUS-001..016, ARCH-SUP-001..016, ARCH-PUR-001..016, ARCH-PRI-001..016, ARCH-PAY-001..016, ARCH-USR-001..016, ARCH-AUD-001..016, ARCH-CASH-001..016, ARCH-REP-001..016
(Architecture.Tests: 155 -> 299). Per module: layer references (Domain pure, Application -> Domain/Contracts only, UI never -> Infrastructure), Contracts leak no Domain types,
cross-module references limited to the allowed *.Contracts set (ARCH-xxx-011), no HTTP, no licensing/update coupling, own DbContext/prefix, no other module references the
module's non-Contract layers. ARCH-POS-019 was relaxed from "POS references no Payments assembly" to "only Payments.Contracts".

### Tests
New: Customers 38, Suppliers 38, Purchasing 45, Pricing 41, Payments 29, Users 50, Audit 26, CashManagement 34, Reporting 34, Integration 32, Architecture +144.
Extended: POS 84 -> 95, Inventory 89 -> 92. Total 903 -> 1428, 0 failures. Each module has domain tests, application tests (in-memory SQLite through Tests.Common, other modules'
contracts replaced by stubs), generated infrastructure tests (module lifecycle, manifest, migration applies to a real file, hosting registration) and contract tests.

### Project references / packages
No new NuGet package in any module (Microsoft.EntityFrameworkCore.Sqlite/Design 10.0.11, Microsoft.Extensions.Configuration.Binder 10.0.11 and Microsoft.Extensions.Hosting.Abstractions
10.0.11 as in the existing modules). Tests: Tests.Common (EF Core Sqlite, DI), Integration.Tests (Microsoft.Data.Sqlite 10.0.11). New project references are the allowed *.Contracts
projects listed in the dependency graph above, plus Pricing.Contracts and Payments.Contracts from POS.Application.

### DI registrations (Stage 8)
Per module `Add<M>Module(services, configuration)`: `<M>DbContext` (SQLite, same file, migrations assembly = <M>.Infrastructure), `Add<M>Core()` (unit of work, repositories, contract
services - all Scoped - and command/query handlers - Transient), `IModule` singleton, `<M>DatabaseInitializer` hosted service. Reporting registers no DbContext and no initializer.
Contract services registered: ICustomerLookup/ICustomerReader, ISupplierLookup/ISupplierReader, IPurchaseOrderReader, IPriceResolver, IPaymentService/IPaymentReader,
IUserLookup/IUserPermissionChecker, IAuditRecorder/IAuditReader, ICashMovementRecorder/ICashSessionReader, IReportProvider; Inventory additionally registers IStockReceiptService.
Optional consumers (POS handlers, Reporting handlers) take the contract as a nullable constructor parameter with a null default, so the container passes null when the module is absent.

### Startup sequence
Unchanged in shape: App.xaml.cs adds the hosting modules after POSHostingModule in this order - Reporting, CashManagement, Audit, Users, Payments, Pricing, Purchasing, Suppliers,
Customers; the host starts hosted services in registration order, so each module initializer applies its pending migration before the main window is shown. A missing module simply
means a missing registration: nothing else fails.

### Stage 8 architectural decisions
1. **Contracts-only, optional by construction.** Hard needs go into the manifest (Purchasing, Pricing); soft needs are nullable constructor parameters. The same host composition works with any subset of Stage 8 modules (Integration.Tests proves it).
2. **No distributed transactions.** Purchasing receives a purchase order line by line through `IStockReceiptService` (new, minimal Inventory contract delegating to AddStock), saving after every line; it is resumable and idempotent per line, and a partial receipt leaves the order Submitted with the already-received lines marked (cancelling it is then refused), instead of pretending to be atomic.
3. **POS is extended minimally.** `AddProductToCart` asks Pricing for a price when the module exists (snapshotted on the cart line); `CheckoutAsync(cartId, reference, POSPaymentRequest?)` records a payment through Payments after the sale is confirmed and before stock is issued. A failed payment cancels the sale; a failed stock issue voids the payment and cancels the sale. A payment request without the Payments module is rejected up front (POS.Checkout.PaymentsUnavailable).
4. **Payments records, it does not process.** No gateway, no hardware; a payment points at anything through a generic (type, id) reference; cash tendered/change is computed; voiding keeps the record.
5. **Users is identity and permission data only.** No password, credential, session or sign-in exists (authentication/security architecture belongs to a later stage); permission codes are stored, never interpreted; nothing enforces them yet.
6. **Audit is append-only.** The entity has no mutators and the module exposes no update or delete at any layer; modules record to it through `IAuditRecorder` with plain values, so Audit knows no other module.
7. **CashManagement tracks cash only.** One open session per drawer (application check plus a filtered unique index), outflows cannot take the drawer below zero, a movement reference makes the contract call idempotent per kind (retries never double-count), the counted close stores expected amount and variance.
8. **Reporting stores nothing.** Pure calculators in Reporting.Domain; each report is an independent section that is Unavailable (not an error) when its source module is missing; the sales report scans a bounded window and says so (IsTruncated).
9. **SQLite specifics.** Money is stored as TEXT through value converters; decimal aggregates are computed in memory (SQLite cannot translate them); search uses EF.Functions.Like with escaping; case-insensitive uniqueness is enforced by normalising on write.
10. **Verification uses the real thing.** Besides per-module in-memory tests, Integration.Tests start the real host and apply the real migrations to a temporary file; they never touch the user's %LOCALAPPDATA% database.

---

## Stage 9 Summary

### Scope decisions (read before changing anything)
1. **Roadmap vs the Stage 9 task text.** The task said not to implement "cloud backup" unless the documents explicitly place it in Stage 9. The roadmap does: Stage 9
   lists the BackupServer component and the "Cloud backup" capability. So the SERVER side (BackupServer) is implemented. The CLIENT side (the optional CloudBackup module,
   `IBackupClient`/`HttpBackupClient`, scheduling, encryption/packaging of the SQLite file) is the Stage 8 optional module list and was NOT built: no client can back up yet.
2. **Server database technology.** The documents say only "ASP.NET Core" for the cloud backend and name no server database. Decision (reversible, behind repository ports):
   EF Core + SQLite, the stack already in the solution, in the SERVER's own file (`CloudDatabase:ConnectionString`), WAL mode, migrations owned by Cloud.Infrastructure
   (`InitialCloudSchema`; EF Core 10 serialises concurrent hosts with `__EFMigrationsLock`). Moving to PostgreSQL/SQL Server later means a new provider + migrations in
   Cloud.Infrastructure only. The architecture lead should confirm this choice before production.
3. **One backend, four hosts.** The architecture says the cloud projects "should initially remain part of one deployable backend where practical". Decision: the four
   components stay separate deployables (LicenseServer.Api, UpdateServer.Api, BackupServer.Api, AdminPortal.Api) over ONE server database and shared storage directories, so the
   customer-facing hosts (license, update, backup) can be exposed while the vendor administration host stays on an internal network. They can be co-hosted later.
4. **Compatibility.** Stage 6/7 wire contracts, ES256 signing, client verification and the Stage 6/7 hosts' default behavior are unchanged: without `CloudDatabase:ConnectionString`
   LicenseServer.Api still uses the in-memory store (Development only) and UpdateServer.Api still serves the package directory. All 109 licensing and 168 updater tests pass unchanged.

### Configuration reference (all environment-specific values; no secrets in source control)
Key | Used by | Meaning
----|---------|--------
CloudDatabase:ConnectionString | all four hosts | server database (required for Admin/Backup; License/Update use it when present; License requires it outside Development)
CloudDatabase:MigrateOnStartup | all four hosts | apply migrations at start (default true)
UpdateServer:PackageDirectory | Admin (writes), Update (reads) | where package bytes live (required with the durable catalog)
UpdateServer:TrustedKeys:n:KeyId/PublicKey | Admin | optional PUBLIC keys; when set, a package whose signature does not verify is rejected at publication
UpdateServer:MaxPackageBytes | Admin | upload limit (default 512 MiB)
BackupServer:StorageDirectory | Backup, Admin | where backup bytes live (required)
BackupServer:MaxBackupBytes / MaxBackupsPerLicense / RequiredModule | Backup | 256 MiB / 10 / "cloud-backup" (an explicitly empty RequiredModule = no entitlement needed)
AdminPortal:Keys:n:Name / Sha256 | Admin | administrator credentials: a name and the SHA-256 (hex) of the API key; no key configured = nobody can call the API
LicenseServer:SigningKeyPemPath / KeyId | License | unchanged from Stage 6 (PKCS#8 PEM outside the repo; required outside Development)
Development only: no admin key configured -> an ephemeral key is generated and logged once; `appsettings.Development.json` of AdminPortal.Api/BackupServer.Api points
at `dev-data/` (git-ignored). Producing a key hash (works in Windows PowerShell 5.1 and pwsh): `(([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes($key)) | ForEach-Object { $_.ToString('x2') }) -join '')`; generate a key with `AdminKeys.Generate()` (prefix `gpa_`).

### API reference
AdminPortal.Api - every /api/admin request needs `Authorization: Bearer <admin key>` (401 otherwise, `Cache-Control: no-store`); errors are `ApiError{code,message}` (400 validation,
403, 404, 409 conflict/invalid state, 413 too large). GET /health is open.
- GET /dashboard; GET /audit (actor, action, entityType, entityId, from, to, page, pageSize)
- POST/GET /customers, GET/PUT /customers/{id}, POST /customers/{id}/deactivate|reactivate
- POST/GET /licenses (create returns the activation key ONCE), GET /licenses/{id}, POST /licenses/{id}/suspend|reinstate|revoke (optional reason), POST .../extend,
  PUT .../entitlements, POST .../release-installation, POST/DELETE .../backup-token (token returned ONCE), GET /installations
- POST/GET /modules, GET/PUT /modules/{id}, POST /modules/{id}/retire|reactivate
- POST /packages (body = the signed .gpkg, ?releaseNotes=), GET /packages (targetId, status), GET /packages/{id}, POST /packages/{id}/withdraw|restore
- GET /backups (licenseId), DELETE /backups/{id}
BackupServer.Api - `Authorization: Bearer <backup token>`: POST /api/backups (raw bytes; optional `X-Backup-Sha256`, `X-Client-Version`, `?label=`), GET /api/backups, GET /api/backups/{id},
GET /api/backups/{id}/content (+ `X-Backup-Sha256`), DELETE /api/backups/{id}. LicenseServer.Api (activate, renew) and UpdateServer.Api (check, download) are unchanged.

### Tables (server database; prefixes keep them apart from desktop tables)
lic_Licenses (activation key stored as SHA-256 only, modules/features as JSON, RowVersion concurrency stamp), upd_Packages (metadata + verbatim signed envelope; unique target+version+framework),
bak_Backups, bak_AccessTokens (token hash only), adm_Customers (unique case-insensitive name), adm_Modules, adm_AuditLog (append-only).

### Stage 9 architectural decisions
1. **Layering.** Application layers (AdminPortal/BackupServer/LicenseServer/UpdateServer .Application) hold the rules and are free of ASP.NET, EF, HTTP and SQLite; Cloud.Infrastructure is the only
   server project with EF Core; the Api hosts are thin endpoints (no DbContext) over services that return `ServiceResult<T>` carrying contract DTOs (enforced by ARCH-CLD-008/014).
2. **Administration authentication = minimum necessary.** Static API keys held as hashes in configuration, constant-time comparison, a name per key recorded in the audit log. It is NOT
   the desktop Users module (ARCH-CLD-009) and not a general identity system (no passwords, SSO, 2FA, roles, lockout, rate limiting: Stage 11).
3. **Secrets.** The activation key and the backup token are shown once and stored only as SHA-256 hashes; admin keys are only configured as hashes; audit entries never contain secrets
   (tested); no private key exists in server code (ARCH-CLD-011/013); packages and licenses are signed elsewhere (UpdatePublisher, LicenseServer signer).
4. **License administration works on the existing record.** Creating, suspending, reinstating, revoking, extending, changing entitlements and releasing the installation change the
   `LicenseRecord`; clients learn at their next renewal exactly as in Stage 6 (verified end to end: suspended + new entitlements arrive in a verified ES256 payload). Revocation is terminal.
   Licenses may only entitle REGISTERED, ACTIVE modules (the module registry is the vendor's authority); removing a module is always allowed.
5. **No lost updates.** The durable license repository saves with an optimistic RowVersion; a stale save throws `LicenseConcurrencyException` (admin gets 409), so a renewal can never
   overwrite a concurrent revocation.
6. **Package publication gate.** `PackageInspector`: opens the .gpkg safely, parses the manifest, applies the shared `ManifestRules`, checks the payload against the manifest (listed files,
   lengths, SHA-256, PayloadHash) and, if trusted public keys are configured, the ES256 signature. Module packages need an active registry entry; duplicate package IDs and duplicate
   target+version(+framework) are conflicts (even if withdrawn). The server never signs and clients still verify everything. Withdrawn packages are neither offered nor downloadable.
6b. **Registry/versions.** Module versions come from the package catalog (no duplicate bookkeeping); `core` is not a module and cannot be registered.
7. **Backup access.** A backup token is issued per license by the vendor; it identifies the license (not a user). Uploads need an Active, unexpired license that includes the `cloud-backup`
   module; READING, restoring and deleting one's own backups stays possible while suspended or expired (expiry never strands customer data) and stops only on revocation or token
   revocation. Backups are opaque: streamed to a staging file, size-limited, hashed on the way in (a claimed hash that does not match is rejected), then promoted; the oldest backups beyond
   the per-license retention count are removed after a successful upload. One license can never see another's backups.
8. **Offline-first.** No Platform, Client or business-module project references any server assembly or project (ARCH-CLD-006/007 inspect assemblies AND project files); the Stage 9 integration
   test runs the full POS path on the real host with no server assembly loaded. The server hosts never touch the desktop database and the desktop never touches the server database.
9. **HTTPS.** All four hosts apply HSTS and HTTPS redirection outside Development; TLS termination/reverse-proxy configuration remains a deployment concern (Stage 14).

### Stage 9 limitations and deferred work
- No client for the cloud: Client-side CloudBackup module, IBackupClient/HttpBackupClient, backup scheduling, client-side encryption and restore flow are NOT built; nothing on the desktop calls
  BackupServer.Api or AdminPortal.Api. Backup content is opaque to the server; encryption/key management and stronger client authentication ("Backup security", "Secure cloud communication")
  belong to Stage 11. The backup token is a bearer secret per license (identification by possession).
- AdminPortal is an API only (no browser UI). Admin authentication is static API keys (see decision 2); no per-administrator permissions, key rotation tooling, lockout or rate limiting.
- Vendor-platform areas of architecture sections 48-52 that are NOT built: plans/products catalog, billing/payments, customer requests/support, activity feed, telemetry, installation limits
  (a license binds ONE installation; `release-installation` is the transfer mechanism), activation-key rotation, module compatibility/dependency metadata beyond what packages carry.
- Server database is SQLite (single node, shared by hosts on one machine through the file); a multi-node deployment needs another provider (see decision in scope item 2). Backup/package bytes are
  local directories, not object storage. Retention is by count only (no byte quotas, no time-based expiry).
- LicenseServer.Api does not translate `LicenseConcurrencyException` (a rare activate/renew vs admin-change race answers 500; the client treats it as unreachable and retries). Admin gets a clean 409.
- Package publication by the server is optional-signature-checked (needs `UpdateServer:TrustedKeys`); there is no staged rollout, release channel or delta package.
- Still deferred from earlier stages and unchanged: license ENFORCEMENT in the desktop, runtime adoption of activated updates (launcher/ModuleHost), IModuleMigrator implementations, update CLI
  wrappers, authentication on top of Users, Stage 8 adoption items.

---

## Known Issues / Blockers

None blocking. Stage 9 is complete.
Build: 0 errors, 0 warnings (115 projects).
All 1633 tests pass (Cloud.Tests 187, Architecture.Tests 315, Integration.Tests 34, others unchanged).
Resolved during Stage 9: stress-running Cloud.Tests exposed rare random failures (about 1 run in 8, different tests each time, SQLite connection-open errors). Cause: the test teardown called the
process-wide `SqliteConnection.ClearAllPools()` while other tests ran in parallel. Fix: test databases use `Pooling=False` (no global pool clearing); staging-file cleanup in the file stores also
gained short retries (a briefly locked file never fails an upload) and stale `.part` files are swept at start. 30 consecutive full Cloud.Tests runs passed afterwards.
(Pre-Stage-9 status for reference: Stage 8 had 1428 tests, 0 warnings, 108 projects.)
Catalog, Inventory, Sales, POS and every Stage 8 module (except Reporting, which has no tables) apply their own migrations at startup.
Database: %LOCALAPPDATA%\GenericPOS\genericpos.db (Platform + all module tables in the same file).

Remaining limitations after Stage 8 (deferred work, none of it is a Stage 8 requirement):
- AUDIT IS NOT ADOPTED: no module records to Audit yet. Adoption means giving a module an optional `IAuditRecorder` constructor parameter (the POS/Pricing pattern); it was left out to keep Stage 1-7 modules untouched.
- USERS IS NOT AUTHENTICATION: no passwords, credentials, sessions or sign-in; permission codes are stored but no module checks them; the POS cashier is still a free-text reference not linked to a Users record.
- CASHMANAGEMENT IS NOT CONNECTED TO POS/PAYMENTS: cash sales are not recorded into a drawer session automatically; `ICashMovementRecorder` is ready (idempotent per reference) for a later optional integration.
- CUSTOMERS/SUPPLIERS: a sale does not carry a customer; the Catalog product has no supplier link; no credit, loyalty or statements.
- PURCHASING: whole-line receiving only (no partial quantities, no supplier returns, no cost update back to Catalog); a failed multi-line receipt is resumable but not rolled back (no stock-reversal contract exists).
- PRICING: price lists with effective periods and quantity breaks only; no customer-specific prices, promotions, discounts, tax or currency; POS still has Total == Subtotal.
- PAYMENTS: records only (no gateway, no hardware, no refunds beyond voiding); POS pays the full cart total with one method (split payments exist in the Payments API but are not offered by POS).
- REPORTING: minimal reports; the sales report scans at most 2000 recent sales (Sales.Contracts only exposes a recent list) and flags IsTruncated; no export, scheduling or caching.
- The Stage 8 UI projects are minimal view models, not hosted in MainWindow, and (net10.0-windows) not covered by Architecture.Tests; module feature entitlements are not enforced (licensing is still not enforced anywhere).
- Everything listed under "Remaining limitations after Stage 7/6/5D" still applies, except that Payments and Users now exist as modules (the 5D line "no Payments module" is superseded by the Stage 8 integration).

Remaining limitations after Stage 7:
- RUNTIME ADOPTION IS NOT WIRED: an Activated update changes the active pointer and leaves a verified, deployed version on disk, but nothing yet loads from
  `installed/<target>/<version>`: there is no launcher that starts the active core version, and ModuleHost still uses the modules compiled into the app
  (its file-system discovery scans `<AppBase>/modules`, not the updater's directories). Until that integration exists, installing an update does not change
  what runs. The host must also call `UpdateService.ConfirmHealthyAsync` after a healthy start (not yet wired in App.xaml.cs; unconfirmed updates are
  rolled back after MaxStartupAttempts starts by design).
- No business module implements `IModuleMigrator` yet; migrations are DEFERRED to each module's own startup initializer, so the pre-activation migration step
  is only exercised with test doubles. The restore point is a full database copy (disk usage) and RESTORE must run while the database is not in use.
- No automatic update checks and no UI: the host/code must call `IUpdateService`. ModulePackager/UpdatePublisher are libraries (no CLI wrappers yet).
- UpdateServer: directory-based, unauthenticated, no publishing API, no admin/portal, no staged rollout or delta packages; HTTPS is expected at the
  host/reverse proxy. Packages are not encrypted (signatures give authenticity/integrity, not confidentiality).
- The advisory whole-file hash comes from the same (untrusted) server; it only catches corruption. Anti-rollback of the signed content relies on the
  version rules (no downgrades), not on a signed "latest version" list.
- Core activation takes effect only after a restart by design; there is no in-place hot swap.

Remaining limitations after Stage 6:
- NO ENFORCEMENT YET: nothing in the runtime or UI denies access based on LicenseState. Business modules do not consult
  ILicenseEntitlementService, so an unlicensed installation still runs POS. Choosing enforcement points and the restriction UX
  is deferred; the mechanism (state + entitlements, tested) is ready.
- No clock-rollback protection (a user can move the system clock back to extend a lease); no secure time source.
- Local files are integrity-protected by signature only (no OS secure storage); an attacker with file access can delete the
  license (-> Unlicensed) but cannot forge one without the private key.
- License server: in-memory store (licenses are lost on restart), no authentication, no admin/portal, no billing; the
  activation key is the only credential. Activation key brute-force/rate limiting is not implemented.
- A license can be bound to one installation at a time; transferring/deactivating an installation is not implemented.
- No licensing status UI. Trusted public keys must be supplied in configuration.
- (Stage 7 now reuses the Stage 6 trust model through Security.Es256; licensing itself is unchanged and still not enforced anywhere.)

Remaining limitations after Stage 5D:
- No payment processing (no Payments module).
- No stock reversal contract: partial stock issue during a failed checkout needs manual correction.
- No distributed transaction across modules (see decision 4).
- POS.UI is a minimal view/view-model, not wired into MainWindow, no real-hardware input.
- Discounts, tax and pricing rules are not applied in POS (Total == Subtotal); Sales receives discount 0, tax 0.

Notes:
- Stage 5C fixed a defect in SalesReader.FindByIdAsync (EF could not translate `s.Id.Value == guid`
  against the SaleId value converter; now compares `s.Id == new SaleId(guid)`).
- Sales.UI is not covered by Architecture.Tests (net10.0-windows TFM gap, same as other modules).
- CompleteSaleCommandHandler does not yet reduce stock or process payments (Stage 5D / Payments).
- Sales design: Sale/SaleItem/Return/ReturnItem/SalesTransaction. The earlier planning names
  SalesOrder/SalesOrderLine/SalePayment were superseded; Payments is a separate future module.
- (Fixed in Stage 9) The header text "Cloud Backend ... Stage 6" was stale: roadmap is 6 Licensing, 7 Updates, 9 Cloud Services & Administration.

---

Last updated: 2026-10-05 - Stage 9 complete (AdminPortal, BackupServer, durable License/UpdateServer persistence, Cloud.Contracts, Cloud.Infrastructure). 1633 tests, 0 warnings; Stage 10 not started.
