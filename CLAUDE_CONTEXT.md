# CLAUDE_CONTEXT.md

Practical onboarding snapshot for future Claude sessions. `PROJECT_STATE.md` remains the formal state document.
Repository source code is the source of truth; last updated 2026-10-04 when Stage 7 was completed.

## 1. Project purpose
Generic, modular, offline-first Inventory & POS platform meant to serve many business types (retail, wholesale,
warehouse, pharmacy, food, restaurants, manufacturing...). Goal is a clean extensible architecture, not just a working POS.

## 2. Technology stack
C#, .NET 10 (`net10.0`; `net10.0-windows` for WPF projects), WPF desktop, SQLite, EF Core 10.0.11,
xUnit tests. Modular monolith. GitHub: https://github.com/Abdo-Hafez-0/generic-pos-platform.git

## 3. Architecture principles
Module independence, clear ownership, offline operation, testability, replaceability, future licensing/updates/cloud.
Local SQLite is the operational source of truth. Cloud is a later concern. Do not implement future modules unless told.

## 4. Layering rules
UI -> Application -> Domain; Infrastructure -> Application -> Domain.
Domain must not depend on Infrastructure, EF Core, SQLite, WPF, ASP.NET, HTTP.

## 5. Module dependency rules
Modules talk only through `<Module>.Contracts`. A module must not reference another module's Infrastructure, UI,
DbContext, repositories, or tables. Existing allowed cross-module refs: Inventory.Application -> Catalog.Contracts;
Sales.Application -> Catalog.Contracts + Inventory.Contracts.

