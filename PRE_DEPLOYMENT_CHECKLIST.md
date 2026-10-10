# Pre-Deployment Checklist

Work remaining after Stage 13, before and during packaging. Sources: `PROJECT_STATE.md` limitation registers (Stages 5D-13) and a code check on 2026-10-06.
Tick an item only when it is built, tested and recorded in `PROJECT_STATE.md`. IDs are stable; refer to them in commits (e.g. `feat(pos): ... [UI-03]`).

Recommended order: Part A (fixes) -> Part B (missing before packaging) -> Part C (Stage 14) -> Part D (follow-ups).

---

## Part A - Fixes to previous stages

### A1. High priority - features that exist but are not connected

- [x] **FIX-01 Host the business screens in the shell** (Stages 5-11) - done 2026-10-07

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
  - [x] **FIX-01d Back-office screens** (2026-10-07): Customers, Suppliers, Prices, Purchase orders, Cash drawer, Business overview, Audit log
    - [x] Fixed on the way: cash drawer records were attributed to whatever name the caller passed (now the signed-in user)
    - [x] Real exe: all 14 menu entries open without error once licensed
    - Decided: no standalone Payments screen (only "void" exists; belongs to checkout, FIX-10)
    - Carried forward: customer/supplier addresses and contacts (handlers exist, no screen yet)
  - [x] **FIX-01e Administration screens**
    - [x] License activation and status screen (2026-10-07, done before FIX-01c): activate / renew, shell unlocks at once; verified end to end with a real local license server
    - [x] Users, roles and permissions (2026-10-07): temporary passwords (changed at first sign-in), roles, permission ticks
    - [x] User decision: administration can never lock itself out (last user who can manage users; no self-deactivation), enforced in the handlers
    - [x] Fixed on the way: permission ticks did not work through UI Automation (screen readers)
    - Carried forward: suggest capabilities that go together (Sell needs Open/close POS sessions); rename/delete roles; change usernames
- [x] **FIX-02 Barcode scanner input** (Stage 10) - done 2026-10-08
  - [x] Forward key presses from the window to `IKeyboardInputSink` (not while a text box has the focus: the box handles the scan itself; the Enter that ends a scan is swallowed so it cannot press a focused button)
  - [x] Bind `IPOSBarcodeInput` to the POS view model (listens while the screen is shown; no cart bound during checkout/close; stops when another screen opens or on sign-out)
  - [x] Real exe: scans on the cart grid, on a focused Checkout button (no sale) and in the barcode box (added once); unknown code refused; scan on another screen ignored
  - Carried forward: a scan always adds quantity 1; physical scanner verification (MISS-07)
- [x] **FIX-03 Health confirmation after startup** (Stage 7) - done 2026-10-08
  - [x] Call `UpdateService.ConfirmHealthyAsync` after a healthy start (today an activated update would be rolled back after `MaxStartupAttempts`)
  - User decisions: confirm only updates whose version really runs in the process; healthy = host started + start screen rendered (no sign-in needed)
  - Until PKG-01 the built-in binaries run, so an activated-but-not-loaded update is still rolled back (it never ran); PKG-01's launcher must set `Updater:RunningHostVersion`
  - [ ] (Runtime loading of activated versions is Part C, PKG-01)
- [x] **FIX-04 POS -> CashManagement** (Stage 8) - done 2026-10-08
  - [x] POS records cash sales / cash refunds into the open drawer session through an optional `ICashMovementRecorder` (cash SALES; refunds: no refund flow exists in POS or Sales.Contracts yet)
  - [x] Decide: inside the checkout `IAtomicOperation` or after the commit (and document the decision) - user: INSIDE; a cash sale is refused without an open shift
  - [x] Drawer per installation: `PosCash:DrawerCode` (default MAIN); the POS screen takes the total in cash (choice/tender/split: FIX-10)
  - [x] Real exe: refused without a shift; after opening it on the Cash drawer screen the sale goes through and the drawer shows "Cash sale +5.00"
  - Carried forward: cash refunds (needs a refund flow)
