# MISS-11 - Electronic receipts for Egypt (ETA) and Saudi Arabia (ZATCA): plan

Status: PLAN, decisions settled (2026-10-19). Owner's decision 2026-10-19: Matjar is sold in **both Egypt and Saudi Arabia**, so MISS-11 covers the
Egyptian Tax Authority's B2C e-receipt and ZATCA's e-invoicing phase 2 (integration). Nothing is built yet; the owner's decisions are in section 4. Facts marked *(to confirm)* must be checked against the authorities' current specifications or a
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
- **11b Product and tax codes**: authority codes on products, units and tax rates (Egypt's first); a check that lists what is missing.
- **11c Egypt (ETA)** - first (owner): the receipt JSON, UUID chain, the QR on the receipt, POS registration (client ID/secret, token), batch
  submission and status, late-submission handling; built against the published SDK and samples, tested on the pre-production environment.
- **11d Saudi Arabia (ZATCA)**: UBL XML for simplified invoices, signing, ICV/PIH chain, the 9-field QR on the receipt, onboarding of a till
  (OTP, CSR, compliance checks, production CSID, renewal), reporting; built against the published specification and samples, tested on the
  simulation environment.
- **11e Returns**: fiscal returns / credit notes from MISS-10 in both countries.
- **11f Release check**: a real end-to-end run in both test environments, a review by a tax adviser in each country, an owner's guide for
  registering a shop and its tills.

## 4. Owner's decisions (2026-10-19)

1. **Egypt first**: after the shared core (11a, 11b), the ETA e-receipt (11c), then ZATCA (11d).
2. **Shop sales only**: Egypt B2C e-receipts and Saudi simplified invoices. Full tax invoices to businesses (Egypt e-invoice, Saudi standard
   invoice with clearance) are a later item (LATER-11).
3. **Test accounts decided later**: build against the published specifications and samples first; the ETA pre-production and ZATCA
   simulation accounts are arranged before the release check (11f).
4. **MISS-10 first**: returns and refunds are built before MISS-11 (fiscal returns need them).
5. **A tax adviser in each country** checks the plan and the finished receipts before Matjar is sold there.

## 5. Owner's decisions for 11a (2026-10-20)

1. **Keep selling, queue**: once switched on, every sale and return gets its fiscal document (and QR) at once, even when the till is not
   registered yet, offline, or the authority refuses; the documents wait in the queue and go out when possible. The shop never stops.
2. **The Fiscal screen**: two columns of white boxes for the setup (country and shop; this till) and the documents list underneath (the
   pattern of Backup / Devices).
3. **Warnings for managers and the till**: the full warning in the status bar for those who manage e-receipts (`fiscal.manage`); a short
   line on the till when documents have waited more than 12 hours, so someone is told.
4. **The country is locked after the first document**: it can change only while no fiscal document exists for this installation.

How 11a is built (each step tested and committed): **11a-1** the module, its settings (country, shop tax details), this till's record and
the Fiscal screen's setup boxes; **11a-2** the fiscal document of every sale and return, made in the same transaction (the per-till chain
and counter; the country part builds the content - none yet, so a test part proves the core); **11a-3** the outbox sender with retries,
the documents list, states and warnings. One installation (its own database) is one till.

## 6. Owner's decisions for 11b (2026-10-20)

1. **All codes on the Electronic receipts screen**: a "Codes for the tax authority" box with products, units and tax rates (missing ones
   counted); the product, unit and tax rate screens stay as they are.
2. **A valid GS1 barcode is the product's code** (EAN-13 / EAN-8 / UPC / GTIN-14 with a correct check digit) without typing; the rest need an
   EGS code.
