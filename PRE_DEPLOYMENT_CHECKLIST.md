# Pre-Deployment Checklist

Work remaining after Stage 13, before and during packaging. Sources: `PROJECT_STATE.md` limitation registers (Stages 5D-13) and a code check on 2026-10-06.
Tick an item only when it is built, tested and recorded in `PROJECT_STATE.md`. IDs are stable; refer to them in commits (e.g. `feat(pos): ... [UI-03]`).

Recommended order: Part A (fixes) -> Part B (missing before packaging) -> Part C (Stage 14) -> Part D (follow-ups).

---

## Part A - Fixes to previous stages

### A1. High priority - features that exist but are not connected

- [ ] **FIX-01 Host the business screens in the shell** (Stages 5-11)
  - [ ] Replace the Stage 2 placeholder content in `MainWindow.xaml` with a real shell (navigation, content area)
  - [ ] Navigation entries shown/hidden by the signed-in user's capabilities
  - [ ] Host PosView (POS)
  - [ ] Host Catalog, Inventory, Sales views
  - [ ] Host the Stage 8 view models (Customers, Suppliers, Purchasing, Pricing, Payments, CashManagement, Reporting)
  - [ ] User / role administration screen (handlers exist and are authorized)
  - [ ] License activation and license status screen
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
| | | | |
