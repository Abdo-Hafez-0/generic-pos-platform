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

**Stage 13 COMPLETE (architecture and integration verification): the architecture was audited against the repository, the twelve mandatory dependency rules are enforced on every project (including the WPF ones), and the module lifecycle now really runs**

Stage 13 audited the code, not the documents, and found that the runtime module lifecycle of sections 11/14/39-41 had never been executed: the module registry was empty in the real desktop, so the updater could not see the installed modules and a module composed without its dependencies started half-working. The fix was approved before it was made.

    Composed modules (IModule of each hosting module)
        ---> ModuleLifecycleService (Client.ModuleHost, after every hosted service, i.e. after the databases are ready)
                 Validate (ModuleDependencyResolver) -> Initialize -> Register (IModuleRegistry) -> Start, in dependency order; Stop in reverse on shutdown
                 invalid composition / init or start failure => the application does not start; the user sees a plain sentence (ModuleCompositionException)
        ---> IModuleRegistry ---> InstalledStateProvider (updater): downgrades, schema and dependent checks see the real installed modules
    Architecture rules: Architecture.Tests Solution/ (ARCH-SOL-001..022) read the .csproj graph, the module folders, manifests, migrations and contract surfaces

Implemented and tested: see "Stage 13 Summary" (audit, dependency map, document comparison, defects, rule matrix, verification status per area, limitations) and "Stage 13" under Completed Work.

### Previous phase - Stage 12 COMPLETE (offline and failure testing): the platform is proven correct and usable when things fail - no network, a cloud that fails in every way, a locked or damaged database, dying peripherals, interrupted operations**

Stage 12 proved the offline-first claim with real SQLite, the real desktop composition and deterministic failure injection, and in doing so found and fixed the one structural weakness: a business operation that spans modules was not one transaction.

    POS checkout (Sales + Payments + Inventory + POS) and Purchasing receive (Purchasing + Inventory)
        ---> IAtomicOperation (Platform.Application)  ---> SharedDatabaseScope (Platform.Infrastructure)
                 ONE connection per DI scope shared by the business module contexts, ONE real SQLite transaction (BEGIN IMMEDIATE ... COMMIT)
                 failed Result / exception / lost connection => the DATABASE rolls everything back (no compensating writes), contexts forget what was undone
    After the commit only: receipt, drawer (devices cannot be rolled back and can never undo a sale)
    Cloud (license / update servers): every failure is a non-fatal result with a plain message; local work never waits for, or depends on, it

Implemented and tested: see "Stage 12 Summary" (defects found and fixed, decisions, failure matrix, offline workflows, recovery scenarios, limitations) and "Stage 12" under Completed Work.
Stage 11 is unchanged in behavior; its decisions (offline authentication, capabilities, license enforcement, clock rollback, protected identity) were re-verified under failure.

### Previous phase - Stage 11 COMPLETE (security hardening): offline authentication, capability-based authorization enforced in the application handlers, centralized license enforcement, protected local licensing state, audited security events**

Security is now a layer of the platform, not a property of the screens. Every mutating business handler refuses before it acts unless the signed-in user CURRENTLY holds the capability its module declared; license
entitlements are enforced in that one place; the installation identity and the clock mark are protected at rest; every security-relevant event reaches the append-only audit log without ever carrying a secret.

    UI / script / test ---> handler ---> IAuthorizationService.AuthorizeAsync(capability)
                                              |  known capability? signed in (ICurrentUser)? user holds it NOW (IPermissionProvider = Users, live)?
                                              |  module licensed (ILicenseEntitlementService, offline) unless the capability is LicenseRequirement.None?
                                              +--> denial => Security.* error + security event; the operation never starts
    Users:  username + password -> PBKDF2 hash (never the password) -> lockout -> SignIn -> ISessionManager (identity only, no permissions)
    Events: SecurityEvent (no field for a secret, sanitised text) -> ISecurityEventSink -> listeners: application log + Audit module (append-only, buffered until the audit store exists)
    Local state: installation identity + clock high-water mark -> ISecretProtector (Windows DPAPI, current user, purpose-bound) ; clock rollback => license Invalid(ClockRollback)
    Servers: per-caller authentication throttle (429), HTTPS-only outside Development (plain HTTP refused), security headers, customer backup activity audited, fail-closed start-up.

Implemented and tested: see "Stage 11 Summary" (decisions, capability list, configuration, limitations, deferred work) and "Stage 11" under Completed Work. The Stage 6 documented limitation "no clock-rollback protection" and the Stage 8/10
limitations "nothing enforces permissions" / "drawer opens are not permission-checked" are resolved. The 2026-10-06 review added the desktop start screen (first-run setup, sign-in, password change, sign-out)
and recorded explicit decisions on read boundaries, clock rollback and every deferred limitation ("Stage 11 Summary - Stage 11 review").

### Previous phase - Stage 10 COMPLETE (hardware & device integration): five vendor-neutral hardware abstractions, replaceable adapters in Client.Hardware, optional POS integration**

Peripherals are an OPTIONAL infrastructure capability. The POS never constructs or knows a device: it receives `IReceiptPrinter`, `ICashDrawer`, `ILabelPrinter`,
`IScale` and `IBarcodeScanner` (Platform.Application) through dependency injection as nullable parameters, and a missing, broken or throwing device can only
ever produce a hardware notice or a failed result - never a changed sale, stock level or payment.

    POS.Application / POS.Infrastructure --(optional, nullable)--> Platform.Application.Abstractions.Hardware (IReceiptPrinter, ICashDrawer, ILabelPrinter, IScale, IBarcodeScanner)
    Client.Hardware (adapters, chosen by "Hardware" configuration; registered by HardwareHostingModule in the desktop composition root only)
        KeyboardWedgeBarcodeScanner | EscPosReceiptPrinter + EscPosCashDrawer (ESC/POS) | ZplLabelPrinter (ZPL) over TcpDeviceTransport / FileDeviceTransport | Null* placeholders
    Checkout: ... save the sale (final) -> THEN print the receipt / open the drawer; failures become POSHardwareNotice entries on the result.

Implemented and tested: the abstractions and their error vocabulary, the adapters above, configuration-driven selection, POS auto-print + cash-drawer kick + reprint +
labels + weight reading + scanner-to-cart bridge + device status, fake hardware, failure-isolation tests on the real host and database, and ARCH-HW-001..014.
No physical device was available: protocols are verified byte-for-byte against fakes, loopback TCP and device-path files, not against real printers. See "Stage 10 Summary".

### Previous phase - Stage 9 COMPLETE (cloud services & administration): AdminPortal (vendor administration API), BackupServer (cloud backup API), durable server persistence for the license and update servers**

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
- [x] 8I Reporting (no tables, no DbContext, no migration - it stores nothing): read-only reports built from other modules' read contracts - sales report (completed sales in a range with daily breakdown, bounded scan window flagged IsTruncated), inventory snapshot, purchasing overview, customer and supplier summaries, and a business overview where each section stands alone; pure calculators in Reporting.Domain (DateRange max 366 days, SalesCalculator, StockCalculator); SOFT dependencies on Sales/Inventory/Purchasing/Customers/Suppliers contracts: each source reader is an optional constructor parameter, so a missing module makes only its own section Unavailable (never an error); contract IReportProvider; Reporting.Tests (34); ARCH-REP-001..016. Limitation: Sales.Contracts only exposes a recent-sales list, so the sales report scans at most 2000 sales (IsTruncated tells the caller). **[Superseded in FIX-12: Sales.Contracts has a ranged completed-sales read; see "FIX-12 Summary".]** No export, scheduling or advanced analytics (Stage 8 minimum)
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

