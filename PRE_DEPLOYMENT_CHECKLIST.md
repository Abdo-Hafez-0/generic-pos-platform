# Pre-Deployment Checklist

Work remaining after Stage 13, before and during packaging. Sources: `PROJECT_STATE.md` limitation registers (Stages 5D-13) and a code check on 2026-10-06.
Tick an item only when it is built, tested and recorded in `PROJECT_STATE.md`. IDs are stable; refer to them in commits (e.g. `feat(pos): ... [UI-03]`).

Recommended order: Part A (fixes) -> Part B (missing before packaging) -> Part C (Stage 14) -> Part D (follow-ups).

---

## Part A - Fixes to previous stages

### A1. High priority - features that exist but are not connected

- [ ] **FIX-01 Host the business screens in the shell** (Stages 5-11)

  Decisions (2026-10-07, user):
  1. **DI scope per user action**: view models call `IUiActionRunner`, which opens a fresh scope per action, disposes it, logs unexpected exceptions and returns a plain message (also covers FIX-07 and the UI side of FIX-06).
  2. **Unlicensed modules**: navigation entry stays visible but locked with a plain sentence; entries the user has no permission for are hidden.
  3. **Handwritten MVVM**: one shared `ViewModelBase` / async command in `Platform.Presentation` (no new package); replaces the per-module copies.
  4. **Resources from the start**: every UI string in `.resx`, layouts safe for `FlowDirection=RightToLeft`; English only for now (prepares FIX-13).

  Findings: only PosView, ProductListView, CreateProductView have XAML; no view model is registered in DI; view models take scoped handlers directly; PosViewModel shows `ex.Message` and asks for a free-text cashier and a warehouse id; view models have no tests; no view model exists for roles/permissions or licensing.

  - [x] **FIX-01a Shell foundation** (2026-10-07)
    - [x] New `Platform.Presentation` project (net10.0, no WPF/EF): `ScreenDescriptor` (id, title resource, group, required capability, view/view-model types), `IScreenProvider`, `IUiActionRunner`, `ViewModelBase`, async commands
    - [x] Each module UI declares its screens through `IScreenProvider` (no Module.UI -> Client.Host reference, Rule 6)
    - [x] `MainWindow` becomes the shell: grouped navigation + content area; Stage 2 placeholder text removed
    - [x] Navigation filtered by permission, license-locked entries explained
    - [x] Resource (.resx) infrastructure for shell strings (`Ui:Culture` setting, RTL-ready `FlowDirection`)
    - [x] `UI.Tests` project (net10.0-windows) for view models and navigation; architecture rules ARCH-SOL-023/024 for `Platform.Presentation`
    - [x] Smoke run: first-run setup -> shell -> sign out -> sign in -> close (exit 0)
    - Carried forward: SignInWindow texts still inline (FIX-13); per-module `ViewModelBase` copies replaced in FIX-01c/d; WPF resource target moved to a shared import in FIX-01b
  - [x] **FIX-01b POS screen** (2026-10-07): host PosView; cashier = signed-in user; warehouse picker; no exception text shown; runner-based
    - [x] Resume the cashier's open till and cart after sign-out/restart (new POS reads: open session by cashier, active warehouses)
    - [x] Tests: view model (20), real offline desktop (3: sell, resume, unknown code), every screen view loads its XAML; smoke: POS entry locked on an unlicensed install
    - Carried forward: payment method / tendered amount at checkout (FIX-10); scanner (FIX-02); change-quantity and clear-cart buttons
    - **Found**: a fresh install is Unlicensed and could not be activated from the desktop -> license screen done next (user decision)
  - [x] **FIX-01c Catalog, Inventory, Sales screens** (2026-10-07)
    - [x] Products (search, create/edit with barcode, deactivate), Categories and units, Stock (overview, receive by SKU/barcode, correct with reason), Warehouses, Sales history (period, lines, takings)
    - [x] Fixed on the way: editing without cost permission erased the cost; one barcode could belong to two products
    - [x] Real exe end to end: activate -> set up -> receive -> sell by barcode -> stock and history updated
    - Carried forward: remove a barcode from a product (no command yet); export/refund in sales history (Stage 8 limits)
  - [ ] **FIX-01d Back-office screens**: Customers, Suppliers, Purchasing, Pricing, Payments, CashManagement, Reporting, Audit
  - [ ] **FIX-01e Administration screens**
    - [x] License activation and status screen (2026-10-07, done before FIX-01c): activate / renew, shell unlocks at once; verified end to end with a real local license server
    - [ ] Users, roles and permissions (new view model)
