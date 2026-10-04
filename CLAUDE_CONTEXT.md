# CLAUDE_CONTEXT.md

Practical onboarding snapshot for future Claude sessions. `PROJECT_STATE.md` remains the formal state document.
Repository source code is the source of truth; last updated 2026-10-04 when Stage 6 was completed.

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
6 Licensing (foundation). Roadmap: 7 Update System, 8 Additional Business Modules, 9 Cloud, 10 Hardware,
11 Security Hardening, 12 Offline/Failure Testing. Never skip ahead.

## 10. Current stage
Stage 6 (Licensing) is COMPLETE as a foundation. Next roadmap stage is 7 (Update System) - only when instructed.

## 11. Stage results worth knowing
**Stage 5C (Sales):** Sale/SaleItem/Return/ReturnItem/SalesTransaction; ISalesService/ISalesReader; `sal_` tables, migration
`InitialSalesSchema`; CompleteSale only records a SalesTransaction (no stock, no payments). 103 tests, ARCH-SAL-001..016.

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
`src/Modules/{Catalog,Inventory,Sales,POS}/<Module>.{Domain,Application,Contracts,Infrastructure,UI}` (42 projects total),
`tests/{Architecture,Platform.Infrastructure,Platform.ModuleContract,Catalog,Inventory,Sales,POS,Licensing}.Tests`; `src/Licensing/Licensing.Contracts`, `src/Client/Client.Licensing(.Http)`, `src/Cloud/LicenseServer/*`.
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
`Architecture.Tests` boundary rules (ARCH-INV-xxx style). Baseline (verified 2026-10-04): Architecture 133, Catalog 64, Inventory 89, Platform.Infrastructure 19,
Platform.ModuleContract 112, Sales 103, POS 84, Licensing 109 = 713 passing.

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
`feat(licensing): implement offline-first license management` (see `git log -1`). Before it: `8c44ea4 feat: add pos module (Stage 5D)`.

## 20. Exact next task
None assigned. Wait for the technical lead. Next roadmap stage: Stage 7 Update System. Follow-ups (not in any stage): license
enforcement points/UI, durable license-server storage, Payments module, stock-reversal contract.

## 21. Known limitations / inconsistencies
- No payments (no Payments module); checkout completes the sale and issues stock only.
- No stock-reversal contract; a failed checkout that already issued some lines needs manual stock correction.
- No distributed transaction across module DbContexts.
- PosView exists but is not hosted in MainWindow; POS has no discounts/tax (Total == Subtotal).
- PROJECT_STATE.md header still says "Cloud Backend ... Stage 6"; roadmap is 6 Licensing, 7 Updates, 9 Cloud.
- WPF UI projects are not covered by architecture tests (net10.0-windows TFM gap).
- Licensing is not enforced anywhere yet; no clock-rollback protection; license server is in-memory and unauthenticated.
- Test baseline: 713 tests (Architecture 133, Catalog 64, Inventory 89, Platform.Infrastructure 19,
  Platform.ModuleContract 112, Sales 103, POS 84, Licensing 109).

## 22. Rules for future Claude sessions
1. Read PROJECT_STATE.md, this file, and the 3 architecture docs before acting.
2. Inspect `git status` first; uncommitted work may be legitimate — never discard it.
3. Implement only the task given; stop after reporting.
4. Respect Contracts-only cross-module rules; never reference other modules' Infrastructure/UI/Domain.
5. Follow the Catalog/Inventory pattern exactly; keep Domain free of EF/WPF.
6. Report test/build failures honestly; don't mark unfinished work complete.
7. Update this file when the project state changes materially.
