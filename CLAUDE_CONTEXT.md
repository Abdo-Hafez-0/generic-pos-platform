# CLAUDE_CONTEXT.md

Practical onboarding snapshot for future Claude sessions. `PROJECT_STATE.md` remains the formal state document.
Repository source code is the source of truth; last updated 2026-10-05 when Stage 10 was completed.

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
6 Licensing (foundation), 7 Update System (foundation), 8 Additional Business Modules, 9 Cloud Services & Administration, 10 Hardware & Device Integration. Roadmap:
11 Security Hardening, 12 Offline/Failure Testing, 13 Architecture/Integration Verification, 14 Packaging, 15 Production Readiness. Never skip ahead.

## 10. Current stage
Stage 10 (Hardware & Device Integration) is COMPLETE. Next roadmap stage is 11 (Security Hardening) - only when instructed. Do not start it unprompted.

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

**Stage 10 (Hardware & Device Integration):** peripherals are OPTIONAL infrastructure behind five vendor-neutral abstractions in `Platform.Application/Abstractions/Hardware` (`IBarcodeScanner`, `IReceiptPrinter`, `ILabelPrinter`, `ICashDrawer`, `IScale` + `HardwareErrors`/`HardwareGuard`, results use `Platform.Core.Result`). Adapters live ONLY in the new `Client.Hardware` (referenced only by `Client.Desktop`'s composition root; `HardwareHostingModule`, `HardwareFactory` is the single place that maps config `Hardware:*:Type` to an adapter): keyboard-wedge scanner, ESC/POS receipt printer + cash drawer, ZPL label printer, over TCP or device-path transports, plus Null placeholders (all devices default to `None`). No scale adapter exists (no generic protocol). POS consumes devices through NULLABLE ctor parameters: checkout saves the sale first, THEN prints/kicks the drawer, and hardware problems become `POSHardwareNotice`s on the successful `POSCheckoutResult` (never an exception, never a rollback); `IPOSDevices` (reprint, label, drawer, weight, status) and `IPOSBarcodeInput` (scans go through `AddProductAsync`) are the POS contracts. Fakes with Works/Unavailable/Timeout/Throws modes are in `tests/Tests.Common/Hardware`. 1794 tests (97 new Hardware.Tests, ARCH-HW-001..014). NO physical device was ever tested; nothing feeds `IKeyboardInputSink` until PosView is hosted in MainWindow. Decisions, config, limitations: PROJECT_STATE.md "Stage 10 Summary".

**Stage 9 (Cloud Services & Administration):** the vendor side of the platform, all OPTIONAL for the desktop. New: `Cloud.Contracts` (ApiError, ServiceResult, DTOs), `Cloud.Infrastructure` (the ONLY server project with EF Core: `CloudDbContext` over the SERVER's own SQLite database, WAL, migration `InitialCloudSchema`, file stores for package + backup bytes, `Add*` composition helpers), `AdminPortal.{Application,Api}` (customers, licenses, module registry, package publish/withdraw, backups view, audit, dashboard; static hashed API keys; NOT the Users module), `BackupServer.{Application,Api}` (opaque backups, per-license bearer token, retention; read/restore survives suspension and expiry). LicenseServer/UpdateServer gained durable stores (opt-in via `CloudDatabase:ConnectionString`; defaults and Stage 6/7 behavior unchanged). Four hosts, one server DB. Activation keys and backup tokens are stored only as SHA-256. Licenses save with an optimistic `RowVersion`. 1633 tests (187 `Cloud.Tests`, 315 architecture incl. ARCH-CLD-001..016, 34 integration incl. proof the POS path runs with no server assembly loaded). NOT built: client-side CloudBackup module/IBackupClient, browser UI, SSO/roles, billing, backup encryption (Stage 11). Decisions, config keys, endpoints, limitations: PROJECT_STATE.md "Stage 9 Summary".

**Stage 8 (Additional Business Modules):** nine independent five-layer modules with their own table prefix, DbContext and migration - Customers (cus_), Suppliers (sup_), Purchasing (pur_; needs Catalog/Suppliers/Inventory contracts), Pricing (pri_; needs Catalog.Contracts; IPriceResolver), Payments (pay_; records only, no gateway), Users (usr_; identity + roles + permission codes, NO authentication), Audit (aud_; append-only), CashManagement (cash_; drawer sessions, idempotent cash movements), Reporting (no tables; read-only reports over OPTIONAL read contracts, missing source = Unavailable section). Cross-module access is Contracts-only; soft dependencies are nullable constructor parameters, not manifest entries. POS was extended minimally and optionally: Pricing resolves the cart price, `CheckoutAsync(..., POSPaymentRequest?)` records a payment through Payments (void + cancel on later failure). Inventory gained `IStockReceiptService` for Purchasing. `IModuleManifest` is unchanged. 1428 tests (299 architecture, 32 integration against the real host + real SQLite). Decisions, tables, graph and limitations: PROJECT_STATE.md "Stage 8 Summary".

## 12. Solution structure
`GenericPOS.sln`: `src/Platform/{Platform.Core,Contracts,Application,Infrastructure}`, `src/Client/{Client.Host,ModuleHost,Desktop,Licensing,Updater}`,
`src/Modules/{Catalog,Inventory,Sales,POS,Customers,Suppliers,Purchasing,Pricing,Payments,Users,Audit,CashManagement,Reporting}/<Module>.{Domain,Application,Contracts,Infrastructure,UI}` (117 projects total),
`tests/{Architecture,Platform.Infrastructure,Platform.ModuleContract,Catalog,Inventory,Sales,POS,Licensing,Customers,Suppliers,Purchasing,Pricing,Payments,Users,Audit,CashManagement,Reporting,Integration}.Tests` + `tests/Tests.Common` (shared in-memory SQLite helper); `src/Licensing/Licensing.Contracts`, `src/Client/Client.Licensing(.Http)`, `src/Cloud/LicenseServer/*`, `src/Security/Security.Es256(.Signing)`, `src/Updates/Updates.{Contracts,Package}`, `src/Client/Client.Updater(.Http)`, `src/Cloud/UpdateServer/*`, `tools/{ModulePackager,UpdatePublisher}`, `tests/Updater.Tests`; Stage 9: `src/Cloud/{Cloud.Contracts,Cloud.Infrastructure}`, `src/Cloud/AdminPortal/*`, `src/Cloud/BackupServer/*`, `tests/Cloud.Tests`; Stage 10: `src/Client/Client.Hardware`, `tests/Hardware.Tests`, `Platform.Application/Abstractions/Hardware`.
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
`feat(hardware): ...` Stage 10 commit (see `git log -3`); before it the `feat(cloud): ...` Stage 9 commit, and before that the `feat(stage8): ...` commits and the Stage 8 integration tests + documentation commit. Before Stage 8: `60a8123`-era `feat(update): implement secure update system`.

## 20. Exact next task
None assigned. Wait for the technical lead. Next roadmap stage: Stage 11 (Security Hardening). Follow-ups (not in any stage): launcher/ModuleHost
adoption of activated updates + calling ConfirmHealthyAsync, license enforcement points, IModuleMigrator in the business modules, the client-side CloudBackup module
(IBackupClient talking to BackupServer.Api), a browser UI for AdminPortal, stock-reversal contract, authentication on top of Users (Stage 11),
adopting Audit/CashManagement/Customers from POS and Sales, hosting view models in MainWindow.

## 21. Known limitations / inconsistencies
- Payments exist (Stage 8E) but only record payments (no gateway/hardware); POS records one payment for the full total, only when asked and only if the module is installed.
- No stock-reversal contract; a failed checkout that already issued some lines needs manual stock correction.
- No distributed transaction across module DbContexts.
- PosView exists but is not hosted in MainWindow; POS has no discounts/tax (Total == Subtotal).
- WPF UI projects are not covered by architecture tests (net10.0-windows TFM gap).
- Licensing is not enforced anywhere yet; no clock-rollback protection. The license server is durable and vendor-administered only when `CloudDatabase:ConnectionString` is configured (otherwise in-memory, Development only); activation key is its only customer credential.
- Updates verify/stage/activate but nothing loads the activated versions yet (no launcher; ModuleHost uses compiled-in modules); ConfirmHealthyAsync is not called by the host; the update server is unauthenticated (publishing goes through AdminPortal.Api when the durable catalog is configured, otherwise it serves a package directory).
- Stage 8 limitations: Audit is adopted by no module; Users has no authentication and nothing enforces permissions; CashManagement is not fed by POS/Payments; sales carry no customer; Purchasing receives whole lines only; Reporting's sales report scans at most 2000 recent sales (IsTruncated).
- Stage 9 limitations: no desktop client calls AdminPortal.Api or BackupServer.Api (CloudBackup module not built); admin authentication is static API keys; backups are opaque, protected by a per-license bearer token, no encryption yet; SQLite server database (single node; the docs name no server DB - decision to confirm); one installation per license; no vendor billing/support/telemetry areas.
- Stage 10 limitations: no real device was tested (ESC/POS, ZPL and keyboard-wedge are verified against fakes, loopback and device-path files); no scale adapter, spooler/serial/USB/vendor adapters, non-ASCII receipts or customer displays; scanner key presses are not forwarded yet (PosView not hosted); reprints carry no payment lines; drawer opens are not permission-checked (Stage 11).
- Test baseline: 1794 tests (Architecture 329, Hardware 97, Cloud 187, Catalog 64, Inventory 92, Platform.Infrastructure 19, Platform.ModuleContract 112, Sales 103, POS 138, Licensing 109, Updater 168, Customers 38, Suppliers 38, Purchasing 45, Pricing 41, Payments 29, Users 50, Audit 26, CashManagement 34, Reporting 34, Integration 41).

## 22. Rules for future Claude sessions
1. Read PROJECT_STATE.md, this file, and the 3 architecture docs before acting.
2. Inspect `git status` first; uncommitted work may be legitimate — never discard it.
3. Implement only the task given; stop after reporting.
4. Respect Contracts-only cross-module rules; never reference other modules' Infrastructure/UI/Domain.
5. Follow the Catalog/Inventory pattern exactly; keep Domain free of EF/WPF.
6. Report test/build failures honestly; don't mark unfinished work complete.
7. Update this file when the project state changes materially.
