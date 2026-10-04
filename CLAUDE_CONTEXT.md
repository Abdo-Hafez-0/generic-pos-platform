# CLAUDE_CONTEXT.md

Practical onboarding snapshot for future Claude sessions. `PROJECT_STATE.md` remains the formal state document.
Repository source code is the source of truth; last updated 2026-10-04 when Stage 5C was completed.

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
Stage 1 Foundation, 2 Client Host, 3 Database Foundation, 4 Module Contract, 5A Catalog, 5B Inventory, 5C Sales.
Roadmap: 5D POS, 6 Licensing, 7 Update System, 8 Additional Business Modules, 9 Cloud, 10 Hardware,
11 Security Hardening, 12 Offline/Failure Testing. Never skip ahead.

## 10. Current stage
Stage 5C (Sales) is COMPLETE. Next is Stage 5D (POS), awaiting the technical lead's instruction.

## 11. Stage 5C result (Sales module)
- **Domain:** Sale (Draft->Confirmed->Completed, Cancel from Draft/Confirmed), SaleItem (snapshots product name/SKU,
  UnitPrice, Discount, TaxRate), Return, ReturnItem, SalesTransaction; value objects Money, SaleQuantity, strongly-typed IDs.
- **Application:** commands CreateSale, AddSaleItem, ConfirmSale, CompleteSale, CancelSale; queries GetSaleById, GetAllSales
  (plain handler classes with `HandleAsync`). AddSaleItem uses Catalog.Contracts `IProductLookup` and (when a warehouse is
  given) Inventory.Contracts `IStockAvailabilityChecker`.
- **Contracts:** `ISalesService`, `ISalesReader` + result/summary models.
- **Infrastructure:** `SalesDbContext` (`sal_` tables), migration `InitialSalesSchema`, internal EF repositories,
  `SalesModule`/manifest/`SalesHostingModule`/`SalesDatabaseInitializer`; `[InternalsVisibleTo("Sales.Tests")]` is set in
  `SalesInfrastructureAssemblyMarker.cs` (same pattern as Inventory).
- **UI:** `SaleListViewModel` only.
- **Host:** `App.xaml.cs` registers Catalog, Inventory, Sales hosting modules in that order.
- **Tests:** Sales.Tests 103 (domain, application, infrastructure incl. migration/initializer, contracts);
  Architecture.Tests 95 total incl. ARCH-SAL-001..016 (`SalesBoundaryTests.cs`).
- Fixed during 5C: `SalesReader.FindByIdAsync` used `s.Id.Value == guid`, untranslatable by EF; now `s.Id == new SaleId(guid)`.
- Deliberately NOT done: stock reduction and payments on CompleteSale (Stage 5D / Payments module); no Payments entity.

## 12. Solution structure
`GenericPOS.sln`: `src/Platform/{Platform.Core,Contracts,Application,Infrastructure}`, `src/Client/{Client.Host,ModuleHost,Desktop,Licensing,Updater}`,
`src/Modules/{Catalog,Inventory,Sales}/<Module>.{Domain,Application,Contracts,Infrastructure,UI}` (30 projects total),
`tests/{Architecture,Platform.Infrastructure,Platform.ModuleContract,Catalog,Inventory,Sales}.Tests`.
Docs at repo root: `Architecture & Solution Design.md`, `Generic Offline-First Inventory & POS Platform.md`,
`Module Map & Dependency Specification.md`, `PROJECT_STATE.md`.

## 13. Module pattern (canonical, from Catalog/Inventory)
Domain (entities, strongly-typed IDs, value objects, events) / Contracts (interfaces + DTOs for other modules) /
Application (commands, queries, repo interfaces, UoW, DTOs) / Infrastructure (DbContext, EF configs, internal repos,
contract implementations, `<X>Module : IModule`, `<X>ModuleManifest`, `<X>HostingModule : IHostingModule`,
`<X>DatabaseInitializer`, `Add<X>Module()`, Migrations, InternalsVisibleTo test assembly) / UI (ViewModels, net10.0-windows).
Registered in `Client.Desktop/App.xaml.cs` via hosting modules (Catalog, then Inventory, then Sales).
IHostingModule (DI at build time) and IModule (runtime lifecycle) are intentionally separate.

## 14. Important contracts
Platform.Core: `Result`, `Error`, `IModule`, `IModuleManifest`, `ModuleId`, `ModuleVersion`, `VersionRange`.
Catalog.Contracts: `IProductLookup`, `IProductBarcodeResolver`. Inventory.Contracts: `IInventoryReader`,
`IStockAvailabilityChecker`, `IStockMovementReader`. Sales.Contracts: `ISalesService`, `ISalesReader`.

## 15. Testing strategy
xUnit. Per-module test projects (domain, application integration on in-memory SQLite, infrastructure, contracts) plus
`Architecture.Tests` boundary rules (ARCH-INV-xxx style). Baseline (verified 2026-10-04): Architecture 95, Catalog 64, Inventory 81, Platform.Infrastructure 19,
Platform.ModuleContract 112, Sales 103 = 474 passing.

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
`feat: add sales module (Stage 5C)` (see `git log -1`). Before it: `6e5d39a docs: add Claude project context`,
`f6d1848 docs: reconcile project state after inventory`.

## 20. Exact next task
Stage 5D - POS module, only when the technical lead gives the instruction. POS must use Catalog.Contracts,
Inventory.Contracts and Sales.Contracts only. Do not start it unprompted.

## 21. Known limitations / inconsistencies
- CompleteSale does not reduce stock or process payments yet (future work; Payments is a separate module).
- PROJECT_STATE.md header still says "Cloud Backend ... Stage 6"; roadmap is 6 Licensing, 7 Updates, 9 Cloud.
- WPF UI projects are not covered by architecture tests (net10.0-windows TFM gap).
- Sales.UI is minimal (one view model).
- Test baseline: 474 tests (Architecture 95, Catalog 64, Inventory 81, Platform.Infrastructure 19,
  Platform.ModuleContract 112, Sales 103).

## 22. Rules for future Claude sessions
1. Read PROJECT_STATE.md, this file, and the 3 architecture docs before acting.
2. Inspect `git status` first; uncommitted work may be legitimate — never discard it.
3. Implement only the task given; stop after reporting.
4. Respect Contracts-only cross-module rules; never reference other modules' Infrastructure/UI/Domain.
5. Follow the Catalog/Inventory pattern exactly; keep Domain free of EF/WPF.
6. Report test/build failures honestly; don't mark unfinished work complete.
7. Update this file when the project state changes materially.
