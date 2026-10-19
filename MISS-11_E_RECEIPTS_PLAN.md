# MISS-11 - Electronic receipts for Egypt (ETA) and Saudi Arabia (ZATCA): plan

Status: PLAN (2026-10-19). Owner's decision 2026-10-19: Matjar is sold in **both Egypt and Saudi Arabia**, so MISS-11 covers the
Egyptian Tax Authority's B2C e-receipt and ZATCA's e-invoicing phase 2 (integration). Nothing is built yet; the open questions at the end
are settled with the owner before coding. Facts marked *(to confirm)* must be checked against the authorities' current specifications or a
tax adviser before release.

## 1. What each country requires (from the official documentation)

### Egypt - ETA e-receipt (B2C)
- Each **POS device is registered** with the ETA and linked to the shop's tax registration number (RIN) before it issues a receipt; the
  shop needs the B2C tag; the POS gets a **client ID and secret** and signs in for a token (JWT). Receipts from retired POS devices are
  refused. [SDK - token](https://sdk.invoicing.eta.gov.eg/toolkitapi/02-token/), [Issuance FAQ](https://sdk.invoicing.eta.gov.eg/receiptissuancefaq/)
- The receipt is **JSON**. Its **UUID** is a SHA-256 hash (64 hex characters) of the receipt's serialized, normalized content, which
  includes the **UUID of the previous receipt from the same POS** (a chain; the first uses an empty string). A return includes the
  **referenceUUID** of the original sale before hashing. [FAQ](https://sdk.invoicing.eta.gov.eg/receiptissuancefaq/)
- The receipt prints a **QR code**: a link to the receipt on the ETA portal (`.../receipts/search/{UUID}/share/{date-time UTC}`) followed by
  `#Total:{total},IssuerRIN:{RIN}`. Everything needed is known at the till, so the QR is printed **offline**. [FAQ](https://sdk.invoicing.eta.gov.eg/receiptissuancefaq/)
- Receipts are sent in batches (1-500 receipts, up to 1.5 MB, up to 300 lines each) to `POST /api/v1/receiptsubmissions`, **within 24
  hours** of issuance (later only through a late-submission request with a yearly quota); the result is read back per submission.
  [Submit](https://sdk.invoicing.eta.gov.eg/ereceiptapi/02-submit-receipt/), [Submission status](https://sdk.invoicing.eta.gov.eg/ereceiptapi/06-get-receipt-submission/)
- Returns are allowed up to **540 days** after the sale and must reference a receipt of the matching type. A person buyer's national ID is
  required above a total threshold (150,000 EGP for receipt version 1.2+). Products carry **item codes** (GS1 or EGS) and unit types from
  the ETA code tables *(to confirm: exact fields for receipt version in force)*.
- Obligation comes in **named waves** of businesses (e.g. Cairo tax offices from 15 Sept 2025); more lists are expected.
  [e-invoicing.org](https://e-invoicing.org/egypt/), [Comarch](https://www.comarch.com/trade-and-services/data-management/legal-regulation-changes/egypt-expands-e-receipt-requirements-for-b2c-transactions-from-september-2025/)

### Saudi Arabia - ZATCA phase 2 (integration)
- Each **e-invoice generation unit** (each till) is onboarded: an OTP from the Fatoora portal, a CSR, a **compliance CSID**, compliance
  checks with sample documents, then a **production CSID** (certificate) that is renewed before it expires.
  [Fatoora portal manual](https://zatca.gov.sa/en/E-Invoicing/Introduction/Guidelines/Documents/Fatoora_Portal_User_Manual_English.pdf),
  [Onboarding](https://zatca1.discourse.group/t/onboarding-and-renewal/480)
- A shop sale is a **simplified tax invoice**: an XML document (UBL 2.1) **signed on the till** with the unit's key, carrying a UUID, an
  **invoice counter (ICV)** and the **previous invoice's hash (PIH)** - a chain like Egypt's. *(to confirm: XAdES signature profile and key
  curve in the current specification)*
- The printed invoice carries a **QR code** with 9 TLV fields (seller name, VAT number, time, total with VAT, VAT, invoice hash, signature,
  public key, ZATCA's stamp) - generated on the till, so it prints **offline**.
  [Wafeq - QR](https://www.wafeq.com/en-sa/tax-and-reporting/qr-code-requirements-for-e-invoices-zatca-saudi-arabia)
- Simplified invoices are **reported within 24 hours** (`.../invoices/reporting/single`); standard (B2B) tax invoices must be **cleared before**
  they are given to the buyer (`.../invoices/clearance/single`). A simulation environment mirrors production.
  [Endpoints](https://zatca1.discourse.group/t/e-invoicing-api-endpoints/487), [ClearTax](https://cleartax.com/sa/process-flow-for-generating-e-invoices-in-phase-ii)
- Obligation comes in **waves** by revenue; seller details must also be in Arabic *(to confirm: current wave dates)*.

### What the two have in common
A chain of documents per till, a hash and a QR known at the till (so selling and printing never wait for the internet), a queue that sends
within 24 hours, a registration of each till with the authority, returns that reference the original sale, and product / tax codes from the
authority's lists.

## 2. How it fits Matjar

- **A new module, `Fiscal`** (licensed per country - a paid add-on, to be placed in the packages, MISS-18), with two country parts:
  `Fiscal.Egypt` (ETA) and `Fiscal.Saudi` (ZATCA). The installation chooses its country once (Egypt / Saudi Arabia / none); the other part
  stays off.
- **Inside checkout, in the same transaction** as the sale (like the cash and payments today, FIX-04/FIX-10): the fiscal document is built
  from the sale's snapshot, its UUID / hash / counter / chain link computed and stored, the QR made - so a sale never exists without its
  fiscal document, and the receipt prints the QR at once. Two tills never share a chain: each till (POS device / generation unit) has its own.
- **An outbox queue**: a background sender (like the cloud-backup uploads, MISS-04) sends what waits, retries with back-off, records the
  authority's answer per document (Waiting / Sent / Accepted / Rejected with the reasons). Nothing is ever deleted; a rejected document is
  corrected the way the authority allows (Egypt: a new UUID referencing the old one; Saudi: a credit/debit note).
- **Warnings that cannot be missed**: the status bar warns when documents have waited too long (approaching 24 hours) or were rejected,
  for the people who can act (a new capability `fiscal.manage`); the till keeps selling.
- **Returns** (MISS-10) issue the fiscal return / credit note referencing the original sale - so MISS-10 comes first or together.
- **Product and tax data**: products get the authority's codes (Egypt: GS1/EGS item code, unit type; Saudi: VAT category per tax rate,
  exemption reasons), checked when the module is switched on so no sale is blocked later by a missing code; shop details (tax number,
  branch, activity code, Arabic name and address) on a settings screen.
- **Secrets**: the ETA client secret and the ZATCA private key / certificate are kept on the till, protected by Windows (DPAPI), like the
  cloud-backup access key - never in the database backups, never in logs.
- **A Fiscal screen** (Administration): the country, the shop's tax details, each till's registration (Egypt: client ID/secret; Saudi: the
  onboarding steps with the OTP - CSR, compliance checks, production CSID - and its expiry date), and the list of documents with their state,
  "Send now", and the authority's reasons for a rejection.
- **Tests**: unit tests of the UUID / hash / QR / XML against the authorities' published samples; integration tests of the queue (offline,
  retries, rejections, restart); real tests against **ZATCA simulation** and the **ETA pre-production** environment before release.

## 3. Steps (each one tested and committed on its own)

- **11a Shared fiscal core**: the Fiscal module, country choice, shop tax details, per-till chain and counter, documents stored in the sale's
  transaction, the outbox and sender, states and warnings, the Fiscal screen (without a country part it does nothing).
- **11b Product and tax codes**: authority codes on products, units and tax rates; a check that lists what is missing.
- **11c Saudi Arabia (ZATCA)**: UBL XML for simplified invoices, signing, ICV/PIH chain, the 9-field QR on the receipt, onboarding of a till
  (OTP, CSR, compliance checks, production CSID, renewal), reporting; tested on the simulation environment.
- **11d Egypt (ETA)**: the receipt JSON, UUID chain, the QR on the receipt, POS registration (client ID/secret, token), batch submission and
  status, late-submission handling; tested on the pre-production environment.
- **11e Returns**: fiscal returns / credit notes from MISS-10 in both countries.
- **11f Release check**: a real end-to-end run in both test environments, a review by a tax adviser in each country, an owner's guide for
  registering a shop and its tills.

## 4. Open questions for the owner (before 11a)

1. **Which country first** (11c Saudi or 11d Egypt)? Saudi is the larger piece (XML signing, onboarding); Egypt is simpler but needs
   product item codes. Building the shared core first serves both.
2. **B2B invoices**: only shop sales (Egypt B2C receipts, Saudi simplified invoices) for now, or also full tax invoices to businesses
   (Egypt e-invoice, Saudi standard invoice with clearance)? Recommended: shop sales first, B2B later.
3. **Test accounts**: the ETA pre-production and ZATCA simulation need a taxpayer account (a test or a real shop). Who registers them
   (the owner's company, or a first customer)?
4. **Order with MISS-10 (returns)**: recommended MISS-10 first, then MISS-11 (returns need fiscal return documents).
5. **Tax adviser**: will the owner have the plan and the finished receipts checked by a tax adviser in each country before selling?
