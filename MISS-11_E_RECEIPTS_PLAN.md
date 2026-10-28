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

## 8. Owner's decisions for 11d (2026-10-22)

1. **Before the till is onboarded: a short QR now, signed later**: a sale made while the till has no certificate prints a QR with
   tags 1-5 only (seller's name, VAT number, time, total with VAT, VAT total); the invoice is signed, put in the chain in sale order
   (ICV, PIH) and reported once the till is onboarded. The shop never stops (11a).
2. **The signing key in Windows' key store, non-exportable** (ZATCA's security standard, requirement 6): made on the till in the
   Windows key store (CNG), never written to a file, never in backups or logs. A reinstalled Windows loses it: the till is onboarded
   again with a new OTP and the chain carries on. The production CSID's secret is kept with DPAPI like the ETA secret.
3. **Onboarding = one button, steps ticked** (drawn option A): "Onboard this till..." opens a window with the environment, the OTP from
   the Fatoora portal, the unit's name, the branch name and the industry; Onboard makes the key, the CSR, asks for the compliance CSID,
   passes the compliance checks with Matjar's own sample documents (simplified invoice, credit note, debit note), and asks for the
   production CSID, ticking each step.
4. **"Correct and resend" makes a corrected copy at the end of the chain**: a refused (NOT_REPORTED) invoice is rebuilt with the current
   codes as a new invoice (new UUID, the next ICV, the chain's last hash) and reported; the refused one stays in the chain, marked
   Replaced *(to confirm with the tax adviser: ZATCA's forum also mentions cancelling it with a credit note and issuing a new invoice)*.
5. **The receipt: title on top, QR under the totals** (drawn option A): "فاتورة ضريبية مبسطة / Simplified Tax Invoice" and the VAT number
   under the shop's name; the QR large and centred under the totals (about 32 mm - it holds more than Egypt's).
6. **The certificate's expiry**: the managers' status bar warns from 30 days before, the till's line in the last 7 days and after it;
   "Renew..." in the same window takes a new OTP and a new CSR.
7. **Reported right after each sale**: each invoice is reported in the background a moment after the sale (ZATCA takes them one at a
   time); what waits is retried every 5 minutes; "Send now" as before.
8. **ZATCA's SDK may be downloaded for the tests** (its sample invoices and validator; kept outside the repository, the file named and
   sized before download, its terms accepted by the owner if asked).