- [x] **FIX-05 Audit business actions** (Stage 8) - done 2026-10-08
  - [x] Optional `IAuditRecorder` in the business modules (POS/Pricing pattern) - user: a Platform `IBusinessEventSink` instead (like security events; no module references Audit), in-memory retry buffer
  - [x] Audit at least: completed sale, refund/return, void, stock adjustment, price change, purchase receive, cash pay-in/pay-out/close (refund/return: no flow exists yet; also cash shift opened)
  - Reported after the commit (the audit log has its own connection by design), stamped with the signed-in user, written in the background
  - Carried forward: refunds/returns (need a flow); "no sale" drawer opens and catalog/purchase-order changes (not chosen); durable outbox (not chosen)

### A2. Medium priority - correctness and robustness

- [x] **FIX-06 Friendly failure messages in every hosted module** (Stage 12): Inventory, Purchasing, CashManagement, Catalog, Customers, Suppliers, Pricing, Payments translate unexpected failures into plain results, like POSService - done 2026-10-08
  - [x] Screens: every action already goes through IUiActionRunner (FIX-01); proven per module with failures injected inside SQLite (no SQL/table/exception text, nothing written)
  - [x] Module boundaries: the write contracts (stock issue/receipt, payment record/void, cash recorder) return plain failed results instead of throwing
  - [x] Real exe: category and drawer shift with the database refusing -> plain sentence; works once it accepts again
  - Carried forward: screen message is generic (does not name the action); log file to look in (MISS-05)
- [x] **FIX-07 One DI scope per user action in the hosted UI** (Stage 13): enforce it in the shell, or switch cross-module readers to `AsNoTracking`; add a test - done 2026-10-08
  - [x] Enforced in the shell (FIX-01 runner); a kept action scope now refuses to hand out services; readers keep tracking
  - [x] Every screen view model and the shell built from the root with scope validation on (catches indirect captures)
  - [x] Tests: stale read shown on a kept scope, fresh on the screen's next action (offline desktop); real exe: a price changed in the database file appears at the next Search
- [x] **FIX-08 Tax and discounts** (Stages 5D/8): tax rate(s), line and cart discounts; POS `Total != Subtotal`; Sales receives real tax/discount; snapshot at transaction time (rule 14) - done 2026-10-08

  Decisions (2026-10-08, user):
  1. **Prices include tax** (VAT style): the shelf price is what the customer pays; the tax inside it is shown and reported.
  2. **Tax rates live in Pricing**: named rates (e.g. Standard 14%, Zero 0%) with one default, and an optional rate per product; the till snapshots the rate on each line. Without Pricing: no tax.
  3. **Line and cart discounts** at the till, percentage or amount, behind a new permission, with a configurable maximum; a cart discount is spread over the lines; every discount is audited.
  4. **Rounding per line to 2 decimals** (half away from zero); sale totals are the sums of the rounded lines.

  Out of scope (optional Discounts & Promotions module, later): automatic promotions, coupons, buy X get Y, promotional periods, customer discounts.

  - [x] **FIX-08a Tax rates in Pricing** (2026-10-08): `TaxRate` / `ProductTaxRate` (pri_TaxRates, pri_ProductTaxRates, migration AddTaxRates), capability `pricing.tax.manage`, contract `ITaxRateResolver` (own rate -> default -> none), Tax rates screen (rates, default, change, deactivate; product rate), audited
  - [x] **FIX-08b Tax at the till and in Sales** (2026-10-08): cart lines snapshot the rate; Sales extracts tax from the gross price; cart/receipt/sale show subtotal, tax and total - one shared rule `Platform.Core.Amounts.TaxInclusiveLine` (rounded per line); pos_CartItems.TaxRate (migration AddCartItemTaxRate); receipt "incl. tax 14%" lines
  - [x] **FIX-08c Discounts at the till** (2026-10-08): line and cart discounts (percentage/amount), permission + maximum, spread over lines, audited - capability `pos.discount.give`, `PosDiscount:MaximumPercent` (default 100), migration AddDiscounts; receipt shows discount, subtotal and tax
  - [x] Real exe: STD 14% on the Tax rates screen; 4 x 2.50 with 10% on the line and 0.50 on the cart -> 8.50 incl. 1.04 tax; 90% refused above a 50% maximum; printed receipt checked
  - Carried forward: Sales history and Business overview do not show tax/discounts yet (the data is recorded); automatic promotions/coupons (optional Discounts module)
