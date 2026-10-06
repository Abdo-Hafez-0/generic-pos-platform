# CLAUDE_CONTEXT.md

Practical onboarding snapshot for future Claude sessions. `PROJECT_STATE.md` remains the formal state document.
Repository source code is the source of truth; last updated 2026-10-05 when Stage 11 was completed.

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
6 Licensing (foundation), 7 Update System (foundation), 8 Additional Business Modules, 9 Cloud Services & Administration, 10 Hardware & Device Integration, 11 Security Hardening. Roadmap:
12 Offline/Failure Testing, 13 Architecture/Integration Verification, 14 Packaging, 15 Production Readiness. Never skip ahead.

## 10. Current stage
Stage 11 (Security Hardening) is COMPLETE. Next roadmap stage is 12 (Offline/Failure Testing) - only when instructed. Do not start it unprompted.

## 11. Stage results worth knowing
**Stage 11 (Security Hardening) - rules every future change must respect:** (1) **Every mutating business handler authorizes first**: it takes `IAuthorizationService` (Platform.Application) and calls `AuthorizeAsync(<Module>Capabilities.X)` before doing anything; the answer is live (Users' `IPermissionProvider` is asked
on every call) and fails closed. ARCH-SEC-006 fails the build if a new `*CommandHandler` lacks it unless it is on the documented exception list; ARCH-SEC-007 does the same for the user/audit/report reads. A new module declares its capabilities in `<Module>.Application/Security/<Module>Capabilities.cs`
(`ICapabilityProvider`, registered in its `Add<M>Core`), codes are lower-case dotted (`pos.sale.create`). (2) **Trusted contract calls are not authorized twice**: a handler used by a person AND by another module's contract authorizes in `HandleAsync` and delegates to an `internal ExecuteAsync`
(InternalsVisibleTo the module's own Infrastructure); the contract service calls ExecuteAsync. (3) **License enforcement lives ONLY in `AuthorizationService`** (capability `LicenseRequirement.Module` vs `None`); modules never touch `ILicenseEntitlementService` (ARCH-SEC-005); there is no config switch to turn security off
(ARCH-SEC-015). Reading/exporting your own data, user/license/update administration are `None` so an expired license never strands the customer; a refusal never touches data. (4) **Authentication is in Users** (PBKDF2 hashes in usr_UserCredentials, lockout, `SignInCommandHandler` -> `ISessionManager`; `ICurrentUser` is identity only,
permissions are never cached); first run uses `BootstrapAdministratorCommandHandler` (only while no user exists). (5) **Security events**: producers call `ISecurityEventSink` with a `SecurityEvent` (no field for a secret; text is sanitised); the Audit module listener stores them as module "security". Never audit secrets.
(6) **Local protection**: `ISecretProtector` (Client.Security = Windows DPAPI) protects the installation identity and the clock mark; `ClockRollbackGuard` makes a turned-back clock restrict licensed work. (7) **Servers**: authentication throttle (429), HTTPS-only outside Development, security headers, shared adm_AuditLog; no private keys or hard-coded credentials in sources (ARCH-SEC-013/014).
(8) **Tests**: `TestModuleDatabase` and the per-module test databases register a permissive `IAuthorizationService`; security tests pass `ScriptedAuthorizationService` (Tests.Common/Security) or the real one; integration hosts with the Users module do the real first-run setup and sign in. 2125 tests after the review (Security.Tests 55 new, ARCH-SEC-001..017).
(9) **Desktop start screen** (review 2026-10-06): `App` shows `SignInWindow` (first-run setup / sign-in / forced password change) before `MainWindow` and exits when nobody signs in; the window is glue over `InteractiveSignInService` (ARCH-SEC-017). (10) **Read policy**: users, audit, reports and customers (`customers.customer.view`) are refused without the capability; product cost is omitted without `catalog.cost.view`; other operational reads are open to the signed-in operator. (11) POS sessions are attributed to the signed-in user. Decisions + the deferred-limitations register: PROJECT_STATE.md "Stage 11 Summary - Stage 11 review".
**Not built (deferred, with owners in that register):** user/role administration and license-activation screens, hosting module screens, backup encryption + the client CloudBackup module (backup.* capabilities belong there), device binding, automatic renewal.
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
Business code sees only `Platform.Application...ILicenseEntitlementService`. Since Stage 11 the license IS enforced, in ONE place (`AuthorizationService`, see the Stage 11 paragraph); modules still never query it.
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

**Stage 8 (Additional Business Modules):** nine independent five-layer modules with their own table prefix, DbContext and migration - Customers (cus_), Suppliers (sup_), Purchasing (pur_; needs Catalog/Suppliers/Inventory contracts), Pricing (pri_; needs Catalog.Contracts; IPriceResolver), Payments (pay_; records only, no gateway), Users (usr_; identity + roles + permission codes; Stage 11 added authentication: credentials, lockout, sign-in, and permissions that are now enforced), Audit (aud_; append-only), CashManagement (cash_; drawer sessions, idempotent cash movements), Reporting (no tables; read-only reports over OPTIONAL read contracts, missing source = Unavailable section). Cross-module access is Contracts-only; soft dependencies are nullable constructor parameters, not manifest entries. POS was extended minimally and optionally: Pricing resolves the cart price, `CheckoutAsync(..., POSPaymentRequest?)` records a payment through Payments (void + cancel on later failure). Inventory gained `IStockReceiptService` for Purchasing. `IModuleManifest` is unchanged. 1428 tests (299 architecture, 32 integration against the real host + real SQLite). Decisions, tables, graph and limitations: PROJECT_STATE.md "Stage 8 Summary".

## 12. Solution structure
`GenericPOS.sln`: `src/Platform/{Platform.Core,Contracts,Application,Infrastructure}`, `src/Client/{Client.Host,ModuleHost,Desktop,Licensing,Updater,Hardware,Security}`,
`src/Modules/{Catalog,Inventory,Sales,POS,Customers,Suppliers,Purchasing,Pricing,Payments,Users,Audit,CashManagement,Reporting}/<Module>.{Domain,Application,Contracts,Infrastructure,UI}` (120 projects total),
`tests/{Architecture,Platform.Infrastructure,Platform.ModuleContract,Catalog,Inventory,Sales,POS,Licensing,Customers,Suppliers,Purchasing,Pricing,Payments,Users,Audit,CashManagement,Reporting,Integration}.Tests` + `tests/Tests.Common` (shared in-memory SQLite helper); `src/Licensing/Licensing.Contracts`, `src/Client/Client.Licensing(.Http)`, `src/Cloud/LicenseServer/*`, `src/Security/Security.Es256(.Signing)`, `src/Updates/Updates.{Contracts,Package}`, `src/Client/Client.Updater(.Http)`, `src/Cloud/UpdateServer/*`, `tools/{ModulePackager,UpdatePublisher}`, `tests/Updater.Tests`; Stage 9: `src/Cloud/{Cloud.Contracts,Cloud.Infrastructure}`, `src/Cloud/AdminPortal/*`, `src/Cloud/BackupServer/*`, `tests/Cloud.Tests`; Stage 10: `src/Client/Client.Hardware`, `tests/Hardware.Tests`, `Platform.Application/Abstractions/Hardware`; Stage 11: `src/Client/Client.Security` (DPAPI), `src/Cloud/Cloud.Hosting` (shared server ASP.NET plumbing), `tests/Security.Tests`, `Platform.Application/Abstractions/{Authorization,Security}`.
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
The Stage 11 commits (`feat(security): ...`, `feat(licensing): ...`, `feat(updates): ...`, `feat(audit): ...`, `feat(cloud): ...`, `test(architecture): ...`; see `git log --oneline d4f6a05..`); before them the `feat(hardware): ...` Stage 10 commit (d4f6a05), the `feat(cloud): ...` Stage 9 commit, and before that the `feat(stage8): ...` commits and the Stage 8 integration tests + documentation commit. Before Stage 8: `60a8123`-era `feat(update): implement secure update system`.

## 20. Exact next task
None assigned. Wait for the technical lead. Next roadmap stage: Stage 12 (Offline/Failure Testing). Follow-ups (not in any stage): the user/role administration and license-activation screens and hosting the module view models in MainWindow (the start screen exists),
launcher/ModuleHost adoption of activated updates + calling ConfirmHealthyAsync, IModuleMigrator in the business modules, the client-side CloudBackup module (IBackupClient talking to BackupServer.Api; declares backup.* capabilities) and backup encryption (needs a key-management design),
a browser UI for AdminPortal, stock-reversal contract, adopting Audit/CashManagement/Customers from POS and Sales.

## 21. Known limitations / inconsistencies
- Payments exist (Stage 8E) but only record payments (no gateway/hardware); POS records one payment for the full total, only when asked and only if the module is installed.
- No stock-reversal contract; a failed checkout that already issued some lines needs manual stock correction.
- No distributed transaction across module DbContexts.
- PosView exists but is not hosted in MainWindow; POS has no discounts/tax (Total == Subtotal).
- WPF UI projects are not covered by architecture tests (net10.0-windows TFM gap).
- Licensing IS enforced since Stage 11 (central gate in `AuthorizationService`) and clock rollback is detected (restricting licensed work, never data); nothing in the UI shows the state yet. The license server is durable and vendor-administered only when `CloudDatabase:ConnectionString` is configured (otherwise in-memory, Development only); activation key is its only customer credential.
- Updates verify/stage/activate but nothing loads the activated versions yet (no launcher; ModuleHost uses compiled-in modules); ConfirmHealthyAsync is not called by the host; the update server is unauthenticated (publishing goes through AdminPortal.Api when the durable catalog is configured, otherwise it serves a package directory).
- Stage 8 limitations: Audit is fed by security events only (modules do not record business actions yet; Stage 11 resolved "Users has no authentication / nothing enforces permissions"); CashManagement is not fed by POS/Payments; sales carry no customer; Purchasing receives whole lines only; Reporting's sales report scans at most 2000 recent sales (IsTruncated).
- Stage 9 limitations: no desktop client calls AdminPortal.Api or BackupServer.Api (CloudBackup module not built); admin authentication is static API keys; backups are opaque, protected by a per-license bearer token, no encryption yet; SQLite server database (single node; the docs name no server DB - decision to confirm); one installation per license; no vendor billing/support/telemetry areas.
- Stage 11 limitations (all decided, see PROJECT_STATE.md "Stage 11 review"): no user-administration or license-activation screen yet (handlers exist); operational reads open to the signed-in operator by decision; backup encryption, client CloudBackup and device binding not built; trusted keys live in editable configuration (Stage 14); throttling per host/in memory.
- Stage 10 limitations: no real device was tested (ESC/POS, ZPL and keyboard-wedge are verified against fakes, loopback and device-path files); no scale adapter, spooler/serial/USB/vendor adapters, non-ASCII receipts or customer displays; scanner key presses are not forwarded yet (PosView not hosted); reprints carry no payment lines (drawer opens are permission-checked since Stage 11: pos.drawer.open).
- Test baseline: 2125 tests (Architecture 346, Cloud 207, Updater 182, Users 163, Licensing 155, POS 150, Platform.ModuleContract 112, Sales 103, Hardware 97, Inventory 97, Catalog 70, Security 55, Integration 48, Purchasing 48, Pricing 44, Customers 43, Suppliers 41, Reporting 38, CashManagement 38, Audit 36, Payments 33, Platform.Infrastructure 19).

## 22. Rules for future Claude sessions
1. Read PROJECT_STATE.md, this file, and the 3 architecture docs before acting.
2. Inspect `git status` first; uncommitted work may be legitimate — never discard it.
3. Implement only the task given; stop after reporting.
4. Respect Contracts-only cross-module rules; never reference other modules' Infrastructure/UI/Domain.
5. Follow the Catalog/Inventory pattern exactly; keep Domain free of EF/WPF.
6. Report test/build failures honestly; don't mark unfinished work complete.
7. Update this file when the project state changes materially.