Facts checked (2026-10-22; ZATCA's Security Features Implementation Standards v1.2, 2023-05-19, and the developer community):
- The stamp: ECDSA with SHA-256, a 256-bit key *(the standard says P-256; ZATCA's SDK and every working integration use secp256k1 - to
  confirm with the compliance CSID)*; a PKCS#10 CSR signed with the key, its subject: CN = the unit's name, organizationIdentifier = the
  VAT number (15 digits, starting and ending with 3), OU = the branch name, O = the taxpayer's name, C = SA; the SAN directory name: SN
  "1-Matjar|2-{version}|3-{till serial}", UID = the VAT number, title = the functions "0100" (simplified only), registeredAddress =
  the branch's address (preferably the national short address), businessCategory = the industry; the certificate template name
  ZATCA-Code-Signing (production) or PREZATCA-Code-Signing (simulation) *(to confirm: the sandbox's TSTZATCA)*.
- The signature: XAdES baseline B-B, enveloped in the UBL extensions; the invoice's hash is SHA-256 of the canonical XML (C14N 1.1)
  without the UBLExtensions, cac:Signature and the QR's AdditionalDocumentReference; the second reference covers SignedProperties
  (SigningTime, SigningCertificateV2 / cert digest); the key's certificate is in KeyInfo. The previous invoice hash (PIH) is the
  previous invoice's hash; the first one uses the SDK's fixed value (SHA-256 of "0") *(to confirm against the SDK)*.
- The QR: TLV (1 byte tag, 1 byte length, UTF-8 value), Base64, up to 700 characters; tags 1 seller's name, 2 VAT number, 3 time (ISO
  8601), 4 total with VAT, 5 VAT total (BT-110), 6 the invoice's hash, 7 the ECDSA signature, 8 the public key, 9 for simplified invoices
  the certificate's signature by ZATCA's CA.
- The APIs (`https://gw-fatoora.zatca.gov.sa/e-invoicing/{core | simulation | developer-portal}`): POST /compliance (OTP header, the
  CSR) -> the compliance CSID (binarySecurityToken, secret, requestID); POST /compliance/invoices (the samples); POST /production/csids
  (compliance_request_id) -> the production CSID; PATCH /production/csids (OTP, a new CSR) to renew; POST /invoices/reporting/single
  {invoiceHash, uuid, invoice (Base64)} with Basic authentication (the CSID's token and secret) and Accept-Version V2 -> 200 REPORTED,
  202 REPORTED with warnings, 400 NOT_REPORTED with errorMessages, 401, 500/503 to retry.
- Reporting is within 24 hours, but a late one is not refused: it is reported with the warning BR-KSA-98 (ZATCA checks lateness later).
  So Saudi invoices are not held as Late (Egypt's 11c-4); the warnings still show what waits too long.
- The certificate is valid up to 5 years (the profile); the CA's CRLs are valid 7 days; the compliance CSID is short-lived (24 hours,
  one source).
- The waves: wave 24 (taxable revenue above SAR 375,000 in 2022-2024) by 30 June 2026; wave 25 (above SAR 187,500, 2022-2025) by
  1 February 2027 *(one secondary source)*.

How 11d is built (each step tested and committed): **11d-1** the Saudi part's Build - the seller's Saudi details (CRN, the national
address: street, building number, district, city, postal code, additional number), the UBL XML of a simplified invoice, its hash, ICV and
PIH, the short QR before onboarding - tested against the published rules and the SDK's samples; **11d-2** signing (XAdES with a key in
the Windows key store) and the 9-tag QR, the printed receipt (title, VAT number, the larger QR); **11d-3** onboarding (the window, the
CSR, the compliance CSID, the compliance checks, the production CSID, the secret, renewal and the expiry warnings), and signing the
invoices made before it; **11d-4** reporting (right after each sale and every 5 minutes, the answers, "Correct and resend" at the end of
the chain).

11d-1 (2026-10-22), owner's choices: the seller's identification as a type (CRN, MOM, MLS, 700, SAG, OTH; CRN first) and a number, and
the national address (drawn option A); **returns get their simplified credit note now** (381, naming the sale's invoice, the return's
reason as the reason for issuance) - 11e keeps what is left (a return without a recorded sale); **ZATCA's SDK is downloaded by the owner**
(its terms accepted on zatca.gov.sa) into D:\ZatcaSdk; its samples become test cases when it is there. Built: the simplified invoice in
UBL 2.1 (ProfileID reporting:1.0, InvoiceTypeCode 388/381 name 0200000, ICV, PIH - the first ZATCA's fixed value), the hash (canonical XML
without the stamp and the QR, SHA-256, Base64; Matjar writes no indentation, so the stamp added later leaves it unchanged), the QR's tags
1-5 before onboarding, amounts net of VAT *(to confirm with ZATCA's validator: each category's VAT = its taxable amount x rate, the
payable amount = what was charged with PayableRoundingAmount for a halala; the issue time and the QR's time in Riyadh time without a zone;
the buyer's name alone on a simplified invoice; PCE as the unit when none is set)*. Saudi invoices are not held as Late.

11d-2 (2026-10-22), owner's choices: **every label in Arabic and English** (drawn option A; ZATCA requires the invoice in Arabic), **nothing
under the QR** (the title and VAT number are on top), **the signing key for this Windows user** (like the ETA secrets), **committed without
the SDK check** (owner, 2026-10-22: the details below are confirmed in 11f). Built: the enveloped XAdES stamp in the UBLExtensions, the QR's nine tags, the key in the
Windows key store (secp256k1, CNG, not exportable), the Saudi receipt (labels with the English word first so each line reads left to right; title and VAT number on top, the invoice's totals - excluding VAT,
the VAT, including VAT - and a rounding line when what was charged differs). Built as ZATCA's SDK is widely known to work *(to confirm in 11f
against the SDK's signed samples and validator, and on ZATCA's simulation)*: the signature is ECDSA/SHA-256 of the invoice hash's bytes
(not of SignedInfo); the certificate's and the signed properties' digests are Base64 of the SHA-256's lowercase hex; the signed properties
are digested in their exclusive canonical form; the QR's tags 6 and 7 hold the Base64 text, 8 the public key's DER, 9 the CA's signature;
SigningTime in Riyadh time without a zone.

11d-3 (2026-10-22), owner's choices: **tested against ZATCA's public developer sandbox** (no account, its fixed OTP, ZATCA's test VAT
number and a made-up shop; opt-in tests); **simulation and production** in the onboarding window (the sandbox is a hidden setting,
Fiscal:Saudi:UseDeveloperSandbox); **the till box as a summary with "Onboard this till..." / "Renew..."** (drawn option A); **the invoices made
before onboarding are signed at its end**, oldest first. Built: the CSR, ZATCA's onboarding API (compliance CSID, three compliance checks -
invoice, credit note, debit note -, production CSID, renewal), the certificate on the till and its secret protected, the expiry warnings.
**Confirmed on ZATCA's sandbox** (its compliance checks validate the XML, the hash, the stamp and the QR): onboarding and renewal pass, and
Matjar's invoices pass with no warning in the edge cases (a halala of rounding, zero-rated with an exemption, a buyer's name, weighed and
discounted lines, a credit note, several lines) - so the 11d-1 and 11d-2 details marked *(to confirm)* about the hash, the stamp, the QR's
bytes, the times without a zone, the rounding amount and the buyer's name are settled. One fix came from it: QR tag 4 is the amount due
(BT-115). Still *(to confirm in 11f)*: the same on the simulation with a real account, and production.

11d-4 (2026-10-28), owner's decisions: **the opt-in tests may report to ZATCA's developer sandbox**; **an invoice ZATCA accepts with
warnings is Accepted, its warnings shown in its row** (no status-bar alarm). Built: reporting each signed invoice singly (POST
/invoices/reporting/single, Basic sign-in with the till's certificate and secret, Clearance-Status 0), right after each sale (the outbox is
woken and sends 3 seconds later) and at the 5-minute rounds; NOT_REPORTED is refused with ZATCA's words; a refused sign-in or a busy ZATCA
leaves the rest waiting; "Correct and resend" makes a new invoice at the end of the chain. **Found by reporting to the sandbox** (the
compliance checks did not test them): the signed properties' certificate digest is of the certificate's Base64 text, and the signed
properties are digested in ZATCA's SDK layout with empty elements self-closed - both fixed; Matjar's sale and credit note are REPORTED.
*(to confirm on the simulation in 11f: the answer to an invoice reported twice - taken as accepted on 409)*. **MISS-11d is complete**; the
run on ZATCA's simulation with a real account is part of 11f.
