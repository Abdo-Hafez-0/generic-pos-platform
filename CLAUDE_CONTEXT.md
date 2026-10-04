# CLAUDE_CONTEXT.md

Practical onboarding snapshot for future Claude sessions. `PROJECT_STATE.md` remains the formal state document.
Repository source code is the source of truth; this file was written from an audit on 2026-10-04.

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
Stage 1 Foundation, 2 Client Host, 3 Database Foundation, 4 Module Contract, 5A Catalog, 5B Inventory — all committed and pushed.
Roadmap: 5C Sales, 5D POS, 6 Licensing, 7 Update System, 8 Additional Business Modules, 9 Cloud, 10 Hardware,
11 Security Hardening, 12 Offline/Failure Testing. Never skip ahead.

## 10. Current stage
Stage 5C — Sales. IN PROGRESS, NOT COMPLETE, NOT COMMITTED.

## 11. Exact Stage 5C progress (audited 2026-10-04)
All Sales work is UNCOMMITTED (untracked `src/Modules/Sales/`, `tests/Sales.Tests/`; modified `GenericPOS.sln`,
`Client.Desktop.csproj`, `App.xaml.cs`). Do not reset/clean it.

Exists and compiles (Client.Desktop builds with 0 errors/0 warnings):
- **Sales.Domain**: `Sale` (Draft->Confirmed->Completed, Cancel), `SaleItem`, `Return`, `ReturnItem`, `SalesTransaction`;
  value objects `Money`, `SaleQuantity`, `SaleId`, `SaleItemId`, `ReturnId`, `ReturnItemId`; enums `SaleStatus`, `ReturnStatus`;
  events `SaleCreated/Completed/Cancelled`.
- **Sales.Application**: commands CreateSale, AddSaleItem, ConfirmSale, CompleteSale, CancelSale; queries GetSaleById, GetAllSales;
  `SaleDto`; repository interfaces ISaleRepository, IReturnRepository, ISalesTransactionRepository; ISalesUnitOfWork.
  Handlers are plain classes with `HandleAsync` (no MediatR).
- **Sales.Contracts**: `ISalesService`, `ISalesReader`, models `SaleResults`, `SaleStatusContract`, `SaleSummaryResult`.
- **Sales.Infrastructure**: `SalesDbContext` (5 `sal_` tables), 5 EF configurations, 3 internal EF repositories, `SalesUnitOfWork`,
  `SalesReader`, `SalesService` (internal), `SalesModule`, `SalesModuleManifest` (id `sales`, v1.0.0, depends on catalog + inventory),
  `SalesHostingModule`, `SalesDatabaseInitializer`, `AddSalesModule()` DI extension.
- **Sales.UI**: only `SaleListViewModel`.
- **Host wiring**: Client.Desktop references Sales.Infrastructure + Sales.UI; `App.xaml.cs` has `using Sales.Infrastructure.Module;`
  but `.WithModule(new SalesHostingModule())` is NOT yet added (only Catalog and Inventory are registered) — Sales is not live in the host.
- **Solution**: all 6 Sales projects added to `GenericPOS.sln`.
- **Tests**: `tests/Sales.Tests/Sales.Tests.csproj` + `SalesTestDatabase.cs` (SQLite in-memory harness, `StubProductLookup`,
  `StubStockAvailabilityChecker`). NO actual test cases written.

Known defects / missing:
1. **Solution build FAILS**: `Sales.Tests` gets CS0122 (6 errors) because Sales.Infrastructure lacks
   `[assembly: InternalsVisibleTo("Sales.Tests")]`. The csproj uses a bogus `<InternalsVisibleTo>` property. Inventory does it via
   an assembly attribute in `InventoryInfrastructureAssemblyMarker.cs` — copy that pattern.