## 6. Module ownership
Each module owns its tables (prefixes: `cat_`, `inv_`, `sal_`) and its own migrations (migration assembly = the module's Infrastructure).

## 7. Database architecture
One SQLite file `%LOCALAPPDATA%\GenericPOS\genericpos.db`; one DbContext per module (PlatformDbContext, CatalogDbContext,
InventoryDbContext, SalesDbContext). Each module has an `IHostedService` database initializer running migrations at host start.

## 8. Offline-first rules
App must work with no internet. No cloud calls in the operational path.

## 9. Implemented stages (committed)
Stage 1 Foundation, 2 Client Host, 3 Database Foundation, 4 Module Contract, 5A Catalog, 5B Inventory, 5C Sales, 5D POS,
6 Licensing (foundation), 7 Update System (foundation). Roadmap: 8 Additional Business Modules, 9 Cloud, 10 Hardware,
11 Security Hardening, 12 Offline/Failure Testing. Never skip ahead.

## 10. Current stage
Stage 7 (Update System) is COMPLETE as a foundation. Next roadmap stage is 8 (Additional Business Modules) - only when instructed.

## 11. Stage results worth knowing
**Stage 5C (Sales):** Sale/SaleItem/Return/ReturnItem/SalesTransaction; ISalesService/ISalesReader; `sal_` tables, migration
`InitialSalesSchema`; CompleteSale only records a SalesTransaction (no stock, no payments). 103 tests, ARCH-SAL-001..016.

**Stage 7 (Update System):** signed, verified, recoverable updates for core and modules. Package = `.gpkg` ZIP (`manifest.json` signed envelope +
`payload/`), hashed per file + PayloadHash, signed with ES256 (the SAME primitive as licensing, shared in `src/Security/Security.Es256`; signing lives
in `Security.Es256.Signing`, referenced only by LicenseServer.Infrastructure and UpdatePublisher). `Updates.Contracts` (PackageManifest, PackageType
Core/Module, discovery DTOs, error codes), `Updates.Package` (format, safe reader, deterministic writer, shared ManifestRules). `Client.Updater`
(no HTTP/EF/WPF/signing/business/Client.Licensing): `PackageVerifier` = 14-step pipeline that mutates nothing; `UpdateService` = check / download /
install (stage -> optional migration with DB restore point -> deploy side by side -> ATOMIC `active.json` pointer switch) / recover / confirm /
rollback; persisted `UpdateState` journal; `Client.Updater.Http` is the only HttpClient user. `UpdateServer.*` serves a directory of packages (no keys).
`tools/ModulePackager` validates+hashes before signing; `tools/UpdatePublisher` signs and writes. Migrations are module-owned: the updater only
orchestrates via `IModuleMigrator` (Platform.Application; none implemented yet, so migration is deferred to each module's startup initializer).
Binary rollback != database rollback: DB restore is explicit only (`RollbackAsync(target, restoreData: true)`).
**NOT wired yet (read before touching it):** no launcher/ModuleHost consumes the active pointers, so an Activated update does not change what
runs; `ConfirmHealthyAsync` is not called by the host yet; no auto discovery/UI; servers are in-memory/directory-based and unauthenticated.
168 updater tests, ARCH-UPD-001..022. Decisions: PROJECT_STATE.md "Stage 7 Architectural Decisions".

**Stage 6 (Licensing):** signed, offline-evaluated licensing. Shared wire model `Licensing.Contracts` (SignedLicense = Base64 payload +
ES256 signature). `Client.Licensing` (Domain/Application/Infrastructure namespaces; NO HTTP/EF/WPF/business modules) verifies with trusted
PUBLIC keys from `Licensing:TrustedKeys`, binds to a persisted installation GUID, evaluates `LicenseState` (Unlicensed, Active,
GracePeriod, Expired, Suspended, Revoked, Invalid) with `LicenseEvaluator`, stores `license.json`/`installation.json` under
%LOCALAPPDATA%\GenericPOS\Licensing. `Client.Licensing.Http` is the only HttpClient user (`ILicenseClient`). Server: `src/Cloud/LicenseServer`
(Application, Infrastructure with the private-key signer, Api with POST /api/licenses/activate|renew; in-memory store).
Business code sees only `Platform.Application...ILicenseEntitlementService`. NOTHING ENFORCES the license yet (no module/UI gates).
No keys are committed; tests generate ephemeral keys. 109 licensing tests, ARCH-LIC-001..018. Decisions: PROJECT_STATE.md "Stage 6 Architectural Decisions".

**Stage 5D (POS):** orchestration module. PosSession, PosCart, PosCartItem (snapshot SKU/name/price); IPOSService/IPOSReader;
`pos_` tables, migration `InitialPOSSchema`; handlers: OpenPosSession, ClosePosSession, StartCart, AddProductToCart,
RemoveProductFromCart, ChangeCartQuantity, ClearCart, CheckoutCart; queries GetPosSession, GetCart, GetCurrentCart.
POS.Application references Catalog.Contracts, Inventory.Contracts, Sales.Contracts ONLY.
Checkout: re-check stock -> Sales create/add(snapshot prices)/confirm -> Inventory `IStockIssueService` -> Sales complete -> cart
CheckedOut (stores SaleId). Added to Inventory (not Sales): `IStockIssueService`, `IssueStockCommandHandler`, `StockIssueService`.
84 POS tests, 8 new Inventory tests, ARCH-POS-001..020. Real-module end-to-end smoke test was run (scratch, not committed).
No Payments module exists; the payment step belongs between confirm and complete in `CheckoutCartCommandHandler`.
No cross-module transaction: see PROJECT_STATE.md "Stage 5D Architectural Decisions" #4 for partial-failure behaviour.
Handlers are plain classes with `HandleAsync` (Platform ICommand/IQuery abstractions exist but are unused by all modules).

## 12. Solution structure
`GenericPOS.sln`: `src/Platform/{Platform.Core,Contracts,Application,Infrastructure}`, `src/Client/{Client.Host,ModuleHost,Desktop,Licensing,Updater}`,
`src/Modules/{Catalog,Inventory,Sales,POS}/<Module>.{Domain,Application,Contracts,Infrastructure,UI}` (52 projects total),
`tests/{Architecture,Platform.Infrastructure,Platform.ModuleContract,Catalog,Inventory,Sales,POS,Licensing}.Tests`; `src/Licensing/Licensing.Contracts`, `src/Client/Client.Licensing(.Http)`, `src/Cloud/LicenseServer/*`, `src/Security/Security.Es256(.Signing)`, `src/Updates/Updates.{Contracts,Package}`, `src/Client/Client.Updater(.Http)`, `src/Cloud/UpdateServer/*`, `tools/{ModulePackager,UpdatePublisher}`, `tests/Updater.Tests`.
Docs at repo root: `Architecture & Solution Design.md`, `Generic Offline-First Inventory & POS Platform.md`,
`Module Map & Dependency Specification.md`, `PROJECT_STATE.md`.

## 13. Module pattern (canonical, from Catalog/Inventory)
Domain (entities, strongly-typed IDs, value objects, events) / Contracts (interfaces + DTOs for other modules) /
Application (commands, queries, repo interfaces, UoW, DTOs) / Infrastructure (DbContext, EF configs, internal repos,
contract implementations, `<X>Module : IModule`, `<X>ModuleManifest`, `<X>HostingModule : IHostingModule`,
`<X>DatabaseInitializer`, `Add<X>Module()`, Migrations, InternalsVisibleTo test assembly) / UI (ViewModels, net10.0-windows).
Registered in `Client.Desktop/App.xaml.cs` via hosting modules (Catalog, Inventory, Sales, POS in that order).
IHostingModule (DI at build time) and IModule (runtime lifecycle) are intentionally separate.

## 14. Important contracts
Platform.Core: `Result`, `Error`, `IModule`, `IModuleManifest`, `ModuleId`, `ModuleVersion`, `VersionRange`.
Catalog.Contracts: `IProductLookup`, `IProductBarcodeResolver`. Inventory.Contracts: `IInventoryReader`,
`IStockAvailabilityChecker`, `IStockMovementReader`. Sales.Contracts: `ISalesService`, `ISalesReader`.

## 15. Testing strategy
xUnit. Per-module test projects (domain, application integration on in-memory SQLite, infrastructure, contracts) plus
`Architecture.Tests` boundary rules (ARCH-INV-xxx style). Baseline (verified 2026-10-04): Architecture 155, Catalog 64, Inventory 89, Platform.Infrastructure 19,
Platform.ModuleContract 112, Sales 103, POS 84, Licensing 109, Updater 168 = 903 passing.

## 16. Git workflow
Task -> inspect -> implement only that task -> build -> test -> verify architecture boundaries -> update PROJECT_STATE.md ->
review git status/diff -> Conventional Commit -> push -> verify push -> verify clean tree -> report -> STOP.
Never force push, rewrite history, reset/clean unexplored work, commit secrets or bin/obj, or mix unrelated changes.
If push auth fails, stop and report. Commit trailer: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## 17. Branch
`master`, remote `origin` = https://github.com/Abdo-Hafez-0/generic-pos-platform.git

## 18. Git status
Clean after the Stage 5C commit is pushed (check `git status` at session start anyway).

## 19. Last relevant commit
`feat(update): implement secure update system` (see `git log -1`). Before it: `e0d777d feat(licensing): implement offline-first license management`.

## 20. Exact next task
None assigned. Wait for the technical lead. Next roadmap stage: Stage 8 Additional Business Modules. Follow-ups (not in any stage): launcher/ModuleHost
adoption of activated updates + calling ConfirmHealthyAsync, license enforcement points, IModuleMigrator in the business modules, Payments module,
stock-reversal contract.

## 21. Known limitations / inconsistencies
- No payments (no Payments module); checkout completes the sale and issues stock only.
- No stock-reversal contract; a failed checkout that already issued some lines needs manual stock correction.
- No distributed transaction across module DbContexts.
- PosView exists but is not hosted in MainWindow; POS has no discounts/tax (Total == Subtotal).
- PROJECT_STATE.md header still says "Cloud Backend ... Stage 6"; roadmap is 6 Licensing, 7 Updates, 9 Cloud.
- WPF UI projects are not covered by architecture tests (net10.0-windows TFM gap).
- Licensing is not enforced anywhere yet; no clock-rollback protection; license server is in-memory and unauthenticated.
- Updates verify/stage/activate but nothing loads the activated versions yet (no launcher; ModuleHost uses compiled-in modules); ConfirmHealthyAsync is not called by the host; update server is directory-based and unauthenticated.
- Test baseline: 903 tests (Architecture 155, Catalog 64, Inventory 89, Platform.Infrastructure 19,
  Platform.ModuleContract 112, Sales 103, POS 84, Licensing 109, Updater 168).

## 22. Rules for future Claude sessions
1. Read PROJECT_STATE.md, this file, and the 3 architecture docs before acting.
2. Inspect `git status` first; uncommitted work may be legitimate — never discard it.
3. Implement only the task given; stop after reporting.
4. Respect Contracts-only cross-module rules; never reference other modules' Infrastructure/UI/Domain.
5. Follow the Catalog/Inventory pattern exactly; keep Domain free of EF/WPF.
6. Report test/build failures honestly; don't mark unfinished work complete.
7. Update this file when the project state changes materially.