- [x] **FIX-09 Purchasing gaps** (Stage 8) - done 2026-10-08

  Decisions (2026-10-08, user):
  1. **Receive per delivery + close short**: each delivery enters a quantity per line (adding up, never above what was ordered); the order is "Partly received" until every unit arrived; a partly received order can be closed short with a reason (the rest is no longer expected).
  2. **Returns attach to a received purchase order**: return from the order's received quantities (never more than received minus earlier returns), from the order's warehouse, at its unit cost; a reason is required; stock goes out through Inventory; audited.
  3. **New capability** `purchasing.return.create` (sensitive) for supplier returns.

  - [x] **FIX-09a Partial-quantity receiving** (2026-10-08): per-line delivery quantities, statuses PartiallyReceived (5) and Closed (6), close short (capability purchasing.order.cancel), received value kept on the order; migration AddPartialReceiving backfills earlier receipts; Purchase orders screen: Received / Still due / Arrived columns, Close short (the Line total column was later dropped to make room, 09b)
    - [x] Real exe: 12 ordered, 5 then 2 arrive (8 refused: "Only 7 ... are still expected"), closed short -> stock 7, overview 8.40 received / 0.00 open; a two-line order with one line complete and one partly
  - [x] **FIX-09b Supplier returns** (2026-10-08): `SupplierReturn` (pur_SupplierReturns / pur_SupplierReturnLines, migration AddSupplierReturns), ReturnedQuantity per order line, ReturnToSupplier through Inventory's IStockIssueService in one transaction (ARCH-RES-004), audited supplier-return.created, capability purchasing.return.create; screen: Returned column, return the selected line with a reason, the order's returns listed
    - [x] Real exe (database upgraded in place from 09a): 2 of 6 received juice returned (4.00), 5 more refused ("Only 4 ... received 6, already returned 2"), 1 water returned from the closed-short order; stock 30 / 4; audit entry with the reason
    - Found: an administrator created before a capability existed does not hold it (first-run setup grants the capabilities of that moment); the new permission had to be ticked on Roles and permissions - flagged as a separate task
    - Carried forward: supplier credit / refund money (no payables in Purchasing); returns are not in the Business overview; a return of several lines at once exists in the command but the screen returns one line at a time
- [x] **FIX-10 POS split payments** (Stage 8): offer the split payments the Payments API already supports - done 2026-10-09

  Decisions (2026-10-09, user):
  1. **Add parts, then finish**: the cashier adds payment parts (cash, card, other) and sees what is still due; Checkout is enabled once the parts cover the total; every part is recorded in the one checkout transaction. Parts live on the screen only (a resumed cart's parts are entered again). No parts = the whole total in cash, as before.
  2. **Only cash gives change**: card/other parts never exceed what is due; a cash part may be tendered above what it pays (the change is shown and printed); only the cash kept goes into the drawer shift; any cash part needs an open shift.
  3. **Optional note**: card parts take an optional note (e.g. approval code); "Other" needs a description; nothing is sent anywhere (no terminal integration).

  - [x] IPOSService.CheckoutWithPaymentsAsync (POSPaymentRequest +Amount; POSCheckoutResult +PaymentIds); the checkout checks the parts before anything is written (amounts > 0 with 2 decimals, add up exactly to the total, tendered only for cash and not below its part, Other described)
  - [x] Receipt: one line per part, then tendered and change for the cash (ReceiptDocument.Payments); the audit entry names the parts ("paid by card 5.00 + cash 2.50")
  - [x] POS screen: Pay with / Amount / Note / Add payment, the parts with Remove, Still due and Change; a cart change clears the parts
  - [x] Real exe: 3 x 2.50 paid card 5.00 (approval 4711) + cash 10.00 -> change 7.50, drawer 50 -> 52.50, printed receipt shows both parts; a card part above what is due refused; a sale without parts still cash
  - Carried forward: parts of an open cart are not kept across sign-out/restart (decided); refunds to the original methods (no refund flow yet)
- [x] **FIX-11 Customer on a sale** (Stage 8): a sale can carry an optional customer - done 2026-10-09

  Decisions (2026-10-09, user):
  1. **Anyone who sells** (pos.sale.create) can find an existing active customer by code, name or phone and attach them; the till shows code and name only, never e-mail, phone or addresses (Stage 11: cashiers do not read customer records).
  2. **Pick existing only**: customers are created on the Customers screen, not at the till.
  3. **Shown in Sales history**: a Customer column and the selected sale's customer (the code/name snapshot taken at the sale).

  - [x] Sales: `Sale.CustomerId/Code/Name` snapshot (AssignCustomer on a draft), ISalesService.CreateSaleAsync(customer:), SaleCustomer, SaleSummaryResult +CustomerId/Name, migration AddSaleCustomer
  - [x] POS: the cart keeps the chosen customer (resumed with the till), IPOSService.FindCustomersAsync / SetCustomerAsync through the OPTIONAL Customers contracts, checkout hands the snapshot to Sales, migration AddCartCustomer
  - [x] Screens: Customer row on the POS screen (find, choose among several, remove); Sales history Customer column and detail
  - [x] Real exe: two customers created, "jan" offers both, Jane Doe chosen, the till restarted with her still on the cart, sold; Sales history shows "C-001 Jane Doe"; older sales unchanged after the upgrade
  - Carried forward: customer on the receipt and in the audit entry (not chosen); a customer's purchase history / reports by customer; customer pricing (optional Discounts module)
- [x] **FIX-12 Sales report limit** (Stage 8): Sales.Contracts exposes a ranged query so the report no longer stops at 2000 sales - done 2026-10-09
  - [x] `ISalesReader.GetCompletedBetweenAsync(fromUtc, toUtc)` - every completed sale of the range, oldest first, no count limit (index on sal_Sales.CompletedAt, migration AddSaleCompletedAtIndex); the sales report and the Business overview use it (IsTruncated is always false now)
  - [x] The daily breakdown uses the shop's local calendar days (the computer's time zone, daylight saving included, through TimeProvider); the overview lists every day of the period again
  - [x] Real exe (Egypt, UTC+3 in October): a sale at 23:30 local on 8 Oct and one at 00:30 local on 9 Oct (21:30 UTC on 8 Oct) -> 8 Oct 1 sale 7.50, 9 Oct 2 sales 7.50 (UTC days would have put 10.00 on 8 Oct)
  - Not chosen: a time-zone setting separate from the computer's (the till runs in the shop); Sales history still lists at most 1000 sales per period (a screen list, its totals are of what it shows)