- [ ] **FIX-02 Barcode scanner input** (Stage 10)
  - [ ] Forward key presses from the window to `IKeyboardInputSink`
  - [ ] Bind `IPOSBarcodeInput` to the POS view model
- [ ] **FIX-03 Health confirmation after startup** (Stage 7)
  - [ ] Call `UpdateService.ConfirmHealthyAsync` after a healthy start (today an activated update would be rolled back after `MaxStartupAttempts`)
  - [ ] (Runtime loading of activated versions is Part C, PKG-01)
- [ ] **FIX-04 POS -> CashManagement** (Stage 8)
  - [ ] POS records cash sales / cash refunds into the open drawer session through an optional `ICashMovementRecorder`
  - [ ] Decide: inside the checkout `IAtomicOperation` or after the commit (and document the decision)
- [ ] **FIX-05 Audit business actions** (Stage 8)
  - [ ] Optional `IAuditRecorder` in the business modules (POS/Pricing pattern)
  - [ ] Audit at least: completed sale, refund/return, void, stock adjustment, price change, purchase receive, cash pay-in/pay-out/close

### A2. Medium priority - correctness and robustness

- [ ] **FIX-06 Friendly failure messages in every hosted module** (Stage 12): Inventory, Purchasing, CashManagement, Catalog, Customers, Suppliers, Pricing, Payments translate unexpected failures into plain results, like POSService
- [ ] **FIX-07 One DI scope per user action in the hosted UI** (Stage 13): enforce it in the shell, or switch cross-module readers to `AsNoTracking`; add a test
- [ ] **FIX-08 Tax and discounts** (Stages 5D/8): tax rate(s), line and cart discounts; POS `Total != Subtotal`; Sales receives real tax/discount; snapshot at transaction time (rule 14)
- [ ] **FIX-09 Purchasing gaps** (Stage 8)
  - [ ] Partial-quantity receiving
  - [ ] Supplier returns
- [ ] **FIX-10 POS split payments** (Stage 8): offer the split payments the Payments API already supports
- [ ] **FIX-11 Customer on a sale** (Stage 8): a sale can carry an optional customer
- [ ] **FIX-12 Sales report limit** (Stage 8): Sales.Contracts exposes a ranged query so the report no longer stops at 2000 sales
- [ ] **FIX-13 Arabic / non-ASCII support** (Stage 10)
  - [ ] Receipts: code pages (Arabic etc. print as `?` today)
  - [ ] UI: localization resources and right-to-left layout

### A3. Low priority - documentation

- [ ] **FIX-14** `PROJECT_STATE.md` "Next Task" still says the desktop has no sign-in screen (stale since the Stage 11 review)
- [ ] **FIX-15** `PROJECT_STATE.md` stage-status table stops at Stage 11; add Stages 12 and 13

---

## Part B - Missing before the packaging stage