### Stage 10 - Hardware & Device Integration (COMPLETE)
Scope source: roadmap Stage 10 ("Integrate physical POS hardware through abstractions": IBarcodeScanner, IReceiptPrinter, ILabelPrinter, ICashDrawer, IScale; business logic never depends on
hardware, implementations replaceable, hardware failures must not corrupt business data); Architecture & Solution Design.md section 72 ("The POS depends on interfaces. Concrete hardware
drivers are infrastructure implementations") and section 8 (Platform.Infrastructure lists "Hardware infrastructure"); Generic Platform document section 35 ("independent components").
- [x] Platform.Application/Abstractions/Hardware (new): IHardwareDevice (GetStatusAsync), DeviceState/DeviceStatus, HardwareErrors (Hardware.NotConfigured/Unavailable/Failed/Timeout/InvalidData),
      HardwareGuard (turns any driver exception into a failed Result; cancellation is not a hardware failure), IBarcodeScanner (event + Start/Stop) + BarcodeScan, IReceiptPrinter + ReceiptDocument/ReceiptLine/ReceiptPayment,
      ILabelPrinter + LabelDocument, ICashDrawer, IScale + WeightReading/WeightUnit (Kilograms conversion). They use the existing Platform.Core Result/Error.
- [x] Client.Hardware (new, plain net10.0, no WPF/EF/HTTP/business/server/vendor SDK/System.IO.Ports): HardwareOptions ("Hardware" section, every device defaults to None), IDeviceTransport with TcpDeviceTransport and
      FileDeviceTransport, EscPosReceiptFormatter + EscPosReceiptPrinter + EscPosCashDrawer, ZplLabelFormatter + ZplLabelPrinter, KeyboardWedgeBarcodeScanner + IKeyboardInputSink,
      Null* placeholders, HardwareFactory (the only place that maps a configured Type to an adapter), AddClientHardware + HardwareHostingModule
- [x] POS.Contracts: IPOSDevices (PrintReceiptAsync(cartId), OpenCashDrawerAsync, PrintProductLabelAsync, ReadWeightAsync, GetDeviceStatusAsync), IPOSBarcodeInput (BindCart/Start/Stop + ScanProcessed),
      POSHardwareNotice, POSScanOutcome, POSWeightResult, POSDeviceStatusResult; POSCheckoutResult gained an optional trailing HardwareNotices (source compatible)
- [x] POS.Application: PosReceiptOptions ("PosReceipt" section: store name, header/footer, AutoPrintReceipt, AutoOpenDrawerOnCashSale), PosReceiptFactory, PrintReceipt/OpenCashDrawer/PrintProductLabel handlers, ReadWeightQueryHandler
      (rejects negative/implausible/unknown-unit readings), GetDeviceStatusQueryHandler; CheckoutCartCommandHandler gained optional printer/drawer/options/time parameters and a final step 9
- [x] POS.Infrastructure: POSDevices (IPOSDevices), POSBarcodeInput (singleton; one scan at a time through IPOSService.AddProductAsync, every failure becomes a rejected outcome, subscribers isolated), logging of hardware notices, DI registration
- [x] Client.Desktop: registers HardwareHostingModule; appsettings.json gained "Hardware" (all None) and "PosReceipt"; POS.UI PosViewModel shows HardwareMessage after checkout (UI touches no hardware type)
- [x] Tests: Tests.Common/Hardware fakes (FakeBarcodeScanner/ReceiptPrinter/LabelPrinter/CashDrawer/Scale with Works/NotConfigured/Unavailable/Timeout/Throws modes); tests/Hardware.Tests (97: abstractions, guard, scanner decoding,
      ESC/POS + ZPL bytes, TCP loopback and device-path transports, factory/configuration/DI); POS.Tests +43 (PosHardwareTests: receipt content, drawer rules, every failure mode leaves the sale intact, reprint, labels, scale, status,
      scanner bridge); Integration.Tests +7 (HardwareIsolationTests: real host + real SQLite + real Payments: failing peripherals never alter the durable sale, restart keeps it, real ESC/POS bytes on a device path, offline printer notice);
      Architecture.Tests ARCH-HW-001..014 (HardwareBoundaryTests)
- [x] Build: 0 errors, 0 warnings (117 projects); all 1794 tests pass
- [x] Not done by design (later stages / not required): physical-device protocol adapters beyond ESC/POS, ZPL and keyboard wedge (Windows spooler printing, serial/USB scales, vendor SDKs), customer displays, POS terminals,
      hosting PosView in MainWindow (so nothing forwards key presses to IKeyboardInputSink yet) [done in FIX-01b / FIX-02], authorization of "no sale" drawer opens (Stage 11), the failure-testing campaign (Stage 12)

### Stage 11 - Security Hardening (COMPLETE)
Scope source: roadmap Stage 11 (authentication, authorization, capability-based permissions, password/security policies, data protection, license protection, package signature verification, update verification, secure cloud
communication, backup security, installation identity, audit trail, tamper resistance); Architecture & Solution Design.md sections 2.5, 69-71 (authentication/authorization separate; capability-oriented permissions that modules register);
Generic Platform document sections 18 and 53. 17 commits (see "Stage 11 Summary - commit history"), each built and tested before it was committed.
- [x] Platform.Application/Abstractions/Authorization + Security (new): ICurrentUser (identity only; HasPermission/HasFeature were removed - they were unused and would have cached what must be live), AuthenticatedIdentity, ISessionManager + SessionContext,
      IAuthorizationService + AuthorizationService (fail closed; also the license gate), IPermissionProvider, CapabilityDescriptor/ICapabilityProvider/ICapabilityCatalog/CapabilityCatalog/CapabilityCodes, SecurityErrors,
      SecurityEvent (sanitised, no secret field) + SecretRedactor, ISecurityEventSink/ISecurityEventListener/SecurityEventDispatcher/NullSecurityEventSink, ISecretProtector. Client.Host registers them for every host (AddPlatformSecurity).
- [x] Users (additive migration AddUserCredentials, table usr_UserCredentials): PasswordPolicy/LockoutPolicy (configuration can only tighten), UserCredential, PBKDF2-HMAC-SHA256 hasher (600,000 iterations default, floor 100,000, per-hash 16-byte salt, self-describing and
      upgradeable, constant-time compare), SignIn/SignOut/ChangePassword/SetUserPassword/BootstrapAdministrator handlers, UsersPermissionProvider (live), UsersCapabilities; every Users command needs users.manage and the four read queries users.view.
- [x] Capabilities declared and enforced by their handlers (31 total, 14 marked sensitive): users, pos (session, sale, receipt reprint, drawer, label), catalog, inventory (receive, adjust, locations), purchasing, payments, cash management, customers, suppliers,
      pricing, reporting.view, audit.view, licensing.manage, updates.manage. Handlers that serve both a person and another module through a contract (AddStock, RecordPayment, VoidPayment, RecordCashMovement, the audit read) authorize then delegate to an
      internal ExecuteAsync that only the module's own contract service can call.
- [x] License enforcement: AuthorizationService checks the owning module against ILicenseEntitlementService after the user's own permission; capabilities declared LicenseRequirement.None (reports, audit, users, license management, updates) work in every license state;
      Security.LicenseRestricted says the data is safe; LicenseService records activated/renewed/rejected/loaded/clock-rollback events; Activate/Renew handlers need licensing.manage.
- [x] Data protection: Client.Security (new, DPAPI current-user, purpose-bound via ISecretProtector); protected installation identity (unusable file kept as evidence and audited, never silently reused; legacy plain text only via Licensing:AllowLegacyPlaintextIdentity);
      ClockRollbackGuard (persisted protected high-water mark floored by the signed license issue time + monotonic stopwatch within a run; tolerance 5 min..7 days; recovery by correcting the clock or an online renewal).
- [x] Updates: PackageVerifier/UpdateService record security.update.rejected/accepted/failed/rolledback; Download/Install/Rollback handlers need updates.manage; the Stage 7 verification pipeline is unchanged (it was already signature-first).
- [x] Audit: AuditSecurityEventListener (buffered, ordered, bounded) writes every security event as module "security"; Users commands record who changed which user/role/permission.
- [x] Cloud: AuthenticationThrottle (Cloud.Contracts) on admin keys, backup tokens and activation keys (429 + Retry-After); ICloudSecurityLog into the shared adm_AuditLog (failed/blocked authentications, customer backup upload/download/delete/refused upload);
      Cloud.Hosting (new, ASP.NET plumbing): security headers + transport security (HSTS, redirect, and a 403 refusal of plain HTTP outside Development); AdminPortal refuses to start without UpdateServer:TrustedKeys outside Development; /health on license and update hosts.
- [x] Tests (+305): Security.Tests 55 (new), Users +107 (157), POS +10, Catalog +4, Inventory +5, Purchasing +3, Payments +4, CashManagement +4, Customers +3, Suppliers +3, Pricing +3, Reporting +4, Audit +9, Licensing +34 (143), Updater +14 (182),
      Cloud +20 (207), Integration +7 (48: real first-run setup, real sign-in, roles, real signed licenses incl. expiry-keeps-data, real audit log, strict composition), Architecture ARCH-SEC-001..016 (+16 = 345).
- [x] Build: 0 errors, 0 warnings (120 projects, verified with a no-incremental rebuild); all 2099 tests pass
- [x] Review (2026-10-06): desktop start screen (first-run setup / sign-in / password change / sign-out), customers.customer.view, catalog.cost.view, POS sessions attributed to the signed-in user,
      plain-words license notices, audit buffer fix, ARCH-SEC-017; decisions and the deferred-limitations register: "Stage 11 Summary - Stage 11 review"

---

### Stage 12 - Offline & Failure Testing (COMPLETE)
Scope source: the Stage 12 task text (the roadmap file is not in the repository). Goal: prove the platform stays correct and usable when things fail, with real SQLite, the real desktop composition and deterministic failure injection; fix only defects the testing exposes.
- [x] Audit first (no code changed before it): existing resilience = hardware isolation (Stage 10), optional cloud clients mapping network errors to results (Stage 6/7), startup recovery in the updater (Stage 7), per-module migrations. Existing failure tests were targeted (one network failure per client, hardware isolation, the offline POS path). The highest-risk boundary was found by reading the code, not assumed: POS checkout and Purchasing receive spanned separate module DbContexts/connections with no shared transaction (compensation + "durable progress per line"); the fix was approved before it was made.
- [x] Platform.Application/Abstractions/Data/IAtomicOperation (new) + Platform.Infrastructure/Persistence/SharedDatabaseScope (new): one connection per DI scope shared by the business module contexts (UseSharedSqlite), one real BEGIN IMMEDIATE transaction, rollback by the database on a failed Result, an exception or a lost connection; nested calls join; AtomicSaveChangesInterceptor stops EF's nested per-save transaction while an operation runs and clears a context's tracked state after a failed save or a rolled-back operation. Ten business modules register through UseSharedSqlite (Users and Audit stay independent ON PURPOSE: an audit record must survive the rollback of what it describes).
- [x] POS.Application CheckoutCartCommandHandler: steps 1-8 (cart + session read, validation, stock re-check, sale, lines, payment, stock issue, completion, cart checked out) are ONE transaction; peripherals run after the commit; compensation (cancel sale, void payment, "manual stock correction") now exists only in the fallback for hosts without IAtomicOperation (unit-test hosts). Purchasing ReceivePurchaseOrderCommandHandler: all lines + order progress commit together or not at all (resumable per-line progress remains only in the fallback).
- [x] POS.Infrastructure POSService: every action converts an unexpected failure into a plain result ("The operation could not be completed and nothing was changed...", POS.OperationFailed / POS.Checkout.NotSaved); details go to the log only. Client.Host StartupFailure + App.xaml.cs: a failed start shows a plain statement instead of the exception text. Licensing/Updater HTTP clients and LicenseService: failure messages no longer carry exception text and say "Local operation is not affected".
- [x] Platform.Infrastructure DatabaseOptions.BusyTimeoutSeconds (optional, bounded 1..600; 0 would mean "wait forever" in the driver): a locked database fails in bounded time and is testable deterministically.
- [x] Tests (129 new; see "Stage 12 Summary" for the matrix): Integration.Tests (+56) AtomicityFailureTests, OfflineWorkflowTests, DatabaseFailureTests, RestartAndRecoveryTests, HardwareFailureTests, LicenseAndSecurityFailureTests with FailureTestKit (database-level failure injection through SQLite triggers, direct database inspection), OfflineDesktop (production-like composition on a network that refuses and counts every request), SignedLicenseWorld (extracted from SecurityIntegrationTests); Licensing.Tests (+30) LicenseServerOutageTests; Updater.Tests (+27) UpdateServerOutageTests; Tests.Common/Network FaultInjectingHandler; Platform.Infrastructure.Tests (+8) SharedDatabaseScopeTests; Architecture.Tests (+8) ARCH-RES-001..008 (ResilienceBoundaryTests).
- [x] Real-executable smoke runs (Windows, isolated folders, no network configured): Client.Desktop.exe starts offline, creates the database and the protected identity file, reaches the sign-in screen and closes cleanly; with a corrupt database file the real executable shows the plain "local database could not be opened - nothing was changed or deleted" message (read through UI Automation) and the file is byte-identical afterwards.
- [x] Build: 0 errors, 0 warnings (120 projects, --no-incremental); all 2254 tests pass (22 test projects), 0 failed, 0 skipped.
- [x] Not done by design (see the Stage 12 limitations): a real process kill, a real full disk, the real OS certificate store, physical devices, UI automation of the WPF screens, load testing.

### Stage 13 - Architecture & Integration Verification (COMPLETE)
Scope source: the Stage 13 task text (the roadmap file is not in the repository). Goal: prove the architecture, module boundaries, contracts, persistence boundaries, licensing, updates, offline behavior and the main vertical slice still hold together; fix only concrete violations; make every violated rule hard to violate again.
- [x] Audit first (repository, not documents): project-reference/package map of all 120 projects, module/runtime dependencies, DbContexts and table prefixes, contracts, DI/hosting, licensing and update wiring, compared with "Architecture & Solution Design.md" (results: "Stage 13 Summary").
- [x] Significant defect stopped and reported before any change; the user chose "lifecycle host, fail fast": Client.ModuleHost ModuleLifecycleService + Platform.Application ModuleCompositionException + Client.Host StartupFailure.Modules.
- [x] Catalog.UI no longer references Client.Host (it reached Platform.Infrastructure/EF Core/SQLite; Rule 6).
- [x] Architecture.Tests: ARCH-SOL-001..016 (Solution/SolutionArchitectureRules.cs, project graph + manifests), ARCH-SOL-017..022 (Solution/OwnershipAndContractRules.cs, contracts, table ownership, shared transaction); the assembly registry now covers all 13 modules; six always-passing placeholder tests removed.
- [x] Tests: Platform.ModuleContract.Tests +9 (ModuleLifecycleServiceTests); Integration.Tests +18 (ModuleLifecycleIntegrationTests, VerticalSliceOwnershipTests, ModuleIntegrationTests, LicensingIntegrationTests, UpdateIntegrationTests, ArchitectureCompositionTests); OfflineDesktop now really isolates the updater folder (Updater:UpdateRoot).
- [x] Build: 0 errors, 0 warnings (120 projects, --no-incremental); all 2297 tests pass (22 test projects), 0 failed, 0 skipped; the 18 new real-host tests passed 5 consecutive runs.
- [x] Real Client.Desktop.exe smoke runs (isolated folders, UI Automation, console log captured): first-run setup, shell, sign-out, sign-in, close (exit 0); restart on the same files: sign-in (not first run), wrong password refused in plain words, sign-in, close (exit 0); both runs logged 13 modules running in dependency order, host stopped, no error lines. No business workflow through the UI (the shell hosts no business screens yet).

---

## Current Task

**Pre-deployment work (PRE_DEPLOYMENT_CHECKLIST.md). FIX-01 COMPLETE (a..e: shell, POS, Catalog/Inventory/Sales, Stage 8 back office, license, users, roles and permissions - every module has its screens). FIX-02 COMPLETE (barcode scanner input on the POS screen). FIX-03 COMPLETE (healthy-start confirmation of updates that really run). FIX-04 COMPLETE (POS cash sales go into the open cash drawer shift). FIX-05 COMPLETE (business actions in the audit log). FIX-06 COMPLETE (plain failure messages at every module boundary and screen). FIX-07 COMPLETE (one DI scope per user action, enforced and tested). FIX-08 COMPLETE (a: tax rates in Pricing, b: tax at the till and in Sales, c: discounts at the till). FIX-09 COMPLETE (a: part deliveries and closing short, b: supplier returns). FIX-10 COMPLETE (split payments at the till). FIX-11 COMPLETE (an optional customer on a sale). FIX-12 COMPLETE (sales report reads every sale of its range; local-day breakdown). FIX-13 COMPLETE (a: receipts with Arabic printed as a picture, b: screen language per user, c: Arabic translations of every screen, right to left). FIX-14/FIX-15 COMPLETE (this document brought up to date). Part A of the checklist is done. Part B: MISS-04 (backup) design APPROVED (MISS-04_BACKUP_DESIGN.md); MISS-04a COMPLETE (local backup core: Client.Backup). MISS-04b COMPLETE (restore across a restart, with a before-restore copy). MISS-04c COMPLETE (daily scheduled backup, catch-up, retry pause, shell notice). MISS-04d COMPLETE (the Backup screen, restart after a confirmed restore). Local backup is usable end to end from the desktop. Stage 14 has not been started.**

---

## Next Task

**Part B of PRE_DEPLOYMENT_CHECKLIST.md (MISS-04..MISS-07; MISS-01..03 are covered by the completed Part A items). MISS-04 (client backup): design approved 2026-10-10, MISS-04a..04d done (local backup complete and usable from the desktop); next MISS-04e (optional CloudBackup module: encryption, recovery code, escrow slot, HTTP client, outbox), then 04f (vendor escrow tool) (MISS-04_BACKUP_DESIGN.md section 11). Work proceeds one checklist item at a time.**
The desktop is a complete application: first-run setup, sign-in, password change and sign-out (Stage 11 review); license activation; every module has its screens in the shell (FIX-01), in English or Arabic per user (FIX-13); a fresh installation can be set up and sell through the desktop alone (activate, categories/units, products, warehouse, receive stock, sell with tax, discounts, split payments and a scanner, print a receipt, cash drawer shift, sales history and reports), verified end to end in the real executable against a real local LicenseServer.Api.

Next roadmap stage: Stage 14 (Packaging / deployment) - only when instructed. Stage 13 follow-ups: "Stage 13 Summary - Remaining limitations". Follow-ups that are NOT part of any completed stage: a launcher that starts the ACTIVE core version and ModuleHost loading modules from the active deployment directories (so activated updates take effect at runtime); IModuleMigrator implementations in the business modules; CLI wrappers for ModulePackager/UpdatePublisher; the client-side CloudBackup module (optional module that talks to BackupServer.Api through an IBackupClient; it must declare and enforce backup.create / backup.restore / backup.delete); a browser UI for AdminPortal; stock-reversal contract; physical-device adapters (Windows spooler, serial/USB scales, vendor SDKs); adoption of Audit / CashManagement / Customers by POS and Sales (see "Stage 8 limitations" and "Stage 9 limitations").

---

## Solution / Project Structure (Current State)

GenericPOS.sln (120 projects)

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
|   +-- Client.Backup              [DONE] - MISS-04a: local backup (SQLite online backup copy, folder destination, verify, history/settings
|                                           next to the database, backup.* capabilities). No HTTP/EF/WPF/crypto/business modules.
|                                           MISS-04b: RestoreService + PendingRestoreStep (Client.Host IStartupPreparation, before the DB opens).
|                                           MISS-04c: BackupSchedule/ScheduledBackupRunner + BackupScheduler (hosted), IBackupNoticeSource.
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
        +-- Users                  [DONE] - usr_*   User, Role, permission codes; IUserLookup, IUserPermissionChecker (Stage 11 added usr_UserCredentials, sign-in/lockout/password handlers and the platform IPermissionProvider)
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
src/Client/
+-- Client.Hardware                [DONE] - Stage 10: hardware adapters (ESC/POS receipt printer + cash drawer, ZPL label printer, keyboard-wedge scanner, TCP/device-path transports, Null placeholders), HardwareFactory, HardwareHostingModule
tests/Hardware.Tests               [DONE] - 97 tests, all passing (Stage 10)
+-- Client.Security                [DONE] - Stage 11: DPAPI ISecretProtector (current user, purpose-bound) + ClientSecurityHostingModule (registered only on Windows)
+-- Cloud.Hosting                  [DONE] - Stage 11: shared server ASP.NET plumbing (security headers, transport security, caller key, 429 answer); no EF, no persistence
tests/Security.Tests               [DONE] - 55 tests, all passing (Stage 11: authorization/session/catalog/events foundation, license gate, DPAPI)
src/Platform/Platform.Application/Abstractions/Hardware [DONE] - Stage 10: IBarcodeScanner, IReceiptPrinter, ILabelPrinter, ICashDrawer, IScale + DTOs, HardwareErrors, HardwareGuard
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

STAGE 10 HARDWARE:
Platform.Application -> (Abstractions/Hardware namespace; no new reference)
Client.Hardware -> Platform.Core, Platform.Application, Client.Host (IHostingModule only) + Hosting.Abstractions / Configuration.Binder / Logging.Abstractions
                   (no WPF, EF, HTTP, business module, server code, vendor SDK, System.IO.Ports)
POS.Infrastructure -> also Platform.Application explicitly (hardware abstractions); POS.Application already referenced it
Client.Desktop -> also Client.Hardware (the ONLY project that references the adapters) and Client.Security (the ONLY project that references the DPAPI protector)
Client.Security -> Platform.Core, Platform.Application, Client.Host (IHostingModule only) + System.Security.Cryptography.ProtectedData
Cloud.Hosting -> Cloud.Contracts + the ASP.NET Core shared framework; referenced by the four server API hosts; Cloud.Infrastructure stays free of ASP.NET (ARCH-CLD-005)
Every business module's Application project -> Platform.Application (authorization); the module Infrastructure projects register the module's ICapabilityProvider
Tests.Common -> also Platform.Core, Platform.Application (fake hardware); Hardware.Tests -> Client.Hardware, Platform.*, Tests.Common; POS.Tests/Integration.Tests -> also Tests.Common (+ Client.Hardware for Integration.Tests)

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
ARCH-HW-001 through ARCH-HW-014: ACTIVE and passing (activated with Stage 10): the five abstractions are Platform.Application interfaces extending IHardwareDevice; Platform knows no device technology; business modules never touch
  System.IO.Ports/Sockets/Windows/device APIs nor the adapters; only Client.Desktop references Client.Hardware (assemblies AND project files); Domain assemblies never use the hardware namespace; only Platform.Application, Client.Hardware,
  POS.Application and POS.Infrastructure may use it; Client.Hardware has no WPF/EF/ASP.NET/HTTP/business/server dependency and only the three approved Microsoft.Extensions packages; UI sources never mention hardware types; adapters are constructed
  only by HardwareFactory; hardware configuration is its own section and no internet address is hard-coded; every POS device dependency is a nullable optional parameter.
Stage 10 test totals: Hardware.Tests 97 (new), POS.Tests 138 (95 + 43), Architecture.Tests 329 (315 + 14), Integration.Tests 41 (34 + 7). Grand total after Stage 10: 1794 tests, 0 failures.
ARCH-SEC-001 through ARCH-SEC-016: ACTIVE and passing (activated with Stage 11, SecurityBoundaryTests): the Platform.Application security abstractions depend on no technology; Domain assemblies know nothing of sessions/claims/licensing; no business module references a concrete
  security implementation (Client.Security, licensing, updater, desktop, signing, DPAPI, server code) nor project-references a client; license enforcement stays centralized (no module touches ILicenseEntitlementService); EVERY business command handler takes IAuthorizationService unless
  it is on the documented exception list (and that list must not rot), as do the user/audit/report read handlers; every capability provider builds a valid unique catalog and is registered by its module; contracts and DTOs never expose passwords, hashes, tokens or keys; Client.Security is
  only data protection; Client.Hardware has no security dependency; server projects never reference Client.Security or Platform.Application and Cloud.Hosting has no persistence; no private key or key file anywhere in src/tools; no hard-coded credential in sources or production
  configuration; no configuration switch turns security off; the desktop composition root registers the security modules.
Stage 11 test totals: Security.Tests 55 (new), Users.Tests 157 (50 + 107), Licensing.Tests 143 (109 + 34), Updater.Tests 182 (168 + 14), Cloud.Tests 207 (187 + 20), Architecture.Tests 345 (329 + 16), Integration.Tests 48 (41 + 7), POS.Tests 148, Audit.Tests 35, plus +3..+5 in each other module.
  Grand total after Stage 11: 2099 tests, 0 failures; after the Stage 11 review: 2125 (ARCH-SEC-017 added, Architecture.Tests 346).

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

## Packages Added in Stage 11

Project | Package | Version | Reason
--------|---------|---------|-------
Client.Security | System.Security.Cryptography.ProtectedData | 10.0.11 | Windows DPAPI. Needed: DPAPI is not in the shared framework, the package is the .NET team's own and maintained for net10.0; the alternatives (hand-written P/Invoke, or a home-made cipher with its key next to the data) are worse.
(No other package. Password hashing uses Rfc2898DeriveBytes.Pbkdf2 from the base library; rate limiting and HTTPS enforcement use ASP.NET Core's own middleware; no authentication/identity framework was added.)

---

## Packages Added in Stage 10

Project | Package | Version | Reason
--------|---------|---------|-------
Client.Hardware | Microsoft.Extensions.Hosting.Abstractions, Configuration.Binder, Logging.Abstractions | 10.0.11 | IHostingModule/DI, "Hardware" configuration binding, adapter logging (same packages already used by Client.Licensing / Client.Updater)
Hardware.Tests | Microsoft.Extensions.Configuration, DependencyInjection, Hosting | 10.0.11 | Configuration/DI/hosting-module tests
(No third-party, vendor or serial-port packages: ESC/POS and ZPL are written directly and talk through plain sockets or device paths.)

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
10    | Hardware & Device Integration                         | COMPLETE (abstractions + ESC/POS, ZPL, keyboard-wedge adapters; no physical device verified)
11    | Security Hardening                                    | COMPLETE (authentication incl. desktop start screen, capability authorization, license enforcement, data protection, audit, server hardening; reviewed 2026-10-06)
12    | Offline & Failure Testing                             | COMPLETE (atomic cross-module operations via IAtomicOperation; offline, cloud-outage, database, hardware and restart failure campaigns; see "Stage 12 Summary")
13    | Architecture & Integration Verification               | COMPLETE (repository audit; the twelve dependency rules enforced on every project; fail-fast module lifecycle; see "Stage 13 Summary")
-     | Pre-deployment fixes (PRE_DEPLOYMENT_CHECKLIST.md)    | Part A COMPLETE (FIX-01..FIX-15); Part B in progress
14    | Packaging & Deployment                                | NOT STARTED (Part C of PRE_DEPLOYMENT_CHECKLIST.md)

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
2. **No distributed transactions.** Purchasing receives a purchase order line by line through `IStockReceiptService` (new, minimal Inventory contract delegating to AddStock), saving after every line; it is resumable and idempotent per line, and a partial receipt leaves the order Submitted with the already-received lines marked (cancelling it is then refused), instead of pretending to be atomic. **[Superseded in Stage 12: checkout and purchase receive now run as ONE SQLite transaction (IAtomicOperation); see Stage 12 Summary.]**
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

## Stage 10 Summary

### Where things live (and why)
1. **Abstractions in Platform.Application/Abstractions/Hardware.** The architecture says "the POS depends on interfaces" and platform projects may not depend on modules, so the contracts sit where the existing licensing
   abstraction (`ILicenseEntitlementService`) sits: a neutral Platform layer every module already references. They speak business vocabulary only (scan, print a receipt, print a label, open the drawer, read a weight) and use
   the existing `Result`/`Error` pattern. No new project was needed for the contracts.
2. **Adapters in a new client component, `Client.Hardware`.** The roadmap and Generic Platform section 35 ask for "independent components" and Architecture section 8 names "hardware infrastructure"; the existing precedent for
   optional infrastructure is the Stage 6/7 client components (Client.Licensing, Client.Updater) with an `IHostingModule`. Client.Hardware follows it: plain net10.0, referenced only by the desktop composition root, no WPF/EF/HTTP.
   It was NOT put into Platform.Infrastructure because that project owns the SQLite persistence that every module references; hardware must stay optional and removable.
3. **POS is the only business consumer.** POS.Application (handlers) and POS.Infrastructure (IPOSDevices, IPOSBarcodeInput) take the abstractions as NULLABLE constructor parameters (the same soft-dependency pattern as Pricing and Payments).
   Adding another consumer (for example label printing from Catalog) is a deliberate architecture decision: ARCH-HW-007 lists the approved assemblies and must be edited in the same change.

### Behavior rules
- **A completed sale is final.** Checkout saves the sale (cart marked checked out, SaleId stored) and only THEN attempts the receipt and, for a cash payment, the drawer. A failure becomes a `POSHardwareNotice` ("The sale was completed and saved, but
  the receipt could not be printed: ...") on the successful result and a warning in the log; it never throws, never rolls back and never changes sale, stock or payment. Peripheral calls deliberately ignore the caller's cancellation token
  (the sale is already done; each device call is bounded by its own timeout). A device that is NOT CONFIGURED is skipped silently (the normal case); misconfigured or broken devices produce a notice.
- **Nothing a device does can escape as an exception.** Adapters return failed Results; every POS call is additionally wrapped in `HardwareGuard`, which converts exceptions (a misbehaving third-party driver) into `Hardware.Failed`.
- **Scanner = input only.** `POSBarcodeInput` feeds scans to the bound cart through the SAME `IPOSService.AddProductAsync` a typed code uses, one at a time; unknown barcodes, no open cart, or exceptions become rejected outcomes (`POS.Scan.*`),
  and a faulty outcome subscriber cannot stop the others. A scanner that cannot start returns a failed result and the cashier keeps typing codes.
- **Scale.** Readings are validated by POS: negative, above 1000 kg or unknown-unit readings are rejected as `Hardware.InvalidData`; unstable readings are returned flagged `IsStable=false`. POS offers `ReadWeightAsync` only: it does NOT convert a weight into a
  cart quantity (a product's unit of measure is a business decision not made here).
- **Labels** encode the product SKU (the POS resolves a scanned SKU exactly like a barcode); the receipt can be reprinted for any checked-out cart (`PrintReceiptAsync(cartId)`, without payment details because the cart does not store them).
- **Optional by construction.** No hardware module, "None" configuration, an unknown Type or an incomplete connection all leave the POS fully working; status shows NotConfigured/Unavailable with a reason, and creating a device never touches the network.

### Adapters (what was actually implemented)
Adapter | Protocol / mechanism | Verified how
--------|----------------------|--------------
KeyboardWedgeBarcodeScanner | fast keystrokes ended by Enter (min length, max gap configurable); UI forwards characters to `IKeyboardInputSink` | unit tests with a manual clock
EscPosReceiptPrinter | ESC/POS (init, align, bold, text, partial cut), ASCII only ('?' for other characters), width configurable | exact bytes + real device-path and loopback TCP transports
EscPosCashDrawer | ESC p kick pulse, through the receipt printer's connection (`ViaReceiptPrinter`) or its own | exact bytes via a device-path file
ZplLabelPrinter | ZPL II (name, Code 128 barcode, price, copies); command characters in data neutralised; code must be printable ASCII | exact bytes
TcpDeviceTransport / FileDeviceTransport | raw TCP (LAN) / device path (UNC share, device node) with timeouts; failures are results | loopback listener, temp files, closed ports, bad hosts/paths
Null* devices | "not configured" / "unavailable: reason" placeholders | tests
IScale | NO adapter: no generic scale protocol exists (serial/USB protocols are vendor-specific) | fake + placeholder only
NO physical printer, drawer, scanner or scale was available; correctness against real devices (code pages, cutters, firmware quirks) is unverified.

### Configuration (separate from business configuration; no secrets)
`Hardware:Scanner:Type` None|KeyboardWedge (+MinimumLength, MaxInterCharacterMilliseconds); `Hardware:ReceiptPrinter:Type` None|EscPosTcp|EscPosFile (+Host, Port=9100, Path, TimeoutMilliseconds, CharactersPerLine=42, CutPaper);
`Hardware:LabelPrinter:Type` None|ZplTcp|ZplFile (+connection, WidthDots, HeightDots); `Hardware:CashDrawer:Type` None|ViaReceiptPrinter|EscPosTcp|EscPosFile (+Pin, OnTimeMilliseconds, OffTimeMilliseconds);
`Hardware:Scale:Type` None. Business receipt text/behavior is a different section: `PosReceipt` (StoreName, HeaderLines, FooterLines, AutoPrintReceipt, AutoOpenDrawerOnCashSale). Environment overrides use the existing `GENERICPOS_` prefix
(for example `GENERICPOS_Hardware__ReceiptPrinter__Type`).

### Stage 10 limitations and deferred work
- No physical-device verification (see above). Not implemented: Windows spooler/driver printing (needs Windows-specific APIs and belongs in its own adapter), serial/USB/Bluetooth transports and scales, vendor SDK adapters, non-ASCII receipt text
  (code pages; Arabic and other scripts print as '?'), barcode/logo graphics on receipts, customer displays and POS terminals (named in the documents as potential integrations, no stage requires them yet).
- Nothing feeds `IKeyboardInputSink` yet: PosView is still not hosted in MainWindow (an earlier limitation), so a configured keyboard-wedge scanner has no key source until the UI hosting exists. `IPOSBarcodeInput` is not yet bound to the view model. **[Superseded in FIX-02: the shell window forwards key presses and the POS screen listens to IPOSBarcodeInput; see "FIX-02 Summary".]**
- `FileDeviceTransport` opens an EXISTING path and writes from the start; it never creates the path. Two writers to the same ordinary FILE overwrite each other (devices are streams, so this only matters in tests).
- The receipt of a reprint carries no payment lines; "no sale" drawer opens are not permission-checked (authorization is Stage 11).
- The Stage 12 failure campaign (unplug during a sale, crash during printing, hardware failure) was done in Stage 12 (see "Stage 12 Summary"); under LOAD (many sales per second) it was not.

---

## Stage 11 Summary

### Scope decisions (read before changing anything)
1. **Repository vs the task text.** `POS-Platform-—-Implementation-Roadmap.txt` is not in the repository; the scope came from the three architecture documents, this file and the task. Capability codes follow the EXISTING Users convention
   (lower-case dotted, `pos.sale.create`), not the `POS.Sale.Create` casing of the task's examples. Operations that do not exist yet got no capability: there is no refund, return or void-sale handler (add `pos.refund`/`pos.sale.void` WITH the operation),
   no client backup module (`backup.create/restore/delete` belong to it). (Cost visibility was decided in the 2026-10-06 review: `catalog.cost.view`.)
2. **Authentication lives in Users, authorization in the platform.** Users owns credentials (additive table usr_UserCredentials) and implements the platform's IPermissionProvider; the platform owns "who is signed in" (SessionContext, identity only) and "may they do this"
   (AuthorizationService). Modules know only Platform.Application. No ASP.NET Identity, no claims, no new framework.
3. **Enforcement is in the handlers**, before anything happens, so a screen, a test or a script cannot bypass it. The answer is computed live on every call (roles, deactivation and permission changes apply to the very next call); it fails closed (nobody signed in,
   unknown capability, no permission source, licensing refused). `ISessionManager.SignIn` carries no permissions on purpose.
4. **Trusted module-to-module calls are not authorized a second time.** A contract call (POS checkout -> Sales/Inventory/Payments, Purchasing receive -> Inventory) runs inside an operation the user was already authorized for; making a cashier hold `payments.payment.record`
   AND `inventory.stock.issue` AND `pos.sale.create` would leak internals into roles. Handlers with two callers split into HandleAsync (authorizes) and an internal ExecuteAsync (InternalsVisibleTo the module's own Infrastructure only). ARCH-SEC-006 keeps the exception list honest.
5. **License enforcement is centralized and cannot be turned off by configuration.** One gate in AuthorizationService, driven by the capability declaration (LicenseRequirement.Module or None). No `Licensing:Enforcement`-style key exists (ARCH-SEC-015). A host without any licensing component
   (tests) has nothing to enforce; the desktop always registers it (ARCH-SEC-016). Developers run licensed through the existing Development dev-license flow of the license server.
6. **A licence never touches data.** A refusal only declines to START licensed work; reading and exporting your own data (reporting.view, audit.view), user and license administration and updates stay available in every state (proved on the real host: an expired license
   stops new sales, every record stays readable and intact across a restart, a renewal restores selling).
7. **Clock rollback** (Stage 6 limitation): persisted protected high-water mark floored by the signed IssuedAt of the stored license + the monotonic stopwatch within a run. A clock that cannot be believed makes the license Invalid(ClockRollback). Tolerance default 120 min, bounded 5 min..7 days,
   no off switch. NOT a secure time source: someone who can run code as the user can in principle defeat it, and each restart can win back at most one tolerance. The one remaining false positive (clock set far AHEAD across a restart, then corrected) is cleared by the next
   successful online renewal, which re-bases the mark on server-signed time. A product decision to confirm: the default restricts licensed work (data untouched) instead of only warning, because a warning-only policy defeats the purpose.
8. **Installation identity** stays a random GUID (activation protocol unchanged) but is stored protected for the current user and machine; an unreadable or plain-text identity is never trusted, never silently overwritten (kept as evidence, audited, regenerated = binding broken = nothing granted).
   Honest limit: the GUID is not secret, so DPAPI stops copying the file, not someone who runs code on the second PC. Real device binding (an installation key pair whose public half the license binds to, proven at renewal) is a protocol change and is deferred. **Compatibility
   decision to confirm:** pre-Stage-11 installs have a plain `installation.json`; it is refused by default (accepting plain text would keep the copy attack open) and sealed once with `Licensing:AllowLegacyPlaintextIdentity=true`, or the installation is re-activated
   (AdminPortal release-installation). No production install exists yet.
9. **Updates:** the Stage 7 pipeline (signature before manifest, per-file + payload hash, version/compat, side by side + atomic pointer, rollback) already met the requirements and is unchanged; Stage 11 adds audit events and `updates.manage`. Permission never replaces verification.
10. **Cloud:** no wire contract changed. Throttling is per caller address in memory (guessing protection, not DDoS protection; behind a reverse proxy the real client address needs forwarded headers - Stage 14). Plain HTTP is refused outside Development even where a redirect cannot be built.
11. **Secrets:** nothing secret is stored in clear anywhere new: passwords only as PBKDF2 hashes, admin keys / backup tokens / activation keys unchanged (SHA-256 only), audit text is sanitised (SecretRedactor) and a typed username is only recorded when it is a real user
    (someone may have typed a password into the name box). No private key exists in client sources (ARCH-SEC-013); no hard-coded credential (ARCH-SEC-014).

### Capabilities (33 after the review; * = sensitive; all `LicenseRequirement.Module` unless marked None)
users.view (None), users.manage* (None) | pos.session.manage, pos.sale.create*, pos.receipt.reprint, pos.drawer.open*, pos.label.print | catalog.product.create / .edit / .deactivate, catalog.category.manage, catalog.unit.manage, catalog.cost.view* (None; redacts the field) |
inventory.stock.receive*, inventory.stock.adjust*, inventory.location.manage | purchasing.order.create / .submit / .cancel, purchasing.order.receive* | payments.payment.record*, payments.payment.void* | cash.session.manage*, cash.movement.record* (module cash-management) |
customers.customer.manage, customers.customer.view* (None) | suppliers.supplier.manage | pricing.pricelist.manage*, pricing.price.manage* | reporting.view (None) | audit.view* (None) | licensing.manage* (None) | updates.manage* (None).
Module IDs for entitlements are the manifest IDs (catalog, inventory, sales, pos, customers, suppliers, purchasing, pricing, payments, users, audit, cash-management, reporting). Not protected by a capability by design: Sales workflow handlers and IssueStock (only reachable through contracts inside POS checkout),
RecordAuditEntry (modules record on their own account), SignIn/SignOut/ChangePassword/BootstrapAdministrator, GetUserPermissions (it IS the permission source), and plain master-data READS of catalog, inventory, customers, suppliers, pricing, payments, cash and POS carts (see limitations).

### First run and sessions
`BootstrapAdministratorCommandHandler` creates the first user, an "Administrator" role holding every capability the installed modules declare at that moment, and the password; it works only while NO user exists and is audited (also when refused). It signs nobody in. After setup the
administrator signs in (`SignInCommandHandler`) and creates other users (`CreateUserCommand` + `SetUserPasswordCommand`, temporary passwords force a change: `ChangePasswordCommandHandler`, which re-proves the current password under the same lockout). Unknown user, user without a password and wrong
password give the same answer and cost the same hashing work; a disabled user is only named to someone who knows the password; oversized input is refused before hashing. Lockout: 5 failed attempts -> 15 minutes, per account, raise-only configuration.
New capabilities of modules installed LATER are not added to the Administrator role automatically (an administrator grants them): automatic escalation was rejected.

### Configuration reference (all optional; none can weaken a rule below its floor; no secret)
Key | Meaning (default; floor / bound)
----|---------------------------------
Security:Passwords:MinimumLength / MaximumLength | password length (10 / 128; at least 8, at most 128)
Security:Lockout:MaxFailedAttempts / LockoutMinutes | sign-in lockout (5 / 15; at least 3 attempts, positive duration)
Security:PasswordHashing:Iterations | PBKDF2 cost (600000; at least 100000)
Licensing:ClockToleranceMinutes | clock rollback tolerance (120; 5 minutes .. 7 days)
Licensing:AllowLegacyPlaintextIdentity | accept and seal a pre-Stage-11 plain installation.json once (false)
Security:AuthThrottle:MaxFailures / WindowMinutes / LockoutMinutes | server credential-guessing throttle (10 / 10 / 15; at least 3 failures)
UpdateServer:TrustedKeys | now REQUIRED by AdminPortal.Api outside Development (public keys only)
No setting enables/disables authorization, authentication or license enforcement (ARCH-SEC-015).

### Security events written to the audit log (module "security")
security.signin.succeeded|failed|locked|blocked, security.signout, security.password.changed|reset, security.bootstrap.administrator-created|refused, security.authorization.denied (capability, user; includes license refusals),
security.user.created|deactivated|reactivated, security.role.created|assigned|removed, security.permission.granted|revoked, security.license.loaded|activated|renewed|rejected|activation-failed|clock-rollback|clock-state-unusable,
security.installation.identity-unusable|sealed, security.update.accepted|rejected|failed|rolledback. Events carry IDs, codes and short reasons only (never passwords, hashes, keys, tokens, license content or package content; tested). Recording is best effort and never
blocks the audited operation; events raised before the audit tables exist wait in a bounded buffer and are written in order after the audit migration. Server side, the shared adm_AuditLog also holds backup.upload|download|delete|upload-denied (actor license:<id>) and admin|backup|license auth-failed|auth-blocked (actor anonymous).

### Stage 11 architectural decisions (short)
1. ICurrentUser lost HasPermission/HasFeature (never used; a cached permission snapshot contradicts "live"); permissions are asked of IAuthorizationService only.
2. Reads that return plain DTOs (Users, Audit, Reporting - the protected ones) now return Result<T> so a refusal is expressible; Users.UI and Audit.UI show the reason.
3. TestModuleDatabase and the per-module test databases register a permissive IAuthorizationService by default so business tests stay about business; security tests register the real or a scripted one. Integration hosts WITH Users do the real first-run setup and sign in;
   hosts without Users (no permission source) use a test-only permissive stand-in because the real service correctly fails closed.
4. Client.Security is a separate project (the precedent is Client.Hardware): DPAPI is Windows- and package-specific and must stay replaceable; consumers fall back to plain files and never invent a cipher when no protector is registered.
5. Cloud.Hosting exists because Cloud.Infrastructure must stay free of ASP.NET (ARCH-CLD-005) and four hosts needed the same middleware.
6. EF migration tooling: `dotnet ef` 9.0.3 (installed tool) generated the Users migration against EF Core 10.0.11 with `-s src/Client/Client.Desktop`; it works but warns that the tool is older than the runtime.

### Stage 11 review (2026-10-06): decisions on the remaining gaps
The Stage 11 implementation was reviewed against the roadmap (authentication, authorization, capability-based permissions, password/security policies,
data protection, license protection, package signature verification, update verification, secure cloud communication, backup security, installation
identity, audit trail, tamper resistance). Every area is implemented and tested; the open questions were decided as follows (each correction is its own
commit, listed in the commit history below).

1. **Desktop authentication / first-run flow - IMPLEMENTED.** "Authentication must work offline" is not met by handlers alone if the product has no way to
   sign in. Decision: the desktop shows a start screen before the shell (`SignInWindow`): first-run setup when no user exists (create the first administrator,
   then sign in), sign-in otherwise, and the forced change of a temporary password; closing it exits; the shell shows who is signed in, the license state in
   plain words (`LicenseNotice`) and offers sign-out. The window is glue over the unit-tested `Users.Application.Security.InteractiveSignInService`; all
   rules stay in the handlers (ARCH-SEC-017 enforces both the gate and the "glue only" shape). Verified by tests of the flow and a startup smoke run of the real
   executable on isolated folders (host starts, start screen reached, no error lines, identity file stored DPAPI-protected, early license event in the audit log).
   The WPF screens themselves have no automated UI test (the net10.0-windows TFM gap shared by every UI project). NOT included, by decision: the user/role
   administration screens and the license-activation screen - they belong with hosting the module screens in the shell (the handlers they need -
   Create/Set password/Assign role/Grant, ActivateLicense - exist and are authorized). Until then a newly installed desktop can set up its administrator and
   sign in, but activating the license and creating further users need those screens.
2. **Read authorization boundaries - DECIDED, two gaps closed.** Policy: reads are refused only where the data is personal, administrative or historical/financial
   evidence; operational reads cashiers need stay open to the signed-in operator; commercially sensitive FIELDS are redacted rather than refusing the read.
   | Data | Rule | Capability |
   |---|---|---|
   | Users and roles | refused without | users.view |
   | Audit trail | refused without | audit.view |
   | Reports | refused without | reporting.view |
   | Customers (personal data) | refused without (NEW) | customers.customer.view |
   | Product cost price | field omitted without (NEW) | catalog.cost.view |
   | Catalog, stock levels, warehouses, prices, POS carts/sessions, device status | open to the operator | - |
   | Suppliers, purchase orders (incl. unit cost), payments, cash sessions | open to the operator (accepted; gate with `<module>.view` when those screens are hosted if the business requires it) | - |
   Module contracts (`ICustomerLookup`, `IProductLookup`, ...) are trusted calls inside already-authorized operations and are not gated (decision 4 above).
   With the start screen in place, every read the desktop UI performs happens for a signed-in user.
3. **Clock rollback - DECIDED: restrict, keep the current behaviour.** A clock that cannot be believed makes the license Invalid(ClockRollback): licensed work is
   declined, data is untouched, reading/exporting/administration stay available. Alternatives rejected: warn-only (turning the clock back would then stretch an
   expired license indefinitely, defeating license protection), lock the application (destroys access to data, forbidden by the architecture). False positives are
   bounded: tolerance 120 minutes by default (configurable only within 5 minutes..7 days), a forward jump during a run does not poison the mark, and the user is now
   told exactly what to do (`LicenseNotice`: correct the date and time, or renew online - a renewal re-bases the mark on server-signed time). Accepted residual risk:
   not a secure time source; at most one tolerance can be won back per restart.
4. **Pre-Stage-11 plain installation identity - DECIDED: refused by default.** No production installation exists; accepting plain text forever would keep the
   copy-to-another-PC bypass open. Migration path: `Licensing:AllowLegacyPlaintextIdentity=true` for one start (the identity is sealed and audited), or re-activation
   (AdminPortal release-installation).
5. **POS session attribution - FIXED.** With a signed-in user, a till session is attributed to that user; the supplied cashier name is used only by hosts without authentication.
6. **New capabilities and existing Administrator roles - DECIDED: no automatic escalation.** The first administrator receives every capability declared at first-run
   setup. Capabilities added later (by a later module or version, e.g. customers.customer.view and catalog.cost.view added in this review) are granted by an administrator
   (`users.manage`, `GrantPermission`); automatically re-granting at start-up would silently undo deliberate revocations. Before production (Stage 15) the upgrade notes
   must list new capabilities.

### Deferred security limitations register (accepted for Stage 11, with the stage that owns each)
| Limitation | Decision | Owner |
|---|---|---|
| User/role administration and license-activation screens; hosting module screens in the shell | Deferred: handlers exist and are authorized | UI hosting follow-up (before Stage 15) |
| Backup encryption and key management; client CloudBackup module (backup.create/restore/delete) | Deferred: needs a key-management design (who holds the key, recovery, rotation) that must not be improvised; server side is isolated, revocable, audited | Backup follow-up module (before Stage 15) |
| Trusted public keys in editable configuration | Accepted: tamper-proofing needs keys inside a signed binary | Stage 14 (Authenticode signing, installer) |
| Device binding of the installation (key pair proven at renewal) | Deferred: protocol change; cloning is bounded by one installation per license and the lease | Licensing follow-up |
| Clock guard is not a secure time source | Accepted residual risk (decision 3) | - |
| Throttling per host, in memory, per caller address; forwarded headers behind a proxy | Accepted for now | Stage 14 (deployment, TLS termination, forwarded headers) |
| Admin authentication by static API keys (no per-admin roles, rotation tooling, 2FA) | Accepted for the vendor-internal host | Stage 15 / vendor portal follow-up |
| License `renew` endpoint unauthenticated | Accepted: it can only return what the server signs for that installation | - |
| Trusted module-to-module contract calls are not re-authorized | Accepted by design (decision 4); kept small by the Contracts-only rules | - |
| Operational master-data reads open to the operator | Accepted (review decision 2) | Revisit when screens are hosted |
| Runtime adoption of activated updates (launcher/ModuleHost) | Deferred since Stage 7 | Stage 14 |
| Automatic background license renewal | Deferred: user path (RenewLicense) and system path (ILicenseService.RenewAsync) exist | Licensing follow-up |
| WPF screens have no automated UI tests | Accepted (TFM gap); flow logic is unit-tested, startup smoke-run | Stage 13 verification campaign |
| Failure campaign | DONE in Stage 12 | - |
| Verification campaign, packaging, production readiness | Not Stage 11 | Stages 13-15 |

### Stage 11 commit history (all on master, each built and tested before committing)
51ada3d foundation (authorization, session, security events) | d420ba7 offline authentication in Users | b6cffe2 Users enforcement | ad0f628 POS enforcement | 87e0e7d Catalog + Inventory enforcement | 634363b Purchasing + Payments + CashManagement |
aa60eb4 Customers + Suppliers + Pricing | f4bc964 audit.view + reporting.view | b2ef0a1 central license enforcement + license events | ef2290b protected identity + clock rollback | 0e42ac9 update audit + updates.manage | ddc4d8b audit listener |
dc41c40 cloud throttling, security log, HTTPS | 87cdaba audit of user/role/permission changes | ed190b1 ARCH-SEC-001..016 | 95cf415 strict composition test | 4942ff5 configuration surface | 6c45d5a docs
Review corrections (2026-10-06): 6c20057 POS sessions attributed to the signed-in user | 01a0d9d customers.customer.view | 0da108e catalog.cost.view | 30277cd InteractiveSignInService |
cad0eee LicenseNotice (plain-words license state incl. clock rollback) | 2aeded0 desktop start screen + sign-out | 5e9eded audit buffer waits for the audit store | 3fa9bb4 capability files whitespace |
8df863d ARCH-SEC-017 | (review docs commit)

## Stage 12 Summary

### The operational rule (read before changing anything)
**Offline business operations must remain functional without cloud connectivity unless the specific operation inherently requires an online service.** Selling, purchasing, stock work, cash sessions, sign-in, license EVALUATION and reading your own data never touch the network; the only operations that need the cloud are activating or renewing a license and checking for / downloading an update, and they fail with a plain message ("... could not be reached. Local operation is not affected") while everything else keeps working. Ordering inside an operation: database work first, in one transaction; devices and network only after the commit (they cannot be rolled back and must never undo a sale).

### Defects found by Stage 12 and fixed (each has a test that failed before the fix or exercises it)
| # | Defect | Fix | Evidence |
|---|---|---|---|
| 1 | Checkout and purchase receive were not atomic: separate contexts/connections, no shared transaction. A failed step left a Confirmed sale or part of a purchase; a failed stock issue after earlier lines could not be undone; a retry after a crash could duplicate the sale and the stock deduction | IAtomicOperation / SharedDatabaseScope (one real SQLite transaction across the module contexts) | 12 AtomicityFailureTests: all fail against the old code, all pass with the fix |
| 2 | A double submit sold a cart twice: the cart was read before the transaction, so two simultaneous checkouts both saw it open | the transaction starts first (BEGIN IMMEDIATE serialises writers) and the cart is read inside it | TwoCheckoutsOfTheSameCart... failed 3 of 3 on the old order, passes 5 of 5 now |
| 3 | After a failed save the failed entities stayed tracked: the next call in the same scope worked on a change that never reached the database (a cart edited in memory, then checked out empty) | failed saves and rolled-back operations clear the tracked state | EveryPosAction_ReportsAPlainFailure... |
| 4 | An unexpected database failure reached the cashier as a raw exception (database wording, table names) | POSService returns a plain "nothing was changed" result; details only in the log | DatabaseFailureTests, AtomicityFailureTests |
| 5 | A failed start-up showed the raw exception text (database wording, file paths) | StartupFailure: plain statement; real executable verified | DatabaseFailureTests + executable smoke run |
| 6 | Cloud failure messages carried exception text (host names, OS wording) and did not say local work continues | fixed wording in LicenseService and both HTTP clients | outage tests, OfflineWorkflowTests |
| 7 | A locked database could only be waited on for the driver default (30 s) and could not be tested deterministically | optional Database:BusyTimeoutSeconds (1..600) | DatabaseFailureTests |

### Decisions introduced
1. **One transaction per business operation, owned by the platform.** Modules keep separate DbContexts and never reference each other; they take part in the transaction only by registering their context through `UseSharedSqlite` (ARCH-RES-003) and orchestrators run through `IAtomicOperation` (ARCH-RES-004: any handler that writes through the Inventory, Sales or Payments contracts must take it). Hosts that register no atomic operation keep the older step-by-step behavior, so unit-test hosts are unchanged.
2. **Users and Audit are deliberately outside the business transaction** (own connection): audit records and sign-in/lockout state must survive the rollback of the operation they describe. Consequence: nothing inside a business transaction may write to them (nothing does; a write would wait for the lock). A new persistent module must be classified in ARCH-RES-003.
3. **One DI scope per user action.** A failed operation clears what the contexts track, but a context that mutated an aggregate and failed BEFORE saving keeps that in-memory change; the rule that keeps this safe is the one the codebase already follows (POSBarcodeInput, tests): a scope per action. Hosting the screens (UI follow-up) must keep it.
4. **Failure injection is test-only and database-level where possible**: SQLite triggers that RAISE on a chosen table (the real EF code, the real transaction and the real engine fail at the chosen write), a decorator that fails or kills at commit (`InterruptCommit`), a fault-injecting HTTP handler, the existing fake hardware. No production switch exists (ARCH-RES-007 forbids failure simulation and Random in src).
5. **Unexpected failures are translated at the service boundary** for POS (the only workflow with a hosted-ready service); other handlers still surface exceptions on database failure and will be translated where their screens are hosted.
6. Stage 11 was not reopened; its decisions were re-verified under failure (clock rollback, unreadable identity, tampered license, rejected update).

### Failure matrix (what was simulated, where, result)
| Category | Scenarios | Where | Result |
|---|---|---|---|
| Network | no network, DNS failure, connection refused (also a real closed port), connection timeout, request timeout (also one real HttpClient timeout), 500, 503, malformed body, empty body, 401, 403, TLS certificate failure, connection reset during the body | LicenseServerOutageTests, UpdateServerOutageTests, OfflineWorkflowTests | every fault is a non-fatal result, no exception, local state untouched, recovery on the next call |
| License server | activation and renewal under every fault; offline evaluation; expired license during an outage; renewal after recovery | Licensing.Tests | stays Active/Expired as the clock says; an outage never grants or revokes; renewal issues a newer license |
| Update server | check and download under every fault; no partial package; retry then install | Updater.Tests | nothing on disk, nothing installed, the retry installs once |
| Backup / admin / cloud APIs | not applicable on the desktop: no client exists (CloudBackup is deferred) so nothing local can fail or block; server behavior is covered by Cloud.Tests; the POS path runs with no server assembly loaded (ServerIndependenceTests) | - | see limitations |
| Database | locked (write lock, exclusive lock), failure at each write of a sale (7 table/operation pairs), failure at the last write, failure before COMMIT, constraint violation, migration that cannot apply (rolled back whole, recovers when the conflict is removed), corrupt file, truncated file, path that is a directory | AtomicityFailureTests, DatabaseFailureTests, SharedDatabaseScopeTests | nothing partial remains (counts, stock, cart status, payments compared with the database directly); damaged files are never overwritten; plain messages |
| Lifecycle | clean shutdown (no journal, integrity ok), restart after a completed sale, after a failed commit, after a connection lost with the transaction open (the database view of a killed process), after an interrupted purchase receipt, double submit (sequential and simultaneous) | RestartAndRecoveryTests | the sale or receipt exists exactly once or not at all; no duplicate sale, payment or stock deduction; no receipt for a lost sale |
| Hardware | receipt printer and cash drawer unavailable / timeout / throwing after a sale; label printer, scale (also negative and implausible readings), barcode scanner (cannot start; unknown, blank, control-character and hostile input) | HardwareFailureTests | the sale is complete and durable with one attempt per device and a precise notice; devices never change business data; reprint creates nothing; recovery when the device returns |
| Licensing / security | clock rolled back 3 h (offline), corrupted installation identity, tampered license file, invalid update package | LicenseAndSecurityFailureTests | the Stage 11 words and rules hold: licensed work declined, data readable and untouched, evidence kept, events audited, recovery when the cause is removed |

### Offline workflows verified (real desktop composition; the network refuses and counts every request; business operations make 0 requests)
Sales: open POS session, add by SKU, change quantity, price-list pricing, cash sale with change, card sale, receipt data, drawer, inventory update, close and reopen the session. Inventory: product lookup, stock lookup, adjustment, movement history. Purchasing: supplier, order, lines, submit, receive; stock and order agree. Cash management: open session, pay-in / pay-out / cash-sale, close with counted amount, totals and variance. Customers and suppliers: the administrator creates both; a cashier signing in offline can sell but cannot read customers or create suppliers (Stage 11 boundaries unchanged). Cloud-dependent operations (renew, update check) fail gracefully while selling continues.

### Recovery scenarios verified (Normal -> Failure -> Recovery -> Normal, with no duplicate state)
Cloud returns -> activation, renewal, check and download succeed; printer, drawer, label printer, scale and scanner restored -> the next operation succeeds and the first receipt can be reprinted; database lock released -> the same cart sells once; fault removed -> the same cart or receipt retried succeeds exactly once and a further attempt changes nothing; restart -> state identical and the shop keeps selling; clock corrected -> selling resumes; genuine license restored -> selling resumes; migration conflict removed -> start-up works.

### Known limitations and scenarios that cannot be realistically tested
- A **real process kill** is not simulated. The equivalent that is tested is the database view of it: the connection disappears with the transaction open (no COMMIT, no ROLLBACK) and a new process finds nothing. SQLite hot-journal recovery is relied upon, not re-proven (no crash image is constructed).
- A **real full disk or I/O error** is simulated by SQLite triggers and exceptions, not by the operating system. TLS validation, DNS and sockets are simulated by exceptions raised exactly as HttpClient raises them (plus one real closed port and one real HttpClient timeout); the OS certificate store is not exercised.
- **Physical devices** were not available (as in Stage 10): fakes, and the real adapters over loopback and device paths only.
- **WPF screens** have no automated UI tests (net10.0-windows TFM gap). Covered instead by the composition tests, a smoke run of the real executable (starts offline, reaches sign-in, closes; a corrupt database shows the plain message) and the unit-tested sign-in flow. Signing in and selling through the real window was not automated.
- No **load or soak campaign** beyond the simultaneous double checkout; SQLite is single-writer, a second writer waits up to the busy timeout (default 30 s) and then fails safely.
- Only POS translates unexpected failures into plain results; Purchasing, Inventory, Cash and the other handlers still throw on a database failure (nothing is kept) and need the same translation where their screens are hosted. **[Superseded in FIX-01/FIX-06: every screen action goes through IUiActionRunner, and the write contracts translate failures; see "FIX-06 Summary".]**
- A context that mutated an aggregate and failed before saving keeps the in-memory change until its scope ends (decision 3).
- Cash sales are still not recorded into a cash-drawer session automatically (Stage 8 limitation), so there is no cross-module cash atomicity to test; a cash session is a single aggregate and atomic by one save (tested).
- Update installation crashes were covered in Stage 7 (InstallRecoveryTests) and were not repeated. The log has console and debug providers only (no log file): "details go to the log" means those providers; a log file belongs with packaging (Stage 14).
- Client-side backup failure handling does not exist because there is no client backup module yet.

### Deferred failure/resilience work
Translate unexpected failures at the other module services when their screens are hosted; keep a scope per action in the hosted UI; a log file and crash-report collection (Stage 14); load and soak testing and a UI-automated sale (Stage 13); a physical-device failure campaign when devices exist; automatic retry or queueing of cloud operations if a later stage needs it (nothing queues today, by design).

### Stage 12 commit history (all on master, each built and tested before committing)
dedbc1d one SQLite transaction for checkout and purchase receive | d048360 atomicity failure tests | d5578f0 cloud outage tests | d269e8b safe cloud failure messages | 2df6fd7 offline POS scenarios | 54de606 consistent contexts after failed saves, plain POS failures, busy timeout | 46bba7c database failure tests | 2cd1e8e cart read inside the checkout transaction (double submit) | eb63199 restart and interrupted-operation tests | e9635b6 hardware failure tests | 185b25c ARCH-RES-001..008 | c0d6dd7 plain startup-failure message | 586806e licensing and security failure tests | 388354b shared-transaction unit tests | d8b7fdc deterministic timeout tests | (docs commit)

## Stage 13 Summary

Verification labels used below: **[AT]** automated test (unit/architecture), **[IT]** integration test on the real host (real modules, real SQLite; "offline desktop" = production-like composition with licensing, updater, HTTP transports, security, a network that refuses every request), **[SMOKE]** real Client.Desktop.exe run, **[STATIC]** statically inspected only, **[NOT VERIFIED]**.

### Audit: the dependency map as the repository has it (120 projects)
- Platform: Core (no refs) <- Contracts <- Application <- Infrastructure (EF Core/SQLite). No Platform project reaches a module, client, server or tool. [AT ARCH-SOL-003]
- Modules (13 x Domain/Application/Contracts/Infrastructure/UI): Domain -> Platform.Core only; Contracts -> Platform.Core only; Application -> own Domain/Contracts + Platform.Core/Application + other modules' Contracts; Infrastructure -> own layers + Platform.Infrastructure + Client.Host (for IHostingModule) + other Contracts; UI -> own Application/Contracts + Platform.Core. [AT ARCH-SOL-004..008]
- Cross-module (Contracts only): Inventory->Catalog; Sales->Catalog,Inventory; POS->Catalog,Inventory,Sales (declared) + Pricing,Payments (optional); Pricing->Catalog; Purchasing->Catalog,Suppliers,Inventory; Reporting->Sales,Inventory,Purchasing,Customers,Suppliers (all optional). Acyclic at project and module level. [AT ARCH-SOL-005, 013, 014]
- Database: one SQLite file; 12 module DbContexts + PlatformDbContext (maps nothing); prefixes cat_ inv_ sal_ pos_ cus_ sup_ pur_ pri_ pay_ usr_ aud_ cash_ (Reporting owns no tables); each module's migrations in its Infrastructure; EF's __EFMigrationsHistory/__EFMigrationsLock are shared bookkeeping. 10 contexts join the shared transaction (UseSharedSqlite), Users and Audit stay independent (Stage 12 decision). [AT ARCH-SOL-019..022, ARCH-RES-003; IT ArchitectureCompositionTests, RealHostMigrationTests]
- Client: Desktop (composition root, references every module Infrastructure + UI) -> Host, ModuleHost, Licensing(+Http), Updater(+Http), Hardware, Security. HTTP only in Client.Licensing.Http and Client.Updater.Http. Licensing/updater/security reach no business module. [AT ARCH-SOL-006, 010, 015; ARCH-LIC/UPD/SEC]
- Cloud: Cloud.Contracts/Infrastructure/Hosting, LicenseServer, UpdateServer, AdminPortal, BackupServer - reached by no module or Platform project [AT ARCH-SOL-010, ARCH-CLD, ARCH-RES-006] and not referenced by Client.Desktop [STATIC; ServerIndependenceTests IT for the composition].
- Tools: ModulePackager, UpdatePublisher (signing) - referenced only by tests.

### Comparison with "Architecture & Solution Design.md" (discrepancies and the decision for each)
| # | Document | Implementation | Decision |
|---|---|---|---|
| 1 | Sections 11/14/39-41: Client.Host/ModuleHost discover, validate, register, activate modules | Nothing ran the lifecycle; registry empty at runtime | **Implementation wrong - fixed** (ModuleLifecycleService, approved). Disk discovery of packaged modules is still not used (modules are compiled in; launcher deferred). |
| 2 | Section 20 / Rule 6: UI does not manipulate EF Core or SQLite | Catalog.UI referenced Client.Host (reaching Platform.Infrastructure, EF Core, SQLite) | **Implementation wrong - fixed** (reference replaced by Platform.Core). |
| 3 | Section 40: IModule has ConfigureServices/RegisterUI | IHostingModule (DI) and IModule (lifecycle) are separate; no RegisterUI | Document is conceptual ("exact interface designed during implementation"); Stage 4 decision 1 stands. No change. |
| 4 | Section 14: ModuleHost checks entitlements | License enforcement is central in AuthorizationService (Stage 11) | Deliberate Stage 11 decision; modules stay compiled in and unlicensed work is refused per capability. No change. |
| 5 | Section 71: the Audit implementation belongs to the platform | Platform owns the abstraction (ISecurityEventSink); the Audit module stores; business actions are not audited yet | Consistent with "modules CAN record"; adoption is the documented Stage 8 limitation. No change. |
| 6 | Module UI uses Application only | Catalog/Payments/CashManagement UI use their OWN Domain enums/value objects (via Application) | Same module, no persistence path, Rule 3 is about Domain->UI. Recorded, not changed. |
Nothing in the architecture was changed silently; the document needed no edit (its rules hold; the gaps were in the implementation).

### Defects found by Stage 13 and fixed
| # | Defect | Fix | Evidence |
|---|---|---|---|
| 1 | The module lifecycle never ran: IModuleRegistry empty in the real desktop; the updater's installed state saw no module, so a signed DOWNGRADE of an installed module was accepted as a new install, schema and dependent checks compared against nothing, discovery reported no installed module; a module composed without its dependency started and failed on first use with a raw DI exception | ModuleLifecycleService (fail fast, user decision) + ModuleCompositionException + StartupFailure.Modules | ModuleLifecycleServiceTests (9), ModuleLifecycleIntegrationTests (4: the updater test fails on the old code), UpdateIntegrationTests (downgrade accepted when the lifecycle is disabled - checked), smoke runs (13 modules running) |
| 2 | Catalog.UI could reach a DbContext (Rule 6) | project reference removed | ARCH-SOL-008 fails against the old project file |
| 3 | Generic architecture rules (licensing, update, cycle detection) saw only 5 of 13 modules; six "deferred" tests always passed and claimed the desktop referenced no module infrastructure (false) | registry completed + ARCH-SOL-002 guards it; placeholders replaced by ARCH-SOL-006/008/009 | ARCH-SOL-002 |
| 4 | The offline-desktop test harness set Updater:StorageDirectory (not a setting), so the updater used the real %LOCALAPPDATA% folder (nothing had been written there) | Updater:UpdateRoot | UpdateIntegrationTests run isolated |

### Architecture rules - how each of the 12 mandatory rules is enforced now
| Rule | Enforced by |
|---|---|
| 1 Platform.Core/Platform never depend on modules | ARCH-SOL-003 (project closure), ARCH-001*, per-module ARCH-xxx-012 [AT] |
| 2, 3 Domain knows no infrastructure, no UI | ARCH-SOL-004 (every Domain: refs Platform.Core only, no packages, no WPF) [AT] |
| 4 No other module's Infrastructure | ARCH-SOL-005/006 (only the composition root references module Infrastructure) [AT] |
| 5 Contracts only | ARCH-SOL-005/007 (project graph), ARCH-SOL-017/018 (contract surfaces: no EF/SQLite/ADO/WPF/HTTP/IQueryable/expressions, no Domain/Application/Infrastructure type) [AT] |
| 6 UI never accesses the database | ARCH-SOL-008 (no UI closure reaches Infrastructure/Client.Host/EF/SQLite), ARCH-SOL-009 (desktop shell code has no DbContext/connection/SQL) [AT] |
| 7 No HTTP in business logic | ARCH-SOL-010 (project + package closure), ARCH-SOL-011 (types) [AT] |
| 8 Cloud not required offline | ARCH-SOL-010, ARCH-RES-006 [AT]; Stage 12 OfflineWorkflowTests + Stage 13 vertical slice and reporting with 0 network requests, ServerIndependenceTests [IT] |
| 9 Explicit module dependencies | ARCH-SOL-012 (one manifest per module, the full set resolves), ARCH-SOL-013 (declared deps are real; undeclared ones are optional constructor parameters) [AT]; enforced at startup by the lifecycle [IT, SMOKE] |
| 10 No cycles | ARCH-SOL-014 (project and module graphs), ARCH-010, resolver at startup [AT] |
| 11 No module modifies another's tables | ARCH-SOL-019 (migrations), 020 (no foreign table name in module code), 021 (no business SQL in Client/Platform), 022 (shared transaction) [AT]; ArchitectureCompositionTests (EF models), RealHostMigrationTests, vertical slice before/after table comparison [IT] |
| 12 Licensing has no business logic | ARCH-SOL-015 (licensing and modules never reach each other), ARCH-SOL-016 (capabilities owned by the declaring module's manifest ID), ARCH-SEC-005 [AT] |

### Shared SQLite transaction (Stage 12) - evaluated against the architecture
Kept, confirmed sound: it shares a CONNECTION per DI scope, never a DbContext; IAtomicOperation exposes only Result-returning work (no context, connection or SQL) and SharedDatabaseScope's connection API is internal [AT ARCH-SOL-022]; no module names SharedDatabaseScope, modules join only by registering their OWN context [AT ARCH-SOL-022, ARCH-RES-003]; only Application-layer use cases open it (section 35) [AT ARCH-SOL-022]; rollback is atomic [IT Stage 12 AtomicityFailureTests]; the checkout wrote only to the owners' tables [IT VerticalSliceOwnershipTests].

### Verification status by area
| Area | Status |
|---|---|
| Module isolation (remove optional module: core starts and sells; absent module's tables/contracts absent) | [IT] OptionalModuleIsolationTests, ModuleLifecycleIntegrationTests |
| Dependent functionality when a dependency is missing | [IT] start refused with a plain message naming the module (fail fast by decision) |
| "Disabled" module | There is no runtime enable/disable switch: a module is absent (not composed) or unlicensed (capabilities refused) - both [IT] |
| Module lifecycle (valid, missing, duplicate, incompatible version, cycle, init failure, start failure, shutdown order, stop failure) | [AT] ModuleLifecycleServiceTests; full composition and missing dependency [IT]; real process [SMOKE] |
| Contracts | [AT] ARCH-SOL-017/018 |
| Database ownership | [AT] + [IT] (see rule 11) |
| Vertical slice (product, stock, POS, find, cart, Pricing price, sale, payment, stock issue + movement, cashier attribution, audit) offline + licensed + signed in | [IT] VerticalSliceOwnershipTests. Audit: security events only - a completed sale is NOT audited [NOT VERIFIED: not implemented, Stage 8 limitation] |
| Catalog<->Inventory, Catalog<->POS, POS<->Sales, Sales<->Inventory, POS<->Payments | [IT] ModuleIntegrationTests, VerticalSliceOwnershipTests, Stage 12 suites |
| POS<->Users/Authorization, Business<->Audit | [IT] SecurityIntegrationTests (Stage 11), vertical slice |
| Modules<->Licensing (licensed, unlicensed, expired, revoked, suspended, grace period, entitlement isolation on writes, data intact, authentication separate) | [IT] LicensingIntegrationTests + SecurityIntegrationTests + LicenseAndSecurityFailureTests |
| Modules<->Update (valid, invalid signature, unknown key, invalid hash, incompatible host, downgrade, same version, dependency conflict, missing dependency, unlicensed module, schema, failed migration + restore point + rollback with data restore, recovery) | [IT] UpdateIntegrationTests; crash recovery, interrupted install, binary rollback in depth [AT] Updater.Tests (Stage 7/12) |
| Client<->Cloud | [IT/AT] Stage 12 outage suites, ServerIndependenceTests; desktop never calls AdminPortal/BackupServer (no client exists) |
| Security controls (authentication, capabilities, cashier identity, session ownership, customer data, cost price, password policy, audit, installation identity, license protection, update signatures, HTTPS, throttling, clock rollback) | Unchanged and green: Security.Tests, Users.Tests, ARCH-SEC-001..017, Cloud.Tests, Stage 11/12 integration suites; cashier identity on a session re-verified in the vertical slice [IT] |
| Desktop | [SMOKE] start, first-run setup, shell, sign-out, sign-in, wrong password, restart, clean shutdown (exit 0), 13 modules running. No business workflow through the UI: the shell hosts no business screens yet. |

### Decisions introduced
1. **The runtime module lifecycle fails fast** (user decision): an invalid composition or a module that cannot initialize/start stops the start with a plain message; an absent optional module is never a failure.
2. **Architecture rules are discovered, not listed**: projects from the .csproj files, modules from src/Modules, prefixes from migrations, manifests from the IModule types. A new module is checked without editing a rule.
3. Cross-module read contracts track entities (EF default); this is safe under the Stage 12 rule "one DI scope per user action" and Stage 13 tests follow it. A scope that outlives one action can read a stale product (observed in a test that broke the rule).

### Remaining limitations (not fixed in Stage 13; owners)
- Modules are compiled in; ModuleHost's file-system discovery and updater-activated versions are not loaded at runtime (launcher - Stage 14). ConfirmHealthyAsync is still not called by the host (Stage 14). **[Done in FIX-03 for updates that really run; see "FIX-03 Summary".]**
- Business actions are not audited (Audit adoption by modules - follow-up); CashManagement not fed by POS (Stage 8). **[CashManagement: done in FIX-04. Audit: done in FIX-05.]**
- Only POS translates unexpected failures into plain results (Stage 12 limitation). **[Superseded in FIX-06.]**
- Cross-module read contracts track entities; hosting the screens must keep one scope per action, or switch the readers to AsNoTracking. **[Done in FIX-01/FIX-07: one scope per action, enforced in the shell and tested; readers keep tracking.]**
- Module UIs use their own Domain enums (Catalog, Payments, CashManagement) - accepted.
- WPF screens: no UI automation of business workflows (the shell has none); smoke covers start/sign-in/shutdown only.
- No load or soak testing; no real process kill, full disk or physical device (Stage 12 limitations unchanged).

### Stage 13 commit history (all on master, each built and tested before committing)
b4d4d5a Catalog.UI without Client.Host | 23c90bd ARCH-SOL-001..016 | d50be5f ARCH-SOL-017..022 | 33c3e75 module lifecycle (fail fast) | 2211930 offline desktop updater folder | 52db999 vertical slice ownership + module integration | 7b5bd9a licensing integration | 9dd9889 update integration | 0295d64 EF model ownership | c44e1c3 analyzer warning | (docs commit)

---

## FIX-01a Summary - Shell foundation (2026-10-07)

Scope source: PRE_DEPLOYMENT_CHECKLIST.md FIX-01a. Decisions (user, 2026-10-07): DI scope per user action; unlicensed modules shown locked with a reason, no-permission screens hidden; handwritten MVVM; every UI string in resources, RTL-safe layouts.

    Module UI (IScreenProvider: ScreenDescriptor id, module, group, title resource, view type, view-model type, capability)
        ---> Client.Desktop DesktopServicesRegistrar registers the providers (composition root; a module UI never references Client.Host)
        ---> NavigationBuilder (Platform.Presentation): permissions held NOW (read through the runner) -> hidden / available / license-locked
        ---> ShellViewModel + MainWindow: grouped menu, open screen, lock reason, welcome; screens created once per signed-in session, discarded at sign-out
    View model action ---> IUiActionRunner: new async DI scope per action, disposed after it; an exception is logged and becomes PresentationText.OperationFailed

- **Platform.Presentation (new, net10.0, no WPF/EF/HTTP)**: Screens/ (ScreenDescriptor + Validate, IScreenProvider, INavigationAware, ScreenGroups: sales, inventory, purchasing, people, finance, reports, administration; NavigationBuilder), Actions/ (IUiActionRunner, IActionScope, UiActionRunner, UiErrors), Mvvm/ViewModelBase (Set/Raise, IsBusy, ErrorMessage, StatusMessage, Accept(Result), Command/Command<T> that never let an exception reach the UI thread), Localization/UiCulture (Ui:Culture, unknown -> "en", IsRightToLeft), Resources/PresentationText.resx. Packages: Microsoft.Extensions.DependencyInjection.Abstractions and Logging.Abstractions 10.0.11.
- **Client.Desktop**: Shell/ShellViewModel, Shell/ScreenFactory (WpfScreenFactory: view model via ActivatorUtilities from the application services, view via its parameterless constructor), Resources/ShellText.resx, MainWindow rewritten (menu + content; Stage 2 placeholder removed; FlowDirection from the culture), DesktopComposition (the hosting-module list, moved out of App.xaml.cs so tests use the same list), App applies Ui:Culture before the first window. appsettings: "Ui": { "Culture": "en" }.
- **Resources**: strongly typed classes are generated by MSBuild (EmbeddedResource StronglyTyped* metadata); WPF projects also need target GenerateStronglyTypedResourcesBeforeCompile (the XAML *_wpftmp compile runs before ResGen). No generated file is committed.
- **Rules for screens**: a view model may depend on singletons only (UI.Tests checks every declared screen against the real composition); scoped services are reached per action through the runner. Showing a screen is a convenience: handlers still authorize every action.
- **Tests**: UI.Tests (new, net10.0-windows; closes the TFM gap for the shell and view models) 44: NavigationBuilderTests, UiActionRunnerTests, ViewModelBaseTests, UiCultureTests, ShellViewModelTests, DesktopCompositionTests. Architecture.Tests +2: ARCH-SOL-023 (Platform.Presentation is technology free), ARCH-SOL-024 (only the desktop and module UIs reference it); ARCH-SEC-016 and ARCH-HW-010 now read DesktopComposition.cs.
- **Real executable** (isolated folders, UI Automation, window-only capture): first-run setup -> shell with menu area, signed-in name, license notice, "no screens yet" (no module declares a screen until FIX-01b) -> sign out -> sign in -> close, exit 0, no error log lines, 13 modules running, "Display language: en".
- **Not done here (owners)**: SignInWindow texts are still inline (FIX-13); the per-module ViewModelBase copies are replaced when each module is converted (FIX-01c/d); the WPF resource target moves to a shared import when the first module UI gets resources (FIX-01b).

---

## FIX-01b Summary - POS screen in the shell (2026-10-07)

- **Screen**: POS.UI Screens/PosScreens declares "pos.sell" (group Sales, capability pos.sale.create); registered in DesktopServicesRegistrar. POS.UI references Platform.Presentation; texts in Resources/PosText.resx.
- **PosViewModel rewritten** on ViewModelBase + IUiActionRunner + ICurrentUser (no service held; one scope per click): on navigation it RESUMES the signed-in cashier's open till and cart (or starts a cart for it), otherwise lists the active warehouses (the only one is preselected; none -> plain instruction). Commands: open till, add by barcode/SKU (Enter key; quantity parsed in the current culture, > 0), remove selected line, checkout (then a new cart; total shown; peripheral notices shown separately, the sale stands), close till (only with an empty cart). No exception text reaches the screen (Stage 5D view model showed ex.Message). The cashier is the signed-in user (the session command already enforced it).
- **PosView rewritten**: bindings to commands only (code-behind keeps the barcode box focused), dark cart grid, numbers aligned at the end, no left/right-specific placement.
- **POS backend**: IPOSReader.FindOpenSessionAsync(cashierReference) and GetWarehousesAsync() (contract model POSWarehouseResult); POS.Application GetOpenSessionForCashierQuery (IPosSessionRepository.GetOpenSessionForCashierAsync) and GetSaleWarehousesQuery (active warehouses through Inventory.Contracts IInventoryReader, a declared POS dependency).
- **Build**: src/Directory.Build.targets runs ResGen before CoreCompile in every WPF project (the XAML temporary assembly needs the generated resource classes); removed from Client.Desktop.csproj.
- **Tests**: UI.Tests +24 (PosViewModelTests 20 against an in-memory till through the real runner; PosScreenOnRealDesktopTests 3 on the production-like OfflineDesktop: sell offline and stock goes down, the open till and cart are resumed by a new screen, an unknown code is refused in plain words; DesktopCompositionTests: every declared screen view loads its XAML on an STA thread). UI.Tests references Integration.Tests (InternalsVisibleTo) for OfflineDesktop. POS.Tests +3 (PosScreenQueriesTests); PosTestDatabase registers the new handlers and an IInventoryReader stub.
- **Real executable**: the menu shows "Point of sale" under Sales; on the unlicensed installation it is marked Locked and opening it shows "Not included in the current license (Unlicensed). Your data is safe and unchanged."; exit 0, no error log lines.
- **Not done here (owners)**: payment method choice and tendered amount at checkout (FIX-10 with split payments); scanner input (FIX-02); change quantity / clear cart commands exist in IPOSService but have no button yet.

---

## FIX-01e (license part) Summary - License screen (2026-10-07, done before FIX-01c by user decision)

Why first: a fresh installation is Unlicensed and nothing on the desktop could activate it, so the POS screen of FIX-01b stayed locked in the real application.

- **Screen** (Client.Desktop, because licensing is a client component, not a business module): Screens/DesktopScreens declares "licensing.license" (group Administration, capability licensing.manage = LicenseRequirement.None, so it is NEVER locked: an unlicensed or expired installation can always be fixed). Screens/Licensing/LicenseViewModel + LicenseView, texts in Resources/LicenseText.resx.
- **Shows**: state in plain words, the LicenseNotice explanation (warning colour when restricted), customer, valid until, renew-online-before (lease), licensed modules, the installation ID (selectable, for vendor support). **Does**: Activate (activation key; Enter works; the key is cleared after success and never logged), Renew now (only with a verified license). Both go through ActivateLicenseCommandHandler / RenewLicenseCommandHandler (they authorize) via the runner.
- **Shell refresh**: Platform.Presentation IShellNavigation (implemented by ShellViewModel, registered as the same singleton): after activation or renewal the navigation is rebuilt at once, so licensed screens unlock without a restart; the status bar shows the new state.
- **Automation IDs** made unique per screen (LicenseStatusText/LicenseErrorText, PosStatusText/PosErrorText) so they never clash with the shell's StatusText/ErrorText.
- **Tests**: UI.Tests +8: LicenseViewModelTests 7 (unlicensed display + installation ID, key required, successful activation shows license/clears key/refreshes shell, refused activation keeps key and state, handler refuses without licensing.manage, unexpected failure plain, renewal); LicenseScreenOnRealDesktopTests 1 on the offline desktop starting UNLICENSED with a stub license server that signs with the trusted key: wrong key refused in the server's words, right key verified and stored, POS entry goes from LicenseLocked to available in the shell, status bar "license: Active". Integration.Tests SignedLicenseWorld.Sign (sign without storing).
- **Real end to end** (real Client.Desktop.exe + real LicenseServer.Api on 127.0.0.1 in Development with a throwaway signing key trusted through Licensing:TrustedKeys and one seeded license): first-run setup; POS locked; License shows "Not activated" and the installation ID; wrong key -> "Unknown activation key."; valid key -> Active, customer, 13 modules, "The installation is activated.", no locked entry left; POS opens ("no active warehouse yet"); restart -> still Active offline; exit 0 both runs; no error log lines; the activation key never appears in the desktop log.
- **Found (not fixed here, separate task)**: LicenseServer.Api development seeding (LicenseServer:DevLicenses) silently drops a seeded license without a Features entry (the DevLicense record requires it), so its key answers "Unknown activation key". Dev-only; production licenses come from AdminPortal.
- **Not done here (owners)**: user/role administration screens (rest of FIX-01e); the Home notice text still comes from Client.Licensing in English (FIX-13).

---

## FIX-01c Summary - Catalog, Inventory and Sales screens (2026-10-07)

Screens (group, capability that shows them): Products (Products and stock, catalog.product.edit), Stock (inventory.stock.receive), Warehouses (inventory.location.manage), Categories and units (catalog.category.manage), Sales history (Sales, none: read-only; reading the shop's own sales needs no license or capability). All run through IUiActionRunner; texts in CatalogText/InventoryText/SalesText.resx; the Stage 5 view models/views (ProductList, CreateProduct, StockLevel, WarehouseList, SaleList) were removed.

- **Products**: search by name/SKU (case-insensitive part) or exact barcode, show inactive, create/edit (category, unit, sale price, cost price, description, add a barcode; format inferred from the value: 13 digits EAN-13, 8 EAN-8, 12 UPC, else Code 128), deactivate (history kept). Without catalog.cost.view the cost field is disabled and explained. Catalog.Application SearchProductsQuery (IProductRepository.SearchAsync: LIKE with literal wildcards, MaxResults 500 + IsTruncated).
- **Categories and units**: list and add (names are not unique by design - unchanged).
- **Stock**: on hand per product and warehouse with readable names (Inventory.Application GetStockOverviewQuery: product SKU/name through Catalog.Contracts IProductLookup, warehouse names; a product Catalog no longer knows is shown by ID, never hidden), filter by warehouse and SKU/name, receive by SKU or barcode (FindStockProductQuery: SKU, then IProductBarcodeResolver when composed) with an optional reference, correct the selected line (+/-) with a reason (stock count, damaged, found, transfer in/out, opening balance, other) and notes.
- **Warehouses**: list (with status) and add (duplicate code refused by the existing handler).
- **Sales history**: a period of local days (today by default) converted to UTC, newest first, lines of the selected sale, "Completed sales: N, total X" counting COMPLETED sales only. Sales.Application GetSalesHistoryQuery (ISaleRepository.GetCreatedBetweenAsync, MaxResults 1000 + IsTruncated); the sale-to-DTO mapping is shared (SaleDtoMapper).
- **Defects found and fixed while building the screens** (each has a test):
  1. Editing a product without catalog.cost.view ERASED its stored cost (reads redact the cost to null, UpdateProductCommand wrote the null back). UpdateProductCommandHandler now keeps the stored cost for such callers.
  2. A barcode could be assigned to two products (no rule, non-unique index), so a scan at the till picked one of them at random. AssignBarcodeCommandHandler now refuses a barcode another product carries ("Catalog.Barcode.InUse", naming the owner). Existing duplicates in a database are not cleaned up (none can exist from the desktop, which had no barcode screen before).
- **Tests**: Catalog.Tests +9 (ProductSearchTests 6, BarcodeUniquenessTests 1, cost preservation 2), Inventory.Tests +2 (StockOverviewTests), Sales.Tests +2 (SalesHistoryTests), UI.Tests +18 on the real offline desktop (catalog 7 incl. a product created on screen and sold on POS; inventory 5 incl. the whole back office through screens only: product -> warehouse -> receive 12 -> sell 3 by barcode -> stock 9; sales history 2; every new view loads its XAML).
- **Real executable end to end** (real exe + real local LicenseServer.Api): first-run setup, activate, add category/unit, product with barcode and cost, warehouse, receive 24 (reference DN-1001), open till, scan the barcode x3 (3.75), checkout, stock shows 21, sales history "Completed sales: 1, total 3.75"; exit 0, no error log lines.
- **Not done here (owners)**: product edit cannot remove a barcode (no command exists); sales history has no export or refund (Stage 8 limits unchanged); category/unit names may repeat; the license-server dev-seeding bug is a separate task.

---

## FIX-01d Summary - Stage 8 back-office screens (2026-10-07)

Screens (group, capability that shows them): Customers (Customers and suppliers, customers.customer.view), Suppliers (suppliers.supplier.manage), Prices (Products and stock, pricing.price.manage), Purchase orders (Purchasing, purchasing.order.create), Cash drawer (Cash and payments, cash.session.manage), Business overview (Reports, reporting.view), Audit log (Administration, audit.view). All through IUiActionRunner, texts in resources; the Stage 8 view models and their eight private ViewModelBase copies are removed (every module UI now uses Platform.Presentation).

- **Customers / Suppliers**: search (code, name, e-mail, phone), list 200 at a time with a count, show inactive, create/edit (the editor loads the full record, notes kept), deactivate/reactivate. Addresses and contacts have handlers but no screen yet.
- **Prices**: price lists (add, optionally as the one used for sales; choose it; deactivate) and the prices of a product found by SKU or barcode (Pricing.Application FindPricingProductQuery): add a dated price per list with a quantity break and an included end day (stored end exclusive), deactivate, and "price at the till now" or "the catalog price applies".
- **Purchase orders**: list by status; new order for an ACTIVE supplier found by code/name (Purchasing.Application FindOrderSuppliersQuery through Suppliers.Contracts ISupplierReader); while Draft: add lines by SKU (unit cost defaults to the product cost), remove, place; cancel a draft or placed order with a reason; receive a placed order into a warehouse (ListReceivingWarehousesQuery through Inventory.Contracts) - one transaction with Inventory (Stage 12).
- **Cash drawer**: per drawer code: open a shift with a float, pay in / pay out with a reason, count and close (expected amount and difference kept and shown), recent shifts with their difference.
- **Business overview**: a period of local days (this month by default), one card per section (sales, stock, purchasing, customers, suppliers; a missing module shows why), and the sales per day (days with sales only).
- **Audit log**: newest first, filter by module, action and local days (last 7 by default), 100 per page, details of the selected entry; read-only.
- **Defect fixed**: the cash handlers took OpenedBy/ClosedBy/RecordedBy from the caller, so a drawer could be opened, paid out or closed in someone else's name. They now record the signed-in user whenever someone is signed in (CashActor, the POS till rule of Stage 11); the supplied name is used only by hosts without authentication. Test: CashManagementAuthorizationTests.
- **Found, recorded under FIX-12**: the sales report's daily breakdown uses UTC dates, so a shop outside UTC sees sales near midnight on the wrong day; the overview lists only days with sales meanwhile.
- **Decided**: no Payments screen. Payments has no list query and only "void"; voiding a payment of a completed sale from a separate screen would leave the sale inconsistent. Payment handling belongs to checkout (FIX-10).
- **Display fixes found in the real executable**: text inside the screens' data templates inherited a dark default colour (implicit TextBlock styles stop at template boundaries) - the shell now sets TextElement.Foreground on the screen host; unlabeled fields in Prices and Purchase orders got labels; the menu is denser so its 14 entries fit the default window.
- **Tests**: CashManagement.Tests +1, UI.Tests +13 on the real offline desktop (customers 2, suppliers 1, cash 2, overview 2, audit 2, prices 2 incl. the till charging the price set on the screen, purchase orders 2 incl. received stock 10 -> 22).
- **Real executable**: with a real local license server: activate, then every one of the 14 menu entries opens with no error and no lock; exit 0, no error log lines.

---

## FIX-01e Summary - Users, roles and permissions screens (2026-10-07)

Decisions (user): (1) user administration can never lock itself out - enforced in the handlers; (2) the administrator types a temporary password (password box, never bound, shown or stored by the screen); the user must choose a new one at the first sign-in (the Stage 11 flow).

- **Users screen** (Administration, users.view; changes need users.manage): search, show inactive, create a user with a temporary password (the configured password policy is checked BEFORE the user is created, so a refused password never leaves a user who cannot sign in), change name and e-mail, deactivate / reactivate, give and take away roles, set a new temporary password. The code-behind hands the password box content to the command at the moment of use and clears the box.
- **Roles and permissions screen** (users.manage): roles, a new role, and for the selected role every capability the installed modules declare (ICapabilityCatalog) with area, name, sensitive flag and description; ticking grants, unticking revokes, immediately; the list is rebuilt from what was stored, so a refused change shows the old state again. A permission no installed module declares stays listed and removable.
- **Lock-out rule (Users.Application AdministrationGuard)**: DeactivateUser, RemoveRole and RevokePermission(users.manage) are refused ("Users.LastAdministrator", plain words) when they would take the number of ACTIVE users holding users.manage through a role from one or more to zero; DeactivateUser refuses deactivating oneself ("Users.SelfDeactivation"). Evaluated on the stored state with the pending change as parameters (IUserRepository.GetActiveUserRolesAsync, new). When nobody holds users.manage already (hosts without authentication, test hosts) the change does not make anything worse and is allowed.
- **Accessibility defect found by the real-executable smoke**: the permission ticks used a CheckBox Command, which UI Automation toggling (screen readers, automation tools) does not invoke - a tick could look granted without being granted. PermissionRow is now a two-way row: any way of ticking (mouse, keyboard, automation) asks the handler.
- **Tests**: Users.Tests +5 (AdministrationGuardTests); UI.Tests +3 on the real offline desktop signed in as its only administrator (new user with temporary password -> refused weak password creates nothing -> role -> sign-in demands a new password -> permissions; the lock-out refusals through both screens; edit, new temporary password, deactivate).
- **Real executable**: owner creates a Cashier role and grants Sell by UI Automation, creates Sara with a temporary password and gives her the role; Sara signs in, must choose her own password, then sees only Point of sale and Sales history; exit 0; no error log lines; no password in the log.
- **Not done here (follow-ups)**: capabilities that go together are not suggested (a cashier needs both "Sell" and "Open and close POS sessions"); roles cannot be renamed or deleted (no command exists); usernames cannot be changed.

---

## FIX-02 Summary - Barcode scanner input (2026-10-08)

- **Key forwarding (shell)**: `MainWindow` hands its key presses (PreviewTextInput / PreviewKeyDown) to `ScannerKeyboard` (Client.Desktop/Shell, WPF-free), which the composition root (`ScannerKeyboardHostingModule` in DesktopComposition.cs, the only desktop file that may name Client.Hardware - ARCH-HW-010) connects to `IKeyboardInputSink`. Keys typed into a text box or password box are NOT forwarded: there the scanner's typing lands in the box and the box's own Enter runs (on the cashier screen the barcode box adds the product), so forwarding as well would add it twice. Elsewhere (grid, button, nothing focused) the decoder sees the keys.
- **The scan's Enter is swallowed**: `IKeyboardInputSink.OnCharacter` now returns true when the character completed a scan; the window then marks that Enter handled, so it cannot also press the focused button (Checkout). An ordinary Enter, slow typing and too-short input are not consumed.
- **POS screen**: `PosViewModel` takes the optional `IPOSBarcodeInput`. Shown: starts the scanner, subscribes, binds the open cart, shows "Scanner ready". Each scan: the cart is shown again ("Item added.") or the refusal in plain words (on the UI thread). During checkout and till close no cart is bound (a scan cannot land in the cart being sold); afterwards the next customer's cart is bound. No scanner configured (or it cannot start): nothing is claimed, codes are typed as before.
- **Leaving a screen**: `INavigationAware.OnNavigatedFromAsync` (default: nothing) is called by the shell when another screen (or a locked entry) is chosen and on sign-out (`ShellViewModel.ResetAsync`, was `Reset`); the cashier screen stops the scanner and unbinds, so a scan on another screen does nothing and a signed-out user's screen never receives scans.
- **Tests (+22)**: Hardware +1 (only the completing Enter is consumed); UI +21: ScannerKeyboard (5), shell leave notifications (3), POS view model with a fake scanner input (10), real offline desktop with the real keyboard-wedge decoder and the real bridge (3: a scan on the grid is sold once and the stock goes down; after leaving the screen a scan is not a scan; without scanner configuration every key is ignored).
- **Real executable** (isolated database, local LicenseServer.Api with a throwaway key, `GENERICPOS_Hardware__Scanner__Type=KeyboardWedge`, keystrokes posted only to the app's window handle): activate -> warehouse, category/unit, product with barcode, receive 10 -> open till ("Scanner ready" shown) -> scan with the cart grid focused: added; scan with Checkout focused: added and NO sale; scan into the barcode box: added once; unknown code: refused in plain words; checkout 7.50; a scan on the Stock screen does nothing; exit 0.
- **Not done here (owners)**: physical scanner verification and serial/USB scanners (MISS-07); a scan always adds quantity 1 (the quantity box applies to typed codes).

---

## FIX-03 Summary - Healthy-start confirmation (2026-10-08)

Decisions (user, 2026-10-08): (1) only what really runs is confirmed - an activated update is confirmed when the version running in this process equals the activated (and active) version; (2) a start is healthy when the host started (every hosted service: migrations, the fail-fast module lifecycle) and the start screen has rendered - nobody has to sign in.

- **Client.Updater**: `StartupHealthConfirmation.ConfirmAsync` (once per process, never throws, logs) goes through the Activated journals (the latest per target, as ConfirmHealthyAsync picks it) and calls `ConfirmHealthyAsync` for each target that runs at that version. `IRunningVersions`: core = new `Updater:RunningHostVersion` (empty = `BaselineHostVersion`, the built-in installation); a module = the manifest version the module host loaded (IModuleRegistry).
- **Client.Desktop**: `App` confirms when the sign-in window has rendered (`ContentRendered`); a failure is logged and never stops the application.
- **Consequence until PKG-01**: the application runs its built-in binaries, so an update that is activated on disk is NOT confirmed (it never ran) and startup recovery rolls it back after MaxStartupAttempts starts, as designed; old versions are not pruned for an untested update. The PKG-01 launcher must set `Updater:RunningHostVersion` for the core it starts and the module host must load activated modules; then confirmation follows automatically.
- **Tests (+10)**: Updater +9 (a running module update is confirmed and survives later starts; a non-running one is rolled back after the attempt limit; module not loaded; core only when that core runs; several targets; once per process; no update folder; a failure never escapes; running versions from configuration and the registry); Integration +1 on the real offline desktop (real signed core and catalog packages installed through the authorized handler: core 1.1.0 runs and is confirmed, catalog 1.1.0 stays activated because catalog 1.0.0 runs).
- **Real executable** (isolated update folder and database; activated core 1.1.0 and catalog 1.1.0 journals; `Updater:RunningHostVersion=1.1.0`): at the start screen core is Confirmed, catalog stays Activated (start 1 of 2); exit 0.

---

## FIX-04 Summary - POS cash sales into the cash drawer (2026-10-08)

Decisions (user, 2026-10-08): (1) the cash is recorded INSIDE the checkout transaction, and a cash sale is REFUSED when the till's drawer has no open shift; (2) the drawer is configured per installation, `PosCash:DrawerCode` (default MAIN, the Cash drawer screen's default; one till = one drawer); (3) the POS screen takes the total in cash now (method choice, tendered amount, change and split payments stay FIX-10).

- **Checkout** (`CheckoutCartCommandHandler`, optional `ICashMovementRecorder` + `ICashSessionReader` + `PosCashOptions`): for a Cash payment, step 1c finds the open shift of the configured drawer before anything is written (refusal "POS.Checkout.CashDrawerNotOpen": "The cash drawer 'MAIN' has no open shift. Open it on the Cash drawer screen before taking cash."); step 6b records a CashSale movement for the cart total (the change goes back to the customer) with the sale as reference ("sale" + sale id, idempotent per kind) and the cashier as recorder, in the same SQLite transaction as sale, payment, stock and cart. A drawer failure rolls everything back ("POS.Checkout.CashDrawerFailed"). Card / Other / payment-less checkouts never touch the drawer and do not need it open. Without the CashManagement module nothing changes.
- **Dependencies**: POS.Application references CashManagement.Contracts only (optional, like Payments; not a manifest dependency). ARCH-POS-014 now also covers CashManagement; ARCH-RES-004 counts ICashMovementRecorder as a cross-module write that must be in IAtomicOperation.
- **POS screen**: checkout passes a Cash payment for the total. Operational consequence: a shop opens the drawer shift (Cash drawer screen, cash.session.manage) before the first sale of the day.
- **Not possible here**: cash REFUNDS - neither POS nor Sales.Contracts has a refund flow yet; CashRefund movements stay unused until one exists.
- **Tests (+9)**: Integration +7 (CashDrawerIntegrationTests: cash into the shift with the sale reference and 50 + 5.00 balance; refused without a shift and nothing written, then sells once opened; the configured drawer TILL-2 is used and MAIN untouched; Card / Other / no payment never touch the drawer; AtomicityFailureTests + cash_Movements INSERT failure rolls the whole sale back and the retry succeeds once), UI +2 (the till refuses then sells once the Cash drawer screen opened the shift, the drawer screen shows the Cash sale; the screen pays in cash). Test kit: CreateShopAsync opens the MAIN shift (float 50) when the host has CashManagement; VerticalSliceOwnership now expects five owning modules (cash_ added) and reads the drawer balance back.
- **Real executable** (isolated database, local LicenseServer.Api): activate, set up and stock a product; checkout without a shift -> refused with the plain sentence, the cart kept; Cash drawer screen: open with 100; checkout -> "Sale completed: 5.00."; Cash drawer screen: balance 105.00, one "Cash sale +5.00" by admin; exit 0.

---

## FIX-05 Summary - Business actions in the audit log (2026-10-08)

Decisions (user, 2026-10-08): (1) a Platform event sink like the security events (no module references Audit); (2) an in-memory retry buffer (no durable outbox); (3) the checklist list of actions.

Constraint found: since Stage 12 the audit log uses its OWN connection (an audit record must survive the rollback of the operation it describes), and SQLite cannot take a second writer while a business transaction holds the lock - so business entries are reported AFTER the action committed.

- **Platform.Application.Abstractions.Auditing**: `BusinessEvent` (module, action, entity type/id, summary, details; sanitised by SecretRedactor and cut to the audit limits on creation, so an entry is never refused for its length and cannot carry a secret), `IBusinessEventSink` (best effort by contract), `TryRecordAsync` (null sink = nothing; never throws; ignores the caller's cancellation because the action is committed).
- **Audit**: `AuditSecurityEventListener` now also implements `IBusinessEventSink` - ONE ordered buffer for security and business events. A business event is stamped at the moment it is reported with the signed-in user (ICurrentUser) and the time, then written in the BACKGROUND: the action never waits, a store that is down keeps the event (bounded, 500) and it is written in order with the next write. An event reported while some transaction held the lock would simply be written after it.
- **Audited actions** (module / action): pos `sale.completed` (after the checkout commit, before the peripherals); sales `sale.cancelled`; payments `payment.voided`; inventory `stock.adjusted`; pricing `price.created` / `price.changed` (old -> new) / `price.deactivated`; catalog `product.price-changed` (only when the sale price actually changed); purchasing `purchase-order.received`; cash-management `cash.shift-opened`, `cash.pay-in` / `cash.pay-out`, `cash.shift-closed` (expected, counted, difference). Cash sales are covered by `sale.completed` (the drawer movement comes through the contract). A refused or failed action leaves no entry.
- **Not possible / not chosen**: refunds/returns (no flow exists); "no sale" drawer opens, product create/deactivate, barcode and purchase-order changes (not chosen); a durable outbox (an entry can be lost only if the application stops while the audit store is unavailable; logged).
- **Rules**: ARCH-RES-005 now also keeps audit reporting out of the checkout transaction.
- **Tests (+10)**: Audit +7 (stamping at report time, explicit actor kept, nobody signed in, buffer while down in order with security events, never waits, buffered before the store exists, cutting/redaction, missing or faulty sink); Integration +3 on the real host (all 12 actions -> exactly one entry each under its module naming the signed-in user, a name-only product edit leaves no price entry; refused/failed actions leave none; an audit table that refuses writes never fails the sale and the entry follows once it works, nothing lost or doubled). VerticalSliceOwnership: Audit is now a sixth owner of the checkout's changes and the sale entry is read back.
- **Real executable**: setup, refused checkout without a shift (no entry), open the drawer with 100, sell 5.00, pay out 3.00 "milk", close counted 101 -> Audit log screen: cash.shift-opened, pos sale.completed, cash.pay-out, cash.shift-closed, all by admin, in order; exit 0.

---

## FIX-06 Summary - Plain failure messages in every hosted module (2026-10-08)

Where an unexpected failure (database locked or unavailable, disk error, corruption) can surface, and what now happens:

- **Screens (all modules)**: already covered since FIX-01 - every screen action runs through `IUiActionRunner`, which logs the exception and shows the plain sentence of PresentationText.OperationFailed; view models cannot hold handlers (DesktopCompositionTests). FIX-06 proves it per module.
- **Module boundaries (new)**: the contract services other modules WRITE through now translate an unexpected failure into their own failed result with a plain sentence and log the details, like POSService: Inventory `IStockIssueService` / `IStockReceiptService` ("Inventory.OperationFailed"), Payments `IPaymentService` record and void ("Payments.OperationFailed"), CashManagement `ICashMovementRecorder` ("CashManagement.OperationFailed"). Inside the caller's transaction the failure is rolled back with everything else. Consequence: a purchase receipt or a checkout whose stock cannot be saved now returns a plain failed result naming the step ("Could not receive 'Cola': [Inventory.OperationFailed] ... Nothing was received ...") instead of an exception.
- **Readers** (product lookup, price resolver, the module readers) return data, not results: a failure there reaches the caller's boundary (POSService, the UI runner), which translates it. Not changed.
- **Tests (+10)**: Integration +4 (ContractFailureTests: stock issue/receipt, payment record/void, cash recorder each return a plain failed result with no database text and write nothing, then work again; a checkout whose stock movement cannot be saved tells the cashier plainly and keeps nothing); UI +6 (HostedScreensFailureTests on the offline desktop with a failure injected inside SQLite: Catalog product + category, Inventory warehouse + receipt + correction, Customers, Suppliers, Pricing price list, Purchasing order + a receipt whose stock cannot be saved, Cash drawer open + pay-out - each shows a plain sentence with no SQL/table/exception text and writes nothing). AtomicityFailureTests: the purchase receipt now asserts the plain failed result instead of an exception.
- **Real executable** (isolated database, triggers added to the running application's database file): adding a category and opening a drawer shift while the database refuses -> the plain sentence; after the trigger is dropped the same click saves; exit 0.
- **Not done here (owners)**: the message on screens is the generic sentence (it does not name the action); there is no log FILE yet to "check the application log" in (MISS-05).

---

## FIX-07 Summary - One DI scope per user action (2026-10-08)

The hazard (Stage 13): module read contracts TRACK what they read, so a DI scope that outlives one action reads stale data (and could carry a failed change into the next action). Option chosen (FIX-01 decision 1): enforce one scope per action in the shell; the readers keep tracking (no AsNoTracking change).

- **Already in place (FIX-01)**: every screen action runs through `IUiActionRunner` (a fresh async scope per action, disposed at its end); screen view models and the shell are singletons that may depend only on singletons (DesktopCompositionTests); the sign-in window, sign-out and the barcode input each open their own scope per action.
- **Enforced (new)**: an `IActionScope` refuses to hand out services once its action ended ("An action scope was used after its action ended..."), also after a failed action - a view model that kept it fails loudly instead of reading through stale, tracking contexts.
- **Guarded (new)**: every declared screen view model and the shell are created from the ROOT of the real desktop composition with scope validation on, which also catches a singleton that captured a scoped service further down (the direct constructor check cannot see that; a negative test proves the check fires). The real host does not validate scopes in Production.
- **Tests (+6, UI)**: the kept-scope refusal (after success and after failure); the root-composition check and its negative case; on the real offline desktop, the Products screen shows a price changed elsewhere at its next action, and the contrast - a scope kept across actions really reads the old price through IProductLookup while a fresh scope reads the new one.
- **Real executable**: Products screen open, the product's price changed directly in the database file (as another terminal would) -> the next Search shows 3.10 instead of 2.50; exit 0.

---

## FIX-08a Summary - Tax rates in Pricing (2026-10-08)

FIX-08 decisions (user, 2026-10-08): prices INCLUDE tax; tax rates live in Pricing (named rates, one default, optional rate per product); line and cart discounts with a permission and a maximum, spread over lines, audited; rounding per line to 2 decimals. Split into 08a (rates), 08b (tax at the till and in Sales), 08c (discounts).

- **Pricing.Domain**: `TaxRate` (code <= 30, upper case; name; Rate as a fraction 0..1 with at most 4 decimals = 0.01%; one default; active/inactive; the default cannot be deactivated) and `ProductTaxRate` (one row per Catalog product ID; no row = the default). `TaxSelection`: the product's own ACTIVE rate, else the active default, else none (no tax).
- **Persistence**: pri_TaxRates (unique code), pri_ProductTaxRates (key = product ID, same-module FK to the rate); migration AddTaxRates.
- **Application**: Create (the first rate becomes the default), Update (name, rate - a new law; past sales keep their snapshot), SetDefault, Deactivate (products that used it fall back to the default; the choice stays visible), SetProductTaxRate (SKU or ID; null = use the default); ListTaxRates, GetProductTax. Capability `pricing.tax.manage` (sensitive). Every change is audited (pricing tax-rate.created / changed / default-set / deactivated, product.tax-rate-set).
- **Contract**: `ITaxRateResolver.ResolveAsync(productId)` -> TaxResolutionResult (Found, Rate, TaxRateId, Code, Name); not used by the till until 08b.
- **Tax rates screen** (Inventory group, after Prices): add (rate typed as a percentage), change the selected rate, use as default, deactivate; find a product by SKU/barcode and choose its rate or "use the default", with the rate that applies now in words.
- **Tests (+14)**: Pricing +12 (domain limits, selection fallback, first-rate default, own rate and back, another default, change and deactivate with fallback, plain refusals); UI +2 on the offline desktop (the owner sets up STD 14% and ZERO 0%, gives a product ZERO, raises STD to 15% and the product back to the default - the till's resolver follows each step; bad percentages refused in plain words). Pricing infrastructure test now expects four tables.

---

## FIX-08b Summary - Tax at the till and in Sales (2026-10-08)

- **One rule** (`Platform.Core.Amounts.TaxInclusiveLine`, pure): Gross = unit price x quantity; Total = Gross - discount (what the customer pays, tax included); Tax = Total x rate / (1 + rate); Net = Total - Tax; every amount rounded per line to 2 decimals, half away from zero; a discount is capped at Gross. POS and Sales both compute their lines with it, so the cart, the payment, the drawer and the sale cannot differ by a cent.
- **Sales**: `SaleItem` now treats UnitPrice and Discount as tax-included amounts: LineTotal = the rule's Total, TaxAmount = the tax contained, SubTotal = Net (before tax). Sale.GrandTotal / TaxTotal / SubTotal are the sums of the rounded lines. Existing sales all have rate 0, so nothing recorded before changes. Four Sales tests were rewritten from the old "tax added on top" math to the decided one.
- **POS**: a cart line snapshots its tax rate when added (optional `ITaxRateResolver` from Pricing: the product's own rate, else the default; no Pricing or no rate = 0); adding more of the same product keeps the first snapshot; a rate outside 0..100% is refused. Cart: Subtotal (before discounts), TaxTotal (contained), Total (payable). Column pos_CartItems.TaxRate (migration AddCartItemTaxRate, default 0 for open carts). Checkout hands each line's snapshot rate to Sales. Contracts: POSCartItemResult +TaxRate/TaxAmount, POSCartResult +TaxTotal (optional, at the end).
- **Receipt**: `ReceiptDocument.Taxes` (per rate, the sum of the rounded line taxes; zero-rate lines print nothing); the ESC/POS formatter prints "incl. tax 14%  0.61" under TOTAL. **POS screen**: "incl. tax" amount next to the total.
- **Tests (+14)**: POS +11 (the line rule incl. rounding and the discount cap; out-of-range rate refused; no Pricing = no tax; per-line snapshots, shelf price total and contained tax; a rate change after adding and a merge keep the first snapshot; checkout hands the rate to Sales; the receipt's taxes per rate); Hardware +1 (tax lines printed after TOTAL, none without tax); Integration +2 on the real host (STD 14% + ZERO bread: cart 9.90 with 0.92 tax, payment 9.90, drawer 59.90, sale 9.90/0.92/8.98 with the 14% snapshot kept after the rate becomes 20%, the next cart gets 20%; no rates = no tax).
- **Not done here**: the Sales history screen and the Business overview do not show tax yet (the data is there); discounts are 08c.

---

## FIX-08c Summary - Discounts at the till (2026-10-08)

- **POS.Domain**: `DiscountRule` (Percent up to 100 or a positive tax-included Amount, kept AS GIVEN and priced every time: a percentage follows quantity changes and new scans, an amount is never more than what it applies to); `CartDiscountAllocation.Spread` (in proportion to the lines after their own discounts, rounded to 2 decimals, the rounding cent to the largest line, shares add up exactly). PosCartItem keeps its line discount; PosCart keeps the cart discount and exposes `PricedLines` - each line's charged amounts (own discount + cart share) under the one TaxInclusiveLine rule; Subtotal (before discounts), DiscountTotal, TaxTotal, Total come from them. Clearing the cart removes the cart discount; a cart discount needs products.
- **Application**: SetLineDiscount / SetCartDiscount (value 0 removes) need the new capability `pos.discount.give` (sensitive) and respect `PosDiscount:MaximumPercent` (default 100 = only "not more than the price"; an amount is measured against what it applies to); plain refusals (AboveMaximum, MoreThanTheAmount, not in the cart). Audited: pos discount.line-given / line-removed / cart-given / cart-removed; sale.completed names the discount.
- **Checkout / Sales**: each line goes to Sales with its total discount and its rate snapshot; Sales computes the same amounts with the same rule. **Receipt**: the line shows its price before the discount with "discount -x" under it; "Subtotal" and "Discount" before TOTAL when there is a discount; tax after discounts.
- **Contracts**: IPOSService.SetLineDiscountAsync / SetCartDiscountAsync, POSDiscountKind; POSCartItemResult +Discount/LineDiscountKind/Value; POSCartResult +DiscountTotal/CartDiscountKind/Value (all optional at the end). Persistence: nullable pos_CartItems.LineDiscountKind/Value, pos_Carts.CartDiscountKind/Value (migration AddDiscounts).
- **POS screen**: Discount box, % / Amount, "On selected line", "On whole cart" (shown only to people holding pos.discount.give; 0 removes), a Discount column and the discount total.
- **Tests (+17)**: POS +14 (rule limits and pricing, the spread, line + cart discounts lowering total and tax, percentage following quantity and amount capped, cart discount needs products and is cleared, through IPOSService to Sales line by line, 0 removes, maximum / more than the line / not in the cart refused, no permission no discount, receipt lines and tax after discounts); Hardware +1 (discount, subtotal and discount lines); UI +2 (controls hidden without the permission; on the offline desktop: STD 14%, 10% line + 0.50 cart -> 8.50 incl. 1.04, the sale records 8.50 / 1.04 / 7.46 with a 1.50 line discount, three audit entries).
- **Real executable** (local license server, receipt printer = EscPosFile in the scratch folder, maximum 50%): Tax rates screen adds STD 14%; 4 x 2.50 -> 10.00 incl. 1.23; 10% on the line -> 9.00 incl. 1.11; 0.50 on the cart -> 8.50 incl. 1.04; 90% refused ("A discount can be at most 50% here."); checkout "Sale completed: 8.50."; the printed receipt shows 4 x 2.50 10.00, discount -1.50, Subtotal 10.00, Discount -1.50, TOTAL 8.50, incl. tax 14% 1.04; exit 0.
- **Not done here (owners)**: the Sales history screen and the Business overview do not show tax or discounts (the data is recorded); automatic promotions, coupons, buy X get Y and customer discounts (optional Discounts & Promotions module, later); refunds (no flow yet).

---

## FIX-09a Summary - Partial-quantity receiving (2026-10-08)

FIX-09 decisions (user, 2026-10-08): receive per delivery with a quantity per line (adding up, never above what was ordered), "Partly received" until complete, and close short with a reason; supplier returns attach to a received purchase order (from its received quantities and warehouse, at its unit cost, reason required, audited); a new sensitive capability `purchasing.return.create`. Split into 09a (part deliveries) and 09b (returns).

- **Purchasing.Domain**: `PurchaseOrderLine.ReceivedQuantity` adds up the deliveries (ReceivedAt = when the whole quantity has arrived; IsReceived/HasReceipts/OutstandingQuantity/ReceivedTotal derived); `PurchaseOrder.ReceiveLine(line, quantity)` refuses more than is still expected ("Only 7 of 'Water 1.5L' are still expected; 8 cannot be received."); `UpdateReceivingStatus` (Received when every unit arrived, else PartiallyReceived once anything arrived); `CloseShort(reason)` only for a partly received order (a placed order with nothing received is cancelled instead); Cancel is refused once anything arrived ("Close it short instead."). New statuses PartiallyReceived = 5 and Closed = 6 (numbers stored, never renumbered). `ReceivedAmount` (received quantity x unit cost) is kept on the order like TotalAmount, so summaries never load lines.
- **Persistence**: pur_PurchaseOrderLines.ReceivedQuantity, pur_PurchaseOrders.ReceivedAmount / ClosedAt / ClosingReason (migration AddPartialReceiving). Backfill: a received line gets its full quantity, a Received order its total; an order the pre-Stage-12 resumable receiving left Submitted with received lines becomes PartiallyReceived with the value of those lines. Down maps 5 -> Submitted and 6 -> Received.
- **Application**: `ReceivePurchaseOrderCommand(OrderId, WarehouseId, Lines?)` with `ReceiveLineQuantity(LineId, Quantity)`; null = everything still outstanding (the earlier behaviour; every existing caller unchanged). The whole delivery is checked against the order BEFORE any stock moves (negative, duplicate line, nothing entered, unknown line, more than still due - plain refusals); still one transaction with Inventory (Stage 12). `ClosePurchaseOrderShortCommand` needs purchasing.order.cancel (its description now mentions closing short); not audited, like cancelling. The audit entry of a delivery is unchanged (purchase-order.received, n line(s)).
- **Contracts**: PurchaseOrderStatusContract +PartiallyReceived/Closed; PurchaseOrderLineResult +ReceivedQuantity; PurchaseSummaryResult +PartiallyReceived/Closed (all optional at the end). ReceivedValue is now what really arrived (every delivery) and OpenValue what is still to come on drafts and orders awaiting goods. **Reporting** keeps its four buckets: partly received counts as awaiting goods, closed short as received ("Draft / awaiting goods / received or closed / cancelled").
- **Purchase orders screen**: statuses "Partly received" and "Closed short" (also as filters); the lines show Received, Still due and an "Arrived" box per line that starts at what is still due (change it for a part delivery; an empty box = nothing of that line); Receive delivery works for placed and partly received orders; Close short (with the reason) for partly received ones; the reason row is hidden once the order is finished. Column widths and header padding adjusted so the new columns fit the default window.
- **Tests (+12)**: Purchasing +10 (domain: deliveries adding up, more than due refused without change, nothing on a draft or a finished order, close-short rules, cancel refused after a part delivery; application: part delivery then the rest, a delivery with one line over is refused before any stock moves, plain validation, close short and no more goods, summary values; upgrade test: a database of the first schema with a received order and a half-received one migrates with the right quantities, values and statuses); UI +1 on the offline desktop (5 of 12, 8 refused, "two" refused, 2 more, close short -> stock 17, order closed with its reason); Reporting +1 (folding of the new statuses). One test changed: the resumable fallback now leaves a part receipt PartiallyReceived instead of Submitted.
- **Real executable** (local license server, isolated database): first-run setup, activation, warehouse, category, unit, supplier, product; order of 12 x 1.20 placed; 5 arrive -> "Partly received", Still due 7, Arrived prefilled 7; 8 refused; 2 more; Close short with "supplier ran out" -> "Closed short", Still due 0, no Arrived box; Stock 7; Business overview 0 / 0 / 1 / 0, still to receive 0.00, received 8.40. A second order (24 water + 10 juice, 24 and 6 arrive): one line complete, one with 4 due; screenshot checked after the column fix.
- **Not done here**: supplier returns (09b); a delivery note number or date per delivery (each delivery is a separate stock movement with the order number and line in its reference); receiving more than ordered (not chosen).

---

## FIX-09b Summary - Supplier returns (2026-10-08)

- **Purchasing.Domain**: `SupplierReturn` aggregate (number RT-yyyyMMdd-XXXXXX; the order, its number, the supplier and the order's receiving warehouse; reason <= 500, required; total) with `SupplierReturnLine` (the order line it comes from, product snapshot, quantity, the order's unit cost). `PurchaseOrderLine.ReturnedQuantity` / `ReturnableQuantity` (= received - returned); `PurchaseOrder.RecordReturn(return, line, quantity)` changes the line and the return together or refuses ("Only 4 of 'Orange juice' can be returned (received 6, already returned 2)."). A return needs received goods (any status with receipts, including closed short) and never changes the order's status.
- **Persistence**: pur_SupplierReturns (unique number; same-module FK to the order, Restrict), pur_SupplierReturnLines (cascade), pur_PurchaseOrderLines.ReturnedQuantity (migration AddSupplierReturns).
- **Application**: `ReturnToSupplierCommand(OrderId, Reason, Lines)` with capability `purchasing.return.create` (sensitive); every line is checked before any stock moves; the stock leaves through Inventory.Contracts `IStockIssueService` (refused when the warehouse no longer holds enough - "Nothing was returned") with the return number, order and line in the movement reference; one transaction with Inventory (IAtomicOperation; ARCH-RES-004 now names this handler too); without a transaction the lines that left stock are kept on the return. Audited after the commit: purchasing supplier-return.created ("Return RT-...: 1 line(s) worth 1.20 sent back to Fresh Water Co from order PO-.... Reason: cracked bottle"). `ListSupplierReturnsQuery(OrderId)`.
- **Contracts**: PurchaseOrderLineResult +ReturnedQuantity (optional at the end).
- **Purchase orders screen**: Returned column; for people holding the new permission and an order with something left to return: return the selected line (quantity, reason); the order's returns are listed (number, time, lines, value, reason). The Line total column was dropped so the lines fit beside the order list (quantity x unit cost; the order total stays in the list).
- **Tests (+9)**: Purchasing +8 (domain: a return at the order cost from the order's warehouse, never more than received minus returned, needs goods / a reason / its own order, allowed on a closed-short order; application: two lines leave stock with the return number, more than left refused before stock moves, plain validation, the fallback keeps only what left stock; the authorization matrix and the sensitive flag; the infrastructure test expects four tables); UI +1 on the offline desktop (3 of 12 returned -> stock 19, the return listed with its reason; after 18 are sold Inventory refuses a return of 2 and nothing is kept; the audit entry; 10 refused with "Only 9"); Architecture: ARCH-RES-004 asserts the return handler.
- **Real executable** (the 09a database, upgraded in place by AddSupplierReturns): the return controls were hidden at first - the administrator was created before the capability existed; after ticking "Return goods to suppliers" on Roles and permissions they appeared. 2 juice returned from the partly received order (4.00), 5 more refused; 1 water returned from the closed-short order (1.20); Stock 30 water / 4 juice; Audit log shows supplier-return.created with the reason.
- **Found (not fixed here)**: first-run setup grants the Administrator role the capabilities that exist at that moment; capabilities added by later versions (FIX-08's pos.discount.give and pricing.tax.manage, this purchasing.return.create) must be ticked by hand on an upgraded installation. Flagged as a separate task.
- **Not done here (owners)**: supplier credit notes / money back (Purchasing has no payables); returns in the Business overview; the screen returns one line at a time (the command takes several).

---

## FIX-10 Summary - Split payments at the till (2026-10-09)

Decisions (user, 2026-10-09): the cashier adds payment parts and checks out once they cover the total, everything in the one checkout transaction, parts kept on the screen only; only cash gives change and only the cash kept goes into the drawer; card parts take an optional note, "Other" a required description; no terminal integration.

- **Contracts**: `IPOSService.CheckoutWithPaymentsAsync(cartId, payments)`; `POSPaymentRequest.Amount` (what the part pays; null = the whole total for the single-payment CheckoutAsync, which is unchanged); `POSCheckoutResult.PaymentIds` (PaymentId stays the first). `ReceiptDocument.Payments` + `AllPayments` (Platform hardware abstraction; the ESC/POS formatter prints every part).
- **Checkout** (`CheckoutCartCommandHandler`): the single payment becomes one part for the total; the parts are checked before anything is written (POS.Checkout.PaymentAmountInvalid / TenderedOnlyForCash / MethodDetailRequired / TenderInsufficient / PaymentsDoNotMatchTotal - "The payments add up to 9.99; 0.01 of the total 20.00 is still due."); each part is one Payments record for the sale; the change is the sum of the cash change; the drawer shift receives only the cash parts (kept cash, not tendered); any cash part needs the open shift (FIX-04) and opens the drawer; hosts without a transaction void every recorded part on a later failure. Audit: "paid by card 5.00 + cash 2.50".
- **Receipt**: a split payment prints one line per part ("Card (approval 4711)  5.00", "Cash  2.50") with Tendered and Change under the cash; a single payment prints as before.
- **POS screen**: Pay with (Cash / Card / Other), Amount (prefilled with what is still due; for cash what is handed over), Note, Add payment (Enter in the amount box); the parts are listed with Remove; Still due and Change; Checkout waits until the parts cover the total; no parts = the whole total in cash; a change of the cart clears the parts with a sentence; the completed message names the change.
- **Tests (+11)**: POS +6 (card + other + cash with change recorded per part against the sale; parts short of / above the total refused before anything is written; every rule in plain words; the fallback voids recorded parts when a later part or the stock issue fails); Hardware +1 (a split payment prints every part, then tendered and change); UI +4 (view model: card then cash with change and checkout sends every part; card above due, other without description and a bad amount refused; remove a part and a cart change clears them; offline desktop with every module: card 6.50 + cash 3.50 of 5.00 -> two payments, drawer 53.50, stock down).
- **Real executable** (local license server run from a copy of its binaries, receipt printer = EscPosFile): Cash drawer shift opened with 50.00; 3 x 2.50 = 7.50; Card 5.00 "approval 4711" -> still due 2.50, Checkout disabled; Cash 10.00 -> change 7.50; "Sale completed: 7.50. Change: 7.50."; receipt TOTAL 7.50, Card (approval 4711) 5.00, Cash 2.50, Tendered 10.00, Change 7.50; drawer 52.50; a 6.00 card part on a 5.00 sale refused; a sale without parts paid in cash (drawer 57.50); audit "paid by card 5.00 + cash 2.50".
- **Not done here**: parts are not kept with an open cart across sign-out/restart (decided); refunds to the original payment methods (no refund flow yet); card terminal integration.

---

## FIX-11 Summary - Customer on a sale (2026-10-09)

Decisions (user, 2026-10-09): anyone who may sell finds and attaches an existing active customer, seeing code and name only; no customer creation at the till; the customer is shown in Sales history.

- **Sales**: `Sale.CustomerId` (plain Guid, no FK) with `CustomerCode`/`CustomerName` - a snapshot taken at the sale (rule 14); `AssignCustomer` only on a draft (ID, code <= 30, name <= 200 required). `ISalesService.CreateSaleAsync(reference, notes, customer, ct)` with the new contract record `SaleCustomer`; `SaleSummaryResult` +CustomerId/CustomerName, `SaleDto` +CustomerId/Code/Name (GetSaleById now uses the shared mapper). Sales never calls Customers. Migration AddSaleCustomer (index on CustomerId).
- **POS**: `PosCart.CustomerId/Code/Name` + `SetCustomer` (open carts; null removes); kept with the cart, so a resumed till still has its customer (migration AddCartCustomer). `IPOSService.FindCustomersAsync(text)` (at least 2 characters; ACTIVE customers whose code, name, e-mail or phone contains the text; `POSCustomerResult` = ID, code, name only) and `SetCustomerAsync(cartId, customerId?)` (the customer must exist and be active), both needing pos.sale.create only. Customers is OPTIONAL for POS (new reference POS.Application -> Customers.Contracts; resolved only when installed): without it the till says "Customers are not available on this installation" and sells as before. Checkout passes the cart's snapshot to Sales. POSCartResult +CustomerId/Code/Name.
- **POS screen**: a Customer row above the cart - the attached customer and Remove customer; a search box (Enter or Find customer): one match is attached at once, several are offered in a list (Choose), none says customers are added on the Customers screen; the next customer's cart starts without one. List items announce the customer's name to screen readers (found in the real exe: they announced the record with its ID).
- **Sales history**: Customer column; "Customer: C-001 Jane Doe" next to the selected sale's lines.
- **Tests (+12)**: Sales +3 (the snapshot through ISalesService and both readers; an incomplete or too long customer refused with no sale created; only on a draft); POS +6 (a cashier without customers.customer.view finds active customers by name or phone and sees ID/code/name only; the customer is stored with the cart and reaches Sales as the snapshot; removing sells without one; unknown/inactive/too short refused; without Customers the till says so and still sells; without pos.sale.create nothing is found or set); UI +3 (one match attached and removed; several to choose from and none in plain words; offline desktop: customer by phone, the till resumed in a new screen with the customer, sold, Sales history row and detail).
- **Real executable** (the FIX-10 database upgraded in place): Customers screen creates C-001 Jane Doe (with phone and e-mail) and C-002 Janet Smith; on the till "jan" offers both (code and name only), Jane Doe chosen; the app closed and restarted - the till's cart still has "C-001 Jane Doe"; removed and chosen again; sold 2.50; Sales history shows the Customer column "C-001 Jane Doe" and "Customer: C-001 Jane Doe" for the selected sale; the two earlier sales have no customer.
- **Not done here**: the customer on the printed receipt and in the sale.completed audit entry (not chosen); a customer's purchase history and reports by customer; customer prices or discounts (optional Discounts & Promotions module).

---

## FIX-12 Summary - Sales report: every sale, local days (2026-10-09)

- **Sales.Contracts**: `ISalesReader.GetCompletedBetweenAsync(fromUtc, toUtc)` -> `CompletedSaleResult(SaleId, CompletedAt UTC, GrandTotal, TaxTotal)`: every sale COMPLETED in the inclusive range, oldest first, no count limit (read-only, lines loaded because the totals are computed from them). Index on sal_Sales.CompletedAt (migration AddSaleCompletedAtIndex). GetRecentAsync is unchanged for its other callers.
- **Reporting**: the sales report (and the Business overview's Sales card) reads the range through it - the 2000-sale scan window (`MaxSalesScanned`) is gone and `IsTruncated` is always false (kept in the contracts for earlier callers). `SalesCalculator.Calculate(facts, range, zone)` groups by the shop's local calendar day (`TimeZoneInfo.ConvertTimeFromUtc`, daylight saving included); the zone is the computer's, from `TimeProvider.LocalTimeZone` (injectable; tests use fixed zones). Without a zone the calculator keeps UTC days.
- **Business overview screen**: lists every day of the chosen period again (empty days included) - the "only days with sales" workaround of FIX-01d is removed.
- **Tests (+2 net)**: Reporting: the three scan-window tests are replaced by: 5000 sales in a month all counted and never truncated; a UTC+2 shop - a sale at 23:30 UTC on 1 January counts on 2 January, one at 22:15 UTC on 31 December on 1 January; a zone with daylight saving (UTC+1/+2) puts summer and winter sales near midnight on the right local days. Sales +2 on SQLite (2050 completed sales in one range plus a draft and a cancelled one -> exactly the 2050, oldest first, UTC kind; the range is inclusive at both ends to the tick). UI: the offline-desktop overview test now expects every day of the period and today's sale on the computer's local date.
- **Real executable** (computer in Egypt Standard Time, UTC+3 in October; the FIX-11 database with two sales moved in SQLite to 20:30 and 21:30 UTC on 8 October): Business overview 1-9 October lists nine days; 8 October: 1 sale 7.50 (23:30 local), 9 October: 2 sales 7.50 (00:30 local and the afternoon sale); UTC days would have shown 2 sales / 10.00 on 8 October.
- **Not done here**: a shop time-zone setting separate from the computer's (not needed: the till runs in the shop); the Sales history screen still lists at most 1000 sales per period (its totals are of the listed sales); export of reports (LATER-07).

---

## FIX-13a Summary - Receipts with Arabic printed as a picture (2026-10-09)

FIX-13 decisions (user, 2026-10-09): every screen text in Arabic with right-to-left layout, business-rule messages stay English for now; Claude drafts the Arabic and the user reviews it; the language is chosen per user (applied at sign-in; the sign-in window follows the installation default); receipts with Arabic print as a picture. Split into 13a (receipts), 13b (language per user, sign-in texts in resources), 13c (translations, right-to-left checks).

- **Platform abstraction**: `IReceiptImageRenderer.Render(lines, widthDots)` -> `MonochromeImage` (1 bit per dot, rows MSB first); `ReceiptImageLine(Left, Right, Bold, Center, Rule)` - the printer-independent layout of a receipt line. The hardware abstraction test now counts the five devices plus this one helper.
- **Client.Hardware** (still no WPF): `EscPosReceiptFormatter.Layout` builds the receipt's lines once (the same content as before); `Format` sends them as ASCII text, or - when any text is outside printable ASCII and a renderer is present (`ReceiptImageMode.Auto`) - as a raster picture with GS v 0 in bands of 128 rows. `Always` / `Never` force one or the other; without a renderer the old '?' text remains. `EscPosReceiptPrinter` falls back to text if drawing fails (logged). Settings `Hardware:ReceiptPrinter:PrintAsImage` (Auto) and `DotsPerLine` (576 for 80 mm, 384 for 58 mm).
- **Client.Desktop**: `WpfReceiptImageRenderer` (registered as the renderer): Arial at width/24 dots, one row per line; a text's direction comes from its first strong letter; a right-to-left text without an amount is right-aligned, amounts end at the right edge, centred lines stay centred, rules are solid; aliased text, thresholded to black and white; drawn on its own STA thread (printing runs in the background). Found while testing: right-to-left text is aligned inside its layout box, so the box is narrowed to the text.
- **Tests (+16)**: Hardware +8 (an Arabic product turns the whole receipt into one picture of the laid-out lines; ASCII stays text; Always/Never; no renderer = '?' text; banding 128/128/44 and a 58 mm width; a failing renderer still prints text; the factory reads the settings); UI +8 (the real renderer: paper width and one row per line; Arabic right / English left; an amount at the right edge and a centred line centred; a full-width rule; direction by first letter).
- **Real executable**: product "مياه معدنية ١٫٥ لتر" (WATER-AR, 3.25) created on the Products screen, received, sold 2 with 1 "Water 1.5L"; the EscPosFile receipt (32,872 bytes) decoded into a 576 x 456 picture: the Arabic name joined and right-aligned over "2 x 3.25 / 6.50", the rest as before.
- **Not done here**: receipt labels in Arabic (they follow a receipt language later); printer code pages (not chosen); store name/header in Arabic were not exercised in the real run (same path as product names).

---

## FIX-13b Summary - Screen language per user (2026-10-09)

- **Users**: `User.Language` (a culture name such as "ar", stored lower case, at most 10 characters; null = the installation's language; migration AddUserLanguage). `SetUserLanguageCommand(UserId, Language?)`: everyone may set their OWN, another user's needs users.manage; `GetUserLanguageQuery(UserId)`: own, or another's with users.view. UserDto +Language.
- **Platform.Presentation**: `UiLanguages` (English, العربية - each named in itself; `Find("ar-EG")` -> Arabic). `UiCulture` reworked (a defect found in the real executable): the display language is kept in `UiCulture.Current` and set on the static `Culture` of every generated resource class (found by reflection; assemblies loaded later included), because .NET restores a thread's culture when the async method that changed it returns - screens opened after a language change read the old texts. `ApplyFormatting` sets the number/date culture once from the installation (amounts are typed and shown the same in Arabic and English).
- **Desktop**: `DesktopSession` (replaces DesktopSignOut): `ShowSignIn` (the start screen in the installation's language, Ui:Culture), `OpenShellAsync` (reads the signed-in user's language - a failure only falls back to the installation's - applies it and opens a NEW shell window, closing the previous one; a window's texts are read when it is built), `SignOutAsync`, `ChangeMyLanguageAsync`. App asks the session for the start screen, then the shell (ARCH-SEC-017 follows the flow into DesktopSession). Header: a language chooser next to Sign out. Users screen: "Screen language" (the installation's / English / العربية), saved with the details. Sign-in window: texts moved to `SignInText.resx`, reading direction from the language. The receipt renderer shapes text culture-independently.
- **Tests (+14)**: Users +6 (a cashier sets their own language but not another's, and cannot read another's; an administrator sets another's, seen on the user; invalid codes refused); UI +8 (the Users screen saves Arabic and back to the installation's on the offline desktop; UiLanguages.Find; a language chosen inside an async method stays for screens opened later and is on the resource classes - the regression test of the real-exe defect; the screen language does not change number format). Also fixed: a blocking wait in a FIX-13a hardware test (xUnit1031 warning).
- **Real executable**: header language "العربية" -> the shell window is rebuilt right to left (menu on the right, Sign out and the chooser on the left; PrintWindow captures right-to-left windows mirrored, so the screenshot was flipped to check); sign-out -> the start screen in English, left to right; signing in again -> right to left; "English" -> left to right. Texts stay English until 13c.

---

## FIX-13c Summary - Arabic screens (2026-10-09)

- **Translations**: every one of the 524 screen texts (shell, start screen, license, and the 12 module UIs + Platform.Presentation's groups and messages) in Modern Standard Arabic, one `X.ar.resx` next to each `X.resx` (satellite assemblies `ar\*.resources.dll`; the neutral English files are unchanged). Placeholders and formats ({0}, {1:N2}, {2:+0.00;-0.00;0.00}) are kept exactly; a test fails if a text has no Arabic twin or a different placeholder.
- **Right-to-left fixes found by opening all 17 screens in Arabic in the real executable**: the shell status bar showed the license state as its enum name ("Active") - it now uses the License screen's words; "Signed in as Test Admin (admin)" and the Users heading lost their closing parenthesis inside Arabic text (WPF ignores the LRE/PDF embedding characters - a trailing left-to-right mark U+200E fixes it); phone numbers showed their digit groups reversed ("123 555 0100") - the phone columns put a left-to-right mark before the value and the phone/e-mail boxes are always typed left to right; the POS product/discount row and the Users search row wrap instead of running off the window (the POS row was also clipped in English).
- **Terms for the user's review** (drafted, not yet checked by a native speaker): till = "الصندوق" (open/close till: فتح/إغلاق الصندوق); takings = "الإيرادات"; opening float = "رصيد الافتتاح"; pay in / pay out = "إيداع / سحب"; SKU = "رمز الصنف"; close short = "إغلاق بنقص" (status "مُغلق بنقص"); ordered (placed purchase order) = "مطلوب"; business overview = "نظرة عامة على النشاط"; audit log = "سجل التدقيق"; sensitive (permission) = "حساسة"; application title = "منصة نقاط البيع"; change (money back) = "الباقي"; still due = "المتبقي".
- **Tests (+17)**: UI +17 (every resource file has a complete Arabic twin with the same placeholders - 16 files; choosing Arabic shows Arabic texts of the shell, a module and the groups, and English again).
- **Real executable**: the administrator's language Arabic; all 17 menu entries opened and captured (PrintWindow mirrors right-to-left windows: the captures were flipped); after the fixes the header reads "مسجَّل الدخول باسم Test Admin (admin)", the status bar "جاهز - الترخيص: نشط", customers' phones "0100 555 123".
- **Not done here (user decision)**: the sentences business rules return (refusals, errors, audit summaries) and the capability names/descriptions on the Roles screen stay English - a follow-up item; receipt labels stay English; long English data in narrow columns is clipped on the left in Arabic (as it is on the right in English).

---

## MISS-04a Summary - Local backup core (2026-10-10)

- **Design** (approved by the user 2026-10-10, `MISS-04_BACKUP_DESIGN.md`): recovery code held by the shop plus vendor escrow for CLOUD backups only; local backups are plain SQLite files (lost-USB risk accepted, to be stated on screen); local backup in `Client.Backup` for every shop, cloud in a paid optional `CloudBackup` module; separate sensitive `backup.configure`; cloud token typed in by the owner; no backup on shift close.
- **New project `Client.Backup`** (Client layer, like Client.Updater; registered by `ClientBackupHostingModule` in DesktopComposition): `BackupService` makes a backup (SQLite online backup of the live database opened read-only, the copy switched to the rollback journal so it is one self-contained file, `PRAGMA integrity_check`, SHA-256, migration list from the shared `__EFMigrationsHistory`), writes it as `name.part` then renames it into the chosen folder (`genericpos-<local time>.db`, never overwrites: `-2` suffix), records it in the history, and keeps the newest N of THAT folder (default 14; backups elsewhere and other files are never touched). Verify reads the file back, compares size and fingerprint and checks the database; the result is kept. Delete removes file and entry (a missing drive is reported, not mistaken for a deleted file). One operation at a time: a second backup is refused in plain words.
- **Files next to the database, not in it** (`<database folder>\Backup`): `history.json`, `settings.json` (atomic writes; an unreadable file is kept aside, never deleted), `staging\` (emptied at every start by `BackupInitializer`). Restoring a backup (04b) can therefore never erase the record of backups. Configuration section `Backup` (Root, LocalFolder, KeepLocal) only presets; the settings belong to `backup.configure`.
- **Capabilities** `backup.create` (make, verify, history, see settings), `backup.restore`, `backup.delete`, `backup.configure` (sensitive except create); none needs a license (rule 10). Handlers check them before anything is read, copied or removed. Audit events (module `backup`): backup.created, backup.expired, backup.verified, backup.verify-failed, backup.deleted, backup.settings-changed.
- **Plain failures**: no folder chosen; no shop data yet; folder not writable (checked with a test file when it is chosen, and again at backup time); a damaged database is never turned into a backup ("Your data was not changed"); a changed/damaged or missing backup file ("If it is on a USB drive, connect the drive").
- **Tests (+45)**: Backup.Tests (new, 36: copy content/journal/fingerprint/migrations, uncommitted writes excluded and the live database keeps working, no temporary copy left, not configured, no database, unwritable folder, damaged database, same-second names, busy, retention per folder, history survives restart and unreadable history kept aside, staging swept, verify pass/changed/missing/not-a-database/unknown, delete, settings defaults/persist/off/invalid/unwritable/working folder, capabilities, every handler checks its capability first, composition with scope validation); Integration +2 (offline desktop: administrator backs up after a sale, backup holds the sale, verify passes, selling continues, no network request, audited under the administrator; not configured explained); Architecture +7 (ARCH-BAK-001..007).
- **Real executable**: on isolated folders the desktop starts with backup registered, removes a leftover temporary copy at start, reaches the sign-in screen and exits 0 with no error lines. A backup cannot be made from the real executable yet: the Backup screen is MISS-04d.
- **Not done here (later steps)**: restore (04b), scheduler and shell notice (04c), the Backup screen (04d), CloudBackup with encryption/recovery code/escrow (04e), the vendor escrow tool (04f). The Administrator role of an EXISTING installation does not get the new backup.* capabilities automatically (known upgrade gap, FIX-09b).

---

## MISS-04b Summary - Restore across a restart (2026-10-10)

- **New host hook `IStartupPreparation`** (Client.Host): `GenericApplicationHost` runs every registered preparation after the host is built and BEFORE `StartAsync`, i.e. before the platform's database initializer (the first hosted service) or any module opens the database. A preparation handles its own expected failures; an escaping exception stops the start with the usual plain message.
- **`RestoreService`** (Client.Backup.Application), all steps need `backup.restore`: Prepare (from the history, or a loose file from another PC/USB) copies the backup into `<db folder>\Backup\restore\` with its fingerprint and checks it: same fingerprint as in the history, a readable database, a backup of THIS application (has migrations), and not from a newer version (a migration the live database does not have = "Update this PC first"; older backups are fine, the module migrations bring them up to date). Nothing changes. Confirm records `restore-pending.json` (who confirmed, the staged file's fingerprint) and audits `backup.restore-requested` in the current data. Cancel takes a prepared or confirmed restore back before the restart. Status query (backup.create): pending restore + last outcome. Restore shares one operation gate with backups.
- **`PendingRestoreStep`** (the swap, at the next start): the staged file must still match its fingerprint; the database file and its -wal/-shm/-journal are MOVED together into `Backup\before-restore\before-restore-<local time>.db` (moving them together keeps writes that were still only in the WAL); the backup is moved into place. Any failure moves everything back (never deletes anything) and the outcome says so in plain words ("in use - is another copy open?"). The pending marker is always cleared (a failed restore is not retried behind the person's back). Afterwards the before-restore copy is folded into one self-contained file and listed in the history (Destination `before-restore`, never removed automatically), so a wrong restore is undone by restoring it. `restore-outcome.json` is reported once by `BackupInitializer` to the audit log of the RESTORED data (`backup.restored` / `backup.restore-failed`) under the person who confirmed.
- **After a restore** everyone signs in with the users and passwords stored in the backup; licensing files are not part of a backup and are unaffected.
- **Defects found while testing and fixed before commit**: a staged file that could not be read would have escaped as an exception and stopped the start; a failure to create the before-restore folder or to write the history after the swap could have stopped the start or left the marker for a second run. All now end in a plain outcome.
- **Tests (+18)**: Backup +16 (whole way incl. rows only in the WAL kept in the before-restore copy, undo by restoring the before-restore copy, no restore = nothing changes and unconfirmed copies removed, database in use = nothing moved, staged backup changed = not restored, newer refused / older accepted, loose file from another PC, garbage/foreign/relative/missing refused, changed history backup refused, only the latest preparation confirmable once, cancel, outcome audited once under the confirming person, every step needs backup.restore); Integration +1 (offline desktop: sale, backup, sale, confirm; next start on the same files has the backup's sales, the before-restore copy has both, `backup.restored` audited under the administrator, the administrator is signed in and the shop sells on); Architecture +1 (ARCH-BAK-008: the swap is a startup preparation, run before the hosted services, working on files only).
- **Real executable** (isolated folders): first run created the database; a restore was staged next to it (python: online backup copy + `restore-pending.json`) and a table was then added to the live database; the second run logged "Shop data restored from backup ...", the live database no longer has that table, the before-restore copy has it and is listed in the history, the marker and staged copy are gone, the outcome is marked reported; both runs exit 0 with no error lines.
- **Not done here**: the application restarting itself after the confirmation and the confirmation sentence belong to the Backup screen (MISS-04d); until then a confirmed restore happens at the next start.

---

## MISS-04c Summary - Scheduled backups and the shell notice (2026-10-10)

- **Schedule in the settings** (backup.configure): `BackupSettings` gained `ScheduleEnabled` (default on) and `DailyAt` (default 23:00, local time); settings saved before MISS-04c read as daily at 23:00. Configuration presets `Backup:ScheduleEnabled`, `Backup:DailyAt` ("HH:mm"), `Backup:SchedulerStartDelaySeconds` (default 60).
- **The rule** (`BackupSchedule.IsDue`, pure): due when the schedule is on, a folder is chosen and no good LOCAL backup (scheduled or by hand) exists since the latest scheduled time (today's once passed, else yesterday's). The same rule is the catch-up: a PC that was off at 23:00 backs up shortly after it starts. After a failed attempt (scheduled or by hand) it waits 30 minutes before trying again, so a missing USB drive does not mean a failure every minute.
- **The scheduler** (`BackupScheduler`, a hosted BackgroundService; `ScheduledBackupRunner` decides): first look after the start delay, then every minute; runs in the background (SQLite online backup, a sale never waits); steps aside while a backup by hand runs (that one counts); nothing it meets stops the application. No Windows Task Scheduler, no service account; no backup on shift close (user decision 7). Everything it needs is in the settings, history and `status.json`, so a restart never loses or repeats a scheduled backup.
- **Last attempt** recorded in `Backup\status.json` (time, origin, success, plain reason); failed attempts are audited (`backup.failed`). Scheduled work is audited as `scheduled backup` (ActorName set, no ActorId), never as whoever is signed in; work by hand keeps the signed-in person.
- **Shell notice** (`IBackupNoticeSource`, implemented by BackupService; `ShellViewModel.BackupNoticeText`, right side of the status bar, English + Arabic): "No backup folder has been chosen, so the shop's data is not being backed up." for holders of backup.configure; "The last backup (time) failed: reason" for holders of backup.create or backup.configure; a later successful backup clears it. It follows backup changes live (`Changed` event, marshalled to the UI thread); cashiers without backup permissions see nothing; sign-out clears it. The reason text stays English (business messages stay English, FIX-13 decision).
- **Tests (+26)**: Backup +20 (scheduled-time rule; 12 due/not-due cases incl. catch-up, retry pause, custom time, schedule off, no folder; once per day and audited as the application's; failure noticed, paused, retried, cleared by success; steps aside for a backup by hand; the background service makes the due backup and stops; notice and change events; a failure by hand also noticed; schedule settings persisted and old settings read as 23:00); UI +4 (notice for configure holders, failure for create holders, nothing for cashiers, follows changes live and sign-out clears it); Integration +1 (real composition with a preset folder: the scheduler makes the missing backup in the background while the shop sells, audited as `scheduled backup`, no network); Architecture +1 (ARCH-BAK-009).
- **Real executable** (isolated folders, folder preset to a path blocked by a file, scheduler delay 0): the log shows the scheduled backup due and failing plainly; after first-run setup through UI Automation the shell's status bar shows "The last backup (10/10/2026 3:44 PM) failed: The backup could not be written to ...\usb\Backups. Check that the drive is connected and has free space. Your data was not changed." (screenshot checked); closing exits 0.
- **Not done here**: the Backup screen (MISS-04d) - until then the folder and schedule are preset by configuration or changed through the handlers only; a long folder path makes the notice wrap to three lines in the status bar.

---

## MISS-04d Summary - The Backup screen (2026-10-10)

- **Administration > Backup** (`Client.Desktop/Screens/Backup`, declared in `DesktopScreens`, needs backup.create, never license-locked): last backup, folder, daily schedule, the plain-file warning (design decision 2), the last restore's outcome; "Back up now"; the list (made, file, where - backup folder / kept before a restore -, size, how, check result) with Check, Restore..., Restore from a file..., Delete...; settings (folder with a folder dialog, how many to keep, daily backup on/off and time). Parts the person may not use are hidden (backup.restore / delete / configure read through IAuthorizationService.IsAllowedAsync); every action goes through its handler in its own scope.
- **Asked first, in a sentence**: Restore shows "Everything entered after <date> will be replaced by the backup <file>. A copy of the current data is kept, and afterwards everyone signs in with the users and passwords stored in the backup." with Restore / Cancel; Delete shows "Delete the backup <file>? This cannot be undone." with Delete / Keep it. A confirmed restore shows "... happens when the application restarts" with **Restart now** and Cancel the restore; the screen opened again still knows a restore is waiting.
- **Restart** (`IApplicationRestarter` / `DesktopRestarter`): starts a new copy of the executable (same arguments and environment) and closes this one; the new copy is told the old process id (`GENERICPOS_RESTART_AFTER_PROCESS`) and `RestartHandoff.WaitForPreviousInstance` (first thing in App.OnStartup) waits up to 60 s for it to end, so two copies never hold the data at once and the pending restore finds the database free.
- **Texts**: `Resources/BackupText.resx` + `.ar.resx` (56 texts; Arabic drafted, awaiting the user's review with the FIX-13c terms). Dates, sizes, file names and the English business sentences are wrapped in left-to-right marks (they read reversed in Arabic otherwise - found in the Arabic real-exe capture). The backup folder and time boxes are always typed left to right.
- **Tests (+14, UI)**: on the real Client.Backup services and a real database file: screen declaration; the owner backs up and checks; restore asks with the sentence, waits for the confirmation, offers the restart (fake restarter), a reopened screen still sees the pending restore and can cancel it; a file from another PC can be chosen; delete asks first and Keep changes nothing; settings checked on the screen (time, count) and by the handler (full path); someone with only backup.create sees no restore/delete/settings; without backup.create nothing is read; the restart hand-off waits for the previous process and ignores unknown/own/invalid ids. The generic composition tests cover the new view model (singleton-only dependencies, scope validation) and its XAML.
- **Real executable** (isolated folders, UI Automation): first-run setup; the shell notice said no folder is chosen; on the Backup screen the folder and 22:30 were saved ("Settings saved.", notice gone), "Back up now" made a backup, Check said it is complete; data was then added from outside; Restore showed the sentence, Restore confirmed it, **Restart now** closed the app (exit 0) and a new copy started at the sign-in screen with the backup's data (the added table gone, kept in the before-restore copy); signed in again (the owner's password from the backup), the screen listed the backup and the before-restore copy and the last-restore sentence; the screen was also captured in Arabic (right to left, readable dates and sizes). No fail/crit log lines.
- **Defect found and fixed**: the before-restore copy was labelled "By hand" under "How" (now "-"); dates/sizes reversed in Arabic (now left-to-right marks).
- **Not done here**: the cloud parts of the screen (MISS-04e); the business sentences inside the screen (refusals, the last-restore outcome) stay English (FIX-13 decision).

---

## Known Issues / Blockers

None blocking. Stage 13 and Part A (FIX-01..FIX-15) are complete; MISS-04a..04d (local backup) are complete.
Arabic translations are drafts until reviewed by a native speaker (FIX-13c Summary lists the terms to check).
Upgrade gap (found in FIX-09b): an Administrator role created before a capability existed does not get it automatically; an administrator must tick it on Roles and permissions.
Observed once (2026-10-07, FIX-01b final run): the Cloud.Tests test host crashed with "Internal CLR error (0x80131506)" while all 23 test projects ran in parallel; Cloud.Tests then passed 207/207 three times in a row on its own. Not reproduced; watch for it in later full runs.
Build: 0 errors, 0 warnings (122 projects, verified with `dotnet build --no-incremental`).
All 2739 tests pass (MISS-04d: UI 209; MISS-04c: Backup 72, UI 195, Integration 143, Architecture 381; MISS-04b: Backup 52, Integration 142, Architecture 380; MISS-04a: Backup 36 (new project), Integration 141, Architecture 379; FIX-13c: UI 191; FIX-13b: Users 174, UI 174; FIX-13a: Hardware 109, UI 166; FIX-12: Sales 110, Reporting 39; FIX-11: Sales 108, POS 190, UI 158; FIX-10: POS 184, Hardware 101, UI 155; FIX-09b: Purchasing 66, UI 151; FIX-09a: Purchasing 58, UI 150, Reporting 39; FIX-08c: POS 178, Hardware 100, UI 149; FIX-08b: POS 164, Sales 105, Hardware 99, Integration 139; FIX-08a: Pricing 56, UI 147; FIX-07: UI 145; FIX-06: Integration 137, UI 139; FIX-05: Audit 43, Integration 133; FIX-04: Integration 130, UI 133; FIX-03: Updater 218, Integration 123; FIX-02: UI 131, Hardware 98; FIX-01e: UI 110, Users 168; FIX-01d: CashManagement 39; FIX-01c: Catalog 79, Inventory 99, Sales 105; POS 153; Architecture 372; Stage 13 figures follow) (Architecture 370, Cloud 207, Updater 209, Licensing 185, Users 163, POS 150, Integration 122, Platform.ModuleContract 121, Sales 103, Hardware 97, Inventory 97, Catalog 70, Security 55, Purchasing 48, Pricing 44, Customers 43, Suppliers 41,
Reporting 38, CashManagement 38, Audit 36, Payments 33, Platform.Infrastructure 27). Stage 12 had 2254; Stage 13 added 43 (Architecture +22 new and -6 placeholders removed, Platform.ModuleContract +9, Integration +18).
Stage 13 limitations: see "Stage 13 Summary - Remaining limitations".
Stage 12 limitations: see "Stage 12 Summary" (no real process kill, full disk, physical device, UI automation or load campaign; only POS translates unexpected failures into plain results).
Not defects but known gaps (see the Stage 11 deferred-limitations register): backup encryption not built (MISS-04). The user-administration and license-activation screens were added in FIX-01e.
Resolved during Stage 9: stress-running Cloud.Tests exposed rare random failures (about 1 run in 8, different tests each time, SQLite connection-open errors). Cause: the test teardown called the
process-wide `SqliteConnection.ClearAllPools()` while other tests ran in parallel. Fix: test databases use `Pooling=False` (no global pool clearing); staging-file cleanup in the file stores also
gained short retries (a briefly locked file never fails an upload) and stale `.part` files are swept at start. 30 consecutive full Cloud.Tests runs passed afterwards.
(Earlier statuses for reference: Stage 9 had 1633 tests/115 projects; Stage 8 had 1428 tests/108 projects.)
Catalog, Inventory, Sales, POS and every Stage 8 module (except Reporting, which has no tables) apply their own migrations at startup.
Database: %LOCALAPPDATA%\GenericPOS\genericpos.db (Platform + all module tables in the same file).

Remaining limitations after Stage 8 (deferred work, none of it is a Stage 8 requirement):
- AUDIT IS NOT ADOPTED **[Superseded in FIX-05; see "FIX-05 Summary"]**: no module records to Audit yet. Adoption means giving a module an optional `IAuditRecorder` constructor parameter (the POS/Pricing pattern); it was left out to keep Stage 1-7 modules untouched.
- USERS IS NOT AUTHENTICATION: no passwords, credentials, sessions or sign-in; permission codes are stored but no module checks them; the POS cashier is still a free-text reference not linked to a Users record.
- CASHMANAGEMENT IS NOT CONNECTED TO POS/PAYMENTS **[Superseded in FIX-04 for POS cash sales; see "FIX-04 Summary"]**: cash sales are not recorded into a drawer session automatically; `ICashMovementRecorder` is ready (idempotent per reference) for a later optional integration.
- CUSTOMERS/SUPPLIERS: a sale does not carry a customer; the Catalog product has no supplier link; no credit, loyalty or statements.
- PURCHASING: whole-line receiving only (no partial quantities, no supplier returns, no cost update back to Catalog); a failed multi-line receipt is resumable but not rolled back (no stock-reversal contract exists). **[Superseded in Stage 12: checkout and purchase receive now run as ONE SQLite transaction (IAtomicOperation); see Stage 12 Summary.]**
- PRICING: price lists with effective periods and quantity breaks only; no customer-specific prices, promotions, discounts, tax or currency; POS still has Total == Subtotal.
- PAYMENTS: records only (no gateway, no hardware, no refunds beyond voiding); POS pays the full cart total with one method (split payments exist in the Payments API but are not offered by POS). **[Superseded in FIX-10: the till takes split payments; see "FIX-10 Summary".]**
- REPORTING: minimal reports; the sales report scans at most 2000 recent sales (Sales.Contracts only exposes a recent list) and flags IsTruncated; no export, scheduling or caching.
- The Stage 8 UI projects are minimal view models, not hosted in MainWindow, and (net10.0-windows) not covered by Architecture.Tests; module feature entitlements are not enforced (licensing was not enforced when this was written; Stage 11 enforces MODULE entitlements through capabilities - FEATURE-level entitlements still have no consumer).
- Everything listed under "Remaining limitations after Stage 7/6/5D" still applies, except that Payments and Users now exist as modules (the 5D line "no Payments module" is superseded by the Stage 8 integration).

Remaining limitations after Stage 7:
- RUNTIME ADOPTION IS NOT WIRED: an Activated update changes the active pointer and leaves a verified, deployed version on disk, but nothing yet loads from
  `installed/<target>/<version>`: there is no launcher that starts the active core version, and ModuleHost still uses the modules compiled into the app
  (its file-system discovery scans `<AppBase>/modules`, not the updater's directories). Until that integration exists, installing an update does not change
  what runs. The host must also call `UpdateService.ConfirmHealthyAsync` after a healthy start [done in FIX-03: StartupHealthConfirmation, only for versions that really run] (not yet wired in App.xaml.cs; unconfirmed updates are
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
- (Stage 7 now reuses the Stage 6 trust model through Security.Es256; licensing was unchanged by Stage 7; Stage 11 enforces it centrally in AuthorizationService.)

Remaining limitations after Stage 5D:
- No payment processing (no Payments module).
- No stock reversal contract: partial stock issue during a failed checkout needs manual correction. **[Superseded in Stage 12: checkout and purchase receive now run as ONE SQLite transaction (IAtomicOperation); see Stage 12 Summary.]**
- No distributed transaction across modules (see decision 4). **[Superseded in Stage 12: checkout and purchase receive now run as ONE SQLite transaction (IAtomicOperation); see Stage 12 Summary.]**
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

Last updated: 2026-10-09 - FIX-13 complete (receipts with Arabic, language per user, Arabic screens checked right to left in the real executable). 2636 tests, 0 warnings, 122 projects; next: FIX-14/15 (documentation) or Part B; Stage 14 not started.