2. `SalesHostingModule` not registered in `App.xaml.cs`. 3. No EF migration for Sales (no `Migrations/` folder); initializer falls back to EnsureCreated when no migrations exist.
4. No Sales tests (domain/application/infrastructure/contracts), no `SalesBoundaryTests` in Architecture.Tests
   (Architecture.Tests does not reference Sales assemblies; `Assemblies.cs` not updated).
5. `CompleteSaleCommandHandler` records the SalesTransaction only; inventory reduction and payments are deliberately deferred to 5D.
6. Sales.UI is minimal; not architecture-tested (net10.0-windows gap, same as other modules).
7. PROJECT_STATE.md not updated for 5C.
8. Design deviates from PROJECT_STATE "Next Task" wording (see §21).

## 12. Solution structure
`GenericPOS.sln`: `src/Platform/{Platform.Core,Contracts,Application,Infrastructure}`, `src/Client/{Client.Host,ModuleHost,Desktop,Licensing,Updater}`,
`src/Modules/{Catalog,Inventory,Sales}/<Module>.{Domain,Application,Contracts,Infrastructure,UI}`,
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
`Architecture.Tests` boundary rules (ARCH-INV-xxx style). Baseline (verified 2026-10-04, excluding Sales.Tests):
Architecture 79, Catalog 64, Inventory 81, Platform.Infrastructure 19, Platform.ModuleContract 112 = 355 passing.

## 16. Git workflow
Task -> inspect -> implement only that task -> build -> test -> verify architecture boundaries -> update PROJECT_STATE.md ->
review git status/diff -> Conventional Commit -> push -> verify push -> verify clean tree -> report -> STOP.
Never force push, rewrite history, reset/clean unexplored work, commit secrets or bin/obj, or mix unrelated changes.
If push auth fails, stop and report. Commit trailer: `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## 17. Branch
`master`, remote `origin` = https://github.com/Abdo-Hafez-0/generic-pos-platform.git

## 18. Git status at audit time
Modified: `GenericPOS.sln`, `Client.Desktop/App.xaml.cs`, `Client.Desktop.csproj`. Untracked: `src/Modules/Sales/`, `tests/Sales.Tests/`.
(All pre-existing Stage 5C work; only this file was added by the onboarding task.)

## 19. Last relevant commit
`f6d1848 docs: reconcile project state after inventory` (previous: `899f0c8` PROJECT_STATE, `cc6e3e5` tests, `40a249f` inventory).

## 20. Exact next task
Resume and finish Stage 5C: fix InternalsVisibleTo, register SalesHostingModule in App.xaml.cs, generate `sal_` migration,
write Sales tests, add Sales architecture tests (ARCH-SAL-xxx), run full solution build/tests, update PROJECT_STATE.md,
commit Sales work as `feat: add sales module (Stage 5C)`, push, verify. Then STOP (do not start 5D). Awaits user instruction.

## 21. Known limitations / inconsistencies
- PROJECT_STATE.md "Next Task" describes Sales as SalesOrder/SalesOrderLine/SalePayment; actual code is Sale/SaleItem/Return/
  ReturnItem/SalesTransaction (no payments entity — Payments is a separate future module). Reconcile when updating state.
- PROJECT_STATE.md says "Cloud Backend ... Stage 6" while the roadmap puts Licensing at 6 and Cloud at 9.
- Sales GUIDs in the .sln look hand-written (valid format); harmless but unusual.
- WPF UI projects are not covered by architecture tests.

## 22. Rules for future Claude sessions
1. Read PROJECT_STATE.md, this file, and the 3 architecture docs before acting.
2. Inspect `git status` first; uncommitted work may be legitimate — never discard it.
3. Implement only the task given; stop after reporting.
4. Respect Contracts-only cross-module rules; never reference other modules' Infrastructure/UI/Domain.
5. Follow the Catalog/Inventory pattern exactly; keep Domain free of EF/WPF.
6. Report test/build failures honestly; don't mark unfinished work complete.
7. Update this file when the project state changes materially.