3. **EGS codes suggested from the SKU and exported**: EG-{tax number}-{SKU}, and a CSV file to register them on the ETA portal in one upload
   *(to confirm: the columns of the portal's current upload template; EGS codes also need a parent GPC code, filled on the portal)*.
4. **Missing codes in the status bar** for those who manage e-receipts.

What each authority needs (from the published specifications): Egypt's receipt lines carry itemType (GS1 / EGS), itemCode, unitType (from
ETA's unit table) and taxableItems (taxType + subType, e.g. T1 / V009 for the general VAT *(to confirm against the code tables)*). Saudi
Arabia's simplified invoice needs each tax rate's VAT category (S, Z, E, O) and, for Z / E / O, the exemption reason code and text; no
product codes. Noted for 11c: Egypt's seller block needs a structured branch address (governorate, city, street, building number) - the
11a settings keep a single address line, to be split then.

## 7. Owner's decisions for 11c (2026-10-21)

1. **The general Receipt type**: sales are `receiptType` "s" (Receipt v1.2), returns "r" (Return Receipt v1.2, `referenceUUID` of the
   sale in the header). The type is defined in one place *(to confirm with the tax adviser: a shop selling goods may have to use the
   Retail Receipt "SR" instead)*.
2. **A split payment sends the code of its largest payment**: Cash "C", Card "V", Other "O" (ETA's payment method codes); one payment
   sends its own code.
3. **The buyer is asked at the till**: a sale at or above the threshold (150,000 EGP for v1.2, a setting) asks for the buyer's name and
   14-digit national ID before it can finish (buyer type "P"); below it the buyer is "P" without an ID.
4. **Late documents are held**: a document still unsent 24 hours after issue moves to a new state Late and is no longer retried; the
   warning tells the owner to file a late-submission request on the ETA portal (yearly quota); after approval "Send late receipts"
   (fiscal.manage, audited) takes a fresh token and sends them.
5. **"Correct and resend" in 11c**: a rejected receipt is rebuilt from its stored content with the current codes - a new UUID, the same
   `previousUUID`, `referenceOldUUID` = the rejected one - and sent; the rejected one is kept, marked Replaced.
6. **Our own QR encoder** (byte mode, error level M) drawn into the receipt image; the QRCoder library (MIT) is used only in the tests as
   a reference. No new package in the app.
7. **"This till" box = a summary + a window** (drawn option B): the box shows the status, environment, POS serial, part of the client
   ID and the last sign-in; "Register this till..." opens a window with the environment (pre-production / production), client ID,
   client secret, POS serial, OS version, model framework and pre-shared key. The secret and the key are kept with Windows DPAPI, never
   shown again, never in backups or logs; the token lives in memory only.
8. **The QR large and centred** under the totals (drawn option A, about 30 mm), with "إيصال إلكتروني" and a short UUID below it.

Facts checked on the ETA SDK (2026-10-21):
- Receipt v1.2: `header` (dateTimeIssued UTC, receiptNumber, uuid, previousUUID - "" only for the POS's first receipt -,
  referenceOldUUID, currency, exchangeRate), `documentType` (receiptType, typeVersion "1.2"), `seller` (rin, companyTradeName,
  branchCode, `branchAddress` {country "EG", governate, regionCity, street, buildingNumber; postalCode, floor, room, landmark,
  additionalInformation optional}, deviceSerialNumber, activityCode), `buyer` (type B / P / F, id, name, mobileNumber), `itemData`
  (internalCode, description, itemType, itemCode, unitType, quantity, unitPrice, netSale, totalSale, total, discounts, taxableItems
  {taxType, amount, subType, rate}), totalSales, netAmount, totalAmount, taxTotals, paymentMethod; feesAmount and adjustment only 0.
- The UUID: the receipt with an empty uuid, serialized (property names in invariant upper case and quoted, values quoted as they are
  in the JSON - numbers are not reformatted -, an array's name before the array and again before each element), SHA-256, 64 hex
  characters. So Matjar writes each number in one fixed form and hashes exactly the text it sends.
- The QR: `{portal}/receipts/search/{uuid}/share/{dateTimeIssued UTC}#Total:{totalAmount},IssuerRIN:{rin}`.
- Sign-in: `POST {identity}/connect/token`, form grant_type=client_credentials, client_id, client_secret; headers posserial,
  pososversion, posmodelframework, presharedkey; a Bearer token for 3600 s.
- Submission: `POST /api/v1/receiptsubmissions` {receipts, signatures}; 1-500 receipts, at most 1.5 MB, 300 lines each; answer 202
  with submissionUUID, acceptedDocuments, rejectedDocuments; 422 DuplicateSubmission (same batch within 10 minutes, Retry-After).
  Status: `GET /api/v1/receiptsubmissions/{submissionUuid}/details?PageNo&PageSize` - submission InProgress / Valid / Invalid,
  each receipt Valid / Invalid / Cancelled with its errors.
- *(to confirm)*: the issuer's CAdES-BES signature in `signatures` ("signature validation is not yet deployed" - 11c sends none); the
  pre-production and production host names of the identity service, the API and the portal; the rounding tolerance ETA allows between
  a line's tax and netSale x rate (Matjar's prices include tax: the net is worked back with 5 decimals and the line total kept exact);
  the late-submission request (made on the portal; no API found).

How 11c is built (each step tested and committed): **11c-1** the seller's structured address (governorate from a list, city, street,
building number, optional fields) and the Egypt part's Build - the receipt JSON, serialization, the UUID chain, the QR text, the payment
code, the buyer at the till above the threshold - tested against the SDK's rules and samples; **11c-2** the QR encoder and the QR on the
printed and on-screen receipt; **11c-3** the till's registration (the window, DPAPI, the environment), the token, the batch submission
and reading back its status; **11c-4** Late documents and "Send late receipts", and "Correct and resend".

11c-1 (2026-10-21, owner's choice from drawn options: the buyer's boxes in the payment area, shown only when needed). Built as described;
notes *(to confirm with the ETA's pre-production environment and the tax adviser)*: numbers are written with 2 to 5 decimals and hashed as
written; Matjar's prices include tax, so each line's net unit price and net amount are worked back with 5 decimals and the tax is the rest
(every total is exactly what was charged); the governorate is sent by its Arabic name; the QR's date is the receipt's dateTimeIssued
(yyyy-MM-ddTHH:mm:ssZ); the portal in the QR is the production one until 11c-3 ties it to the till's environment; the till's serial number
is Matjar's own (MTJ-XXXX-XXXX-XXXX), to be registered on the ETA portal as the POS serial; a return of a sale that has no receipt (sold
before receipts were on) is not built - the "Return Receipt Without Reference" type is for 11e.

11c-2 (2026-10-21): the QR printed with Matjar's own encoder (level M, byte mode), about 30 mm (whole dots per module), centred after
the payments, with "إيصال إلكتروني" and "UUID first-8...last-8" under it, on the sale receipt, its copy and the return receipt *(to confirm with the tax
adviser: a minimum printed size, and whether the title and UUID are wanted)*. A sale whose document was not built prints no QR.

11c-3 (2026-10-21), owner's decisions: **no issuer signature for now** (the submit API lists one as required but says its validation is
not deployed; to confirm on pre-production, signing with a certificate becomes a LATER item if required); **send every 5 minutes** (up to
500 receipts per batch, "Send now" any time); **the environment is locked after the first send** (a tested till moves to production with a
fresh installation). The ETA's addresses (SDK FAQ): pre-production id.preprod.eta.gov.eg / api.preprod.invoicing.eta.gov.eg /
preprod.invoicing.eta.gov.eg; production id.eta.gov.eg / api.invoicing.eta.gov.eg / invoicing.eta.gov.eg. *(to confirm on
pre-production: the receipts accepted without a signature; the POS serial format the portal takes; that a submission's details list each
receipt's uuid and status as read)*.

11c-4 (2026-10-21), owner's choices: drawn option A (the late line with "Send late receipts" above the documents, "Correct and resend" in a
refused row's "..." menu) and "Build and send" for documents that could not be made (built at the end of the chain with their sale's
time). A corrected copy keeps the refused one's place in the chain and its previousUUID and names it as referenceOldUUID; it is not held
by the 24 hours (the ETA takes a resubmission with the old UUID) *(to confirm: the 96-hour amendment window the FAQ mentions)*. Late ones
are held; later receipts still go out *(to confirm on pre-production: that the ETA takes a receipt whose previous one is held)*.
**MISS-11c is complete**; the run on the ETA's pre-production environment is part of 11f.