- [ ] **MISS-01 Usable UI layer** - covered by FIX-01, FIX-02, FIX-06, FIX-07, plus an update notification in the shell
- [ ] **MISS-02 Module wiring** - covered by FIX-04, FIX-05, FIX-11
- [ ] **MISS-03 Tax and discounts** - covered by FIX-08
- [ ] **MISS-04 Client backup module** (Stages 9/11)
  - [ ] Design first: encryption and key management (who holds the key, recovery, rotation) - approve before building
  - [ ] `CloudBackup` optional module with `IBackupClient` / HTTP client to BackupServer.Api
  - [ ] Capabilities `backup.create` / `backup.restore` / `backup.delete` declared and enforced
  - [ ] Local backup (to a folder / USB) for shops without internet
  - [ ] Restore flow (database not in use during restore)
  - [ ] Scheduling
- [ ] **MISS-05 Log file and crash reports** (Stage 12)
  - [ ] Rolling log file under LocalAppData (no secrets)
  - [ ] Unhandled-exception crash report collection
- [ ] **MISS-06 Testing**
  - [ ] UI-automated business workflow (sign in -> scan -> sell -> receipt -> close shift) on the real executable
  - [ ] Load / soak test (e.g. a full day of sales; many sales per minute)
- [ ] **MISS-07 Physical device verification** (Stage 10)
  - [ ] Choose the supported printer, scanner, cash drawer (and scale if needed)
  - [ ] Windows spooler / driver printing adapter if the chosen printer needs it
  - [ ] Serial/USB transports if needed
  - [ ] Device failure campaign (unplug during a sale, paper out, etc.)

---

## Part C - Stage 14: Packaging & Deployment

- [ ] **PKG-01 Launcher**: starts the ACTIVE core version; ModuleHost loads modules from the updater's deployment directories (runtime adoption of updates, deferred since Stage 7)
- [ ] **PKG-02 `IModuleMigrator`** implemented in the business modules (pre-activation migration really runs)
- [ ] **PKG-03 Installer** (MSIX or WiX/Inno): install, upgrade, uninstall (uninstall never deletes business data - rule 10)
- [ ] **PKG-04 Code signing**: Authenticode-signed binaries and installer
- [ ] **PKG-05 Trusted public keys** moved from editable configuration into the signed binary (Stage 11 register)
- [ ] **PKG-06 CLI wrappers** for `ModulePackager` and `UpdatePublisher`; a documented release procedure
- [ ] **PKG-07 Server deployment**
  - [ ] TLS termination and forwarded headers behind a reverse proxy
  - [ ] Throttling behind a proxy (per real client address)
  - [ ] Server database choice (SQLite single node vs another provider for multi-node)
  - [ ] Backup/package storage location and retention
- [ ] **PKG-08 AdminPortal browser UI** (optional; today it is an API only)

---

## Part D - Follow-ups (after packaging / Stage 15)

- [ ] **LATER-01** Installation device binding (key pair proven at license renewal)
- [ ] **LATER-02** Automatic background license renewal
- [ ] **LATER-03** Admin authentication: per-admin accounts/roles, key rotation, 2FA (replace static API keys)
- [ ] **LATER-04** Activation-key brute-force protection, installation transfer flow
- [ ] **LATER-05** Update server: staged rollout / release channels, delta packages
- [ ] **LATER-06** Receipt graphics (logo, barcode), customer display
- [ ] **LATER-07** Reporting: export (CSV/PDF), scheduling
- [ ] **LATER-08** Stage 15 production readiness

---

## Progress log

| Date | Item(s) | Commit(s) | Notes |
|---|---|---|---|
| 2026-10-07 | FIX-01 plan | f6ab63d | Decisions recorded |
| 2026-10-07 | FIX-01a | c4fceab | Shell foundation; 2343 tests, 0 warnings, 122 projects |
| 2026-10-07 | FIX-01b | 43b0cf7 | POS screen; 2370 tests, 0 warnings |
| 2026-10-07 | FIX-01e (license) | 79cb067 | License screen; 2378 tests, 0 warnings; real end-to-end activation |
| 2026-10-07 | FIX-01c | c2cc6f8, a36467d, (this commit) | Catalog, Inventory, Sales screens; 2 defects fixed; 2409 tests, 0 warnings |