- [x] **FIX-13 Arabic / non-ASCII support** (Stage 10) - done 2026-10-09

  Decisions (2026-10-09, user):
  1. **Screens now, messages later**: every screen text (resources + the sign-in window) in Arabic with right-to-left layout checked screen by screen; the sentences business rules return stay English for now (a follow-up item).
  2. **Claude drafts the Arabic, the user reviews**: Modern Standard Arabic for every string, with a list of uncertain terms.
  3. **Language per user**: each user chooses Arabic or English; the screens switch at that user's sign-in; the sign-in window follows the installation default.
  4. **Receipts with Arabic print as a picture** (rendered with a Windows font, ESC/POS GS v 0); ASCII receipts stay text.

  - [x] **FIX-13a Receipts as a picture** (2026-10-09): `IReceiptImageRenderer` (Platform hardware abstraction) implemented by the desktop with WPF (Arial; Arabic shaped and right-aligned, amounts at the right edge); the ESC/POS formatter lays the receipt out as lines and sends them as text or, when a text needs more than ASCII, as a raster picture in 128-row bands; printer settings `PrintAsImage` (Auto | Always | Never) and `DotsPerLine` (576 / 384); a renderer failure falls back to text
    - [x] Real exe: a product named "مياه معدنية ١٫٥ لتر" sold with an English one -> the receipt file holds a 576 x 456 picture; decoded, the Arabic name is joined and right-aligned, totals and labels unchanged
    - Carried forward: receipt labels (Receipt, TOTAL, Change ...) stay English until the receipt follows a language (with FIX-13b/c or later)
  - [x] **FIX-13b Language per user** (2026-10-09): `User.Language` (migration AddUserLanguage), SetUserLanguage (own language for everyone, another user's with users.manage) and GetUserLanguage; `DesktopSession`: the start screen in the installation's language, the shell opened in the signed-in user's language (a new shell window per sign-in or language change); language chooser in the shell header and on the Users screen; sign-in window texts in SignInText.resx
    - [x] Found in the real exe and fixed: .NET restores the thread culture when an async method that changed it returns, so screens opened later read the old language - the display language now lives in `UiCulture.Current` and in every resource class; numbers and dates keep the installation's format
    - [x] Real exe: Arabic chosen in the header -> the shell reopens right to left; sign-out -> the start screen in English; sign-in -> right to left again; English chosen -> left to right
    - Note for screenshots: PrintWindow of a right-to-left window is mirrored (flip it to see the screen)
  - [x] **FIX-13c Arabic translations** (2026-10-09): all 524 screen texts in 16 `*.ar.resx` files (Modern Standard Arabic, drafted by Claude - to be reviewed by the user, see PROJECT_STATE "FIX-13c Summary" for the terms to check); a test keeps every text translated with the same placeholders
    - [x] Real exe: all 17 screens opened in Arabic and checked right to left; fixed on the way: the license state in the status bar was not translated; the signed-in name and a user's heading lost their closing parenthesis (a left-to-right mark keeps it); phone numbers showed their digit groups reversed (left-to-right mark in the grids, phone/e-mail boxes typed left to right); the POS discount row and the Users search row now wrap instead of running off the screen
    - Carried forward (user decision): the sentences that business rules return (refusals, errors, audit summaries) and the permission names/descriptions on the Roles screen stay English - a follow-up item; receipt labels stay English

### A3. Low priority - documentation

- [x] **FIX-14** `PROJECT_STATE.md` "Next Task" still says the desktop has no sign-in screen (stale since the Stage 11 review) - done 2026-10-10: "Next Task" now points to Part B and describes the desktop as it is; the stale "no user-administration / license-activation screen" gap removed
- [x] **FIX-15** `PROJECT_STATE.md` stage-status table stops at Stage 11; add Stages 12 and 13 - done 2026-10-10: rows for Stages 12, 13, the pre-deployment work and Stage 14 (not started)

---

## Part B - Missing before the packaging stage

- [ ] **MISS-01 Usable UI layer** - covered by FIX-01, FIX-02, FIX-06, FIX-07, plus an update notification in the shell
- [ ] **MISS-02 Module wiring** - covered by FIX-04, FIX-05, FIX-11
- [ ] **MISS-03 Tax and discounts** - covered by FIX-08
- [ ] **MISS-04 Client backup module** (Stages 9/11)
  - [x] Design first: encryption and key management (who holds the key, recovery, rotation) - approve before building. Draft: `MISS-04_BACKUP_DESIGN.md`
    Decisions (2026-10-10, user): 1. recovery code held by the shop PLUS vendor escrow; 2. only cloud backups are encrypted, local backups are plain SQLite files (lost-USB risk accepted, shown on screen);
    3. local backup and restore in `Client.Backup` for every shop, cloud in a paid optional `CloudBackup` module (license module `cloud-backup`); 4. a separate sensitive `backup.configure` permission.
    5. escrow mechanism of design section 5B approved; 6. cloud token typed in by the owner; 7. no backup trigger on shift close. Design APPROVED 2026-10-10; build order MISS-04a..f (design section 11).
  - [x] **MISS-04a** `Client.Backup` core (2026-10-10): local backup to a folder, verify, history, settings, retention, backup.* capabilities
  - [ ] `CloudBackup` optional module with `IBackupClient` / HTTP client to BackupServer.Api
  - [x] Capabilities `backup.create` / `backup.restore` / `backup.delete` (+ `backup.configure`) declared and enforced (MISS-04a; restore is enforced when it is built in 04b)
  - [x] Local backup (to a folder / USB) for shops without internet (MISS-04a; the screen is 04d)
  - [x] **MISS-04b** Restore flow (database not in use during restore) (2026-10-10): prepare/confirm while running, swap at the next start before the database opens, before-restore copy kept and listed
  - [x] **MISS-04d** Backup screen (2026-10-10): status, back up now, check, list, restore with a confirming sentence and "Restart now", restore from a file, delete asked first, settings; English + Arabic
  - [x] **MISS-04c** Scheduling (2026-10-10): daily at a set time while the application runs, catch-up after a missed time, 30-minute retry pause, shell notice when a backup fails or no folder is chosen
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
| 2026-10-07 | FIX-01c | c2cc6f8, a36467d, 2cddbad | Catalog, Inventory, Sales screens; 2 defects fixed; 2409 tests, 0 warnings |
| 2026-10-07 | FIX-01d | 03e5399, b2cad75, 9dd8cac, 50a2307, db4fb2a, 1a7fd78 | Stage 8 back-office screens; cash actor defect fixed; 2423 tests, 0 warnings |
| 2026-10-07 | FIX-01e (users) | 3abdef0 | Users, roles and permissions screens; lock-out rule; FIX-01 complete; 2431 tests, 0 warnings |
| 2026-10-08 | FIX-02 | 4b94f7e | Barcode scanner input; 2453 tests, 0 warnings; real exe with a keyboard-wedge scanner |
| 2026-10-08 | FIX-03 | 061e377 | Healthy-start confirmation of updates that really run; 2463 tests, 0 warnings; real exe |
| 2026-10-08 | FIX-04 | 6896931 | POS cash sales into the open drawer shift (inside the transaction); 2472 tests, 0 warnings; real exe |
| 2026-10-08 | FIX-05 | b0a5634 | Business actions in the audit log (Platform event sink, background write, retry buffer); 2482 tests, 0 warnings; real exe |
| 2026-10-08 | FIX-06 | 3f36a0c | Plain failure messages at module boundaries; per-module screen proof; 2492 tests, 0 warnings; real exe |
| 2026-10-08 | FIX-07 | 9429ae3 | One DI scope per action enforced and tested; 2498 tests, 0 warnings; real exe |
| 2026-10-08 | FIX-08 plan, FIX-08a | e6e9ee1 | Decisions recorded; tax rates in Pricing + Tax rates screen; 2512 tests, 0 warnings |
| 2026-10-08 | FIX-08b | 787737b | Tax at the till and in Sales (prices include tax, one shared line rule); 2526 tests, 0 warnings |
| 2026-10-08 | FIX-08c | 0f749f8 | Discounts at the till; FIX-08 complete; 2543 tests, 0 warnings; real exe with a printed receipt |
| 2026-10-08 | FIX-09 plan, FIX-09a | a1d02f8 | Decisions recorded; part deliveries and closing short; 2555 tests, 0 warnings; real exe |
| 2026-10-08 | FIX-09b | 2b1ccec | Supplier returns; FIX-09 complete; 2564 tests, 0 warnings; real exe with an in-place database upgrade |
| 2026-10-09 | FIX-10 | 50089d2 | Decisions recorded; split payments at the till; 2575 tests, 0 warnings; real exe with a printed receipt |
| 2026-10-09 | FIX-11 | 18d25ef | Decisions recorded; an optional customer on a sale; 2587 tests, 0 warnings; real exe with an in-place upgrade |
| 2026-10-09 | FIX-12 | b87b317 | Ranged completed-sales read (no 2000 cap), local-day breakdown; 2589 tests, 0 warnings; real exe across local midnight |
| 2026-10-09 | FIX-13 plan, FIX-13a | 4c978ef | Decisions recorded; receipts with Arabic printed as a picture; 2605 tests, 0 warnings; real exe receipt decoded |
| 2026-10-09 | FIX-13b | 3d8063e | Language per user, sign-in texts in resources, display language independent of the thread culture; 2619 tests, 0 warnings; real exe |
| 2026-10-09 | FIX-13c | 22d55cf | Arabic translations of every screen text, right-to-left fixes; FIX-13 complete; 2636 tests, 0 warnings; real exe, 17 screens checked |
| 2026-10-10 | FIX-14, FIX-15 | 980066e | PROJECT_STATE.md brought up to date (Next Task, stage table); Part A complete |
| 2026-10-10 | MISS-04 design | 7decb78, a58bef1 | Design drafted, all decisions made and approved |
| 2026-10-10 | MISS-04a | a58bef1 | Local backup core (Client.Backup); 2681 tests, 0 warnings; real exe start-up |
| 2026-10-10 | MISS-04b | 7a6f5fa | Restore across a restart (IStartupPreparation, before-restore copy); 2699 tests, 0 warnings; real exe restore at start |
| 2026-10-10 | MISS-04c | 4bd4772 | Daily scheduled backup, catch-up, retry pause, shell notice; 2725 tests, 0 warnings; real exe notice after a failed scheduled backup |
| 2026-10-10 | MISS-04d | (this commit) | Backup screen with restart after a confirmed restore; 2739 tests, 0 warnings; real exe: settings, backup, check, restore, restart, Arabic |
