using Platform.Core.Results;

namespace Fiscal.Domain.Enums
{
    /// <summary>The tax authority whose electronic receipts this installation issues (MISS-11). None = electronic receipts are off.</summary>
    public enum FiscalCountry
    {
        None = 0,
        Egypt = 1,          // the Egyptian Tax Authority's B2C e-receipt (11c)
        SaudiArabia = 2,    // ZATCA e-invoicing phase 2, simplified invoices (11d)
    }

    /// <summary>MISS-11a-2: what a fiscal document records.</summary>
    public enum FiscalDocumentKind
    {
        Sale = 1,
        Return = 2,
    }

    /// <summary>
    /// MISS-11a-2: where a fiscal document stands. Waiting: built and chained, to be sent. Sent: handed to the authority, its answer awaited.
    /// Accepted / Rejected: the authority's answer. NotBuilt: the country's part could not build it (for example a product without the
    /// authority's code) - the sale stands (owner's decision: keep selling); it is not in the chain and needs attention.
    /// </summary>
    public enum FiscalDocumentState
    {
        Waiting = 1,
        Sent = 2,
        Accepted = 3,
        Rejected = 4,
        NotBuilt = 5,

        /// <summary>
        /// MISS-11c-4 (owner's decision): still unsent more than 24 hours after its sale - held, no longer tried; the authority takes it only
        /// after the shop's late-submission request is approved on its portal ("Send late receipts").
        /// </summary>
        Late = 6,

        /// <summary>MISS-11c-4: a refused document replaced by its corrected copy ("Correct and resend"); kept, never sent again.</summary>
        Replaced = 7,
    }

    /// <summary>
    /// MISS-11c-3: which of the authority's systems this till talks to - its pre-production (test) environment or production. Owner's decision:
    /// it can change only until a document has been handed to the authority.
    /// </summary>
    public enum FiscalEnvironment
    {
        None = 0,
        PreProduction = 1,
        Production = 2,
    }

    /// <summary>Whether this till is registered with the authority (Egypt: a POS device with its client ID; Saudi Arabia: an onboarded unit).</summary>
    public enum FiscalTillRegistration
    {
        NotRegistered = 0,
        Registered = 1,
    }
}

namespace Fiscal.Domain.Entities
{
    using Fiscal.Domain.Enums;

    /// <summary>
    /// MISS-11a: the shop's fiscal settings - one row per installation: the country (the authority) and the seller's tax details the
    /// authority's documents carry. The country is LOCKED once this till has issued a fiscal document (owner's decision 2026-10-20: a chain
    /// must never mix countries); the details may still be corrected.
    /// </summary>
    public sealed class FiscalSettings
    {
        public const int SingletonId = 1;

        private FiscalSettings() { }

        public int Id { get; private set; } = SingletonId;
        public FiscalCountry Country { get; private set; }

        /// <summary>The seller's tax registration number (Egypt: the 9-digit RIN; Saudi Arabia: the 15-digit VAT number).</summary>
        public string? TaxNumber { get; private set; }

        /// <summary>Egypt: the branch code ("0" for the head office); Saudi Arabia: not used.</summary>
        public string? BranchCode { get; private set; }

        /// <summary>Egypt: the activity code (ISIC-based) the authority registered for the shop; Saudi Arabia: not used.</summary>
        public string? ActivityCode { get; private set; }

        /// <summary>The seller's legal name in Arabic, as registered with the authority.</summary>
        public string? SellerNameArabic { get; private set; }

        // MISS-11c: the seller's branch address in Arabic, as registered with the authority, in the fields Egypt's receipt carries
        // (seller.branchAddress); 11a kept one line, now the street.
        public string? Governorate { get; private set; }
        public string? City { get; private set; }
        public string? Street { get; private set; }
        public string? BuildingNumber { get; private set; }
        public string? PostalCode { get; private set; }
        public string? Floor { get; private set; }
        public string? Room { get; private set; }
        public string? Landmark { get; private set; }
        public string? AdditionalInformation { get; private set; }

        // MISS-11d: Saudi Arabia's national address adds the district and the additional number
        public string? District { get; private set; }
        public string? AdditionalNumber { get; private set; }

        public FiscalAddress Address => new(Governorate, City, Street, BuildingNumber, PostalCode, Floor, Room, Landmark, AdditionalInformation, District, AdditionalNumber);

        /// <summary>
        /// MISS-11d: Saudi Arabia - the seller's identification and its type (owner's choice, drawn option A: a list, CRN by default): CRN
        /// commercial registration, MOM, MLS, 700, SAG or OTH (ZATCA's schemeID).
        /// </summary>
        public string? SellerIdScheme { get; private set; }
        public string? SellerId { get; private set; }

        /// <summary>MISS-11d: ZATCA's seller identification types (schemeID), CRN first.</summary>
        public static IReadOnlyList<string> SaudiSellerIdSchemes { get; } = ["CRN", "MOM", "MLS", "700", "SAG", "OTH"];

        public DateTime UpdatedAt { get; private set; }
        public string? UpdatedBy { get; private set; }

        public bool IsOn => Country != FiscalCountry.None;

        public static FiscalSettings Default() => new() { Country = FiscalCountry.None };

        /// <summary>
        /// Changes the settings. A country other than None needs its tax number in the authority's form; <paramref name="countryLocked"/>
        /// (documents were issued) refuses any change of country.
        /// </summary>
        public Result Update(FiscalCountry country, string? taxNumber, string? branchCode, string? activityCode, string? sellerNameArabic, FiscalAddress? address,
            bool countryLocked, string? updatedBy, DateTime nowUtc, string? sellerIdScheme = null, string? sellerId = null)
        {
            if (!Enum.IsDefined(country))
                return Error.Validation("Fiscal.Settings.CountryInvalid", "Choose the country of the tax authority.");
            if (countryLocked && country != Country)
                return Error.Conflict("Fiscal.Settings.CountryLocked",
                    "Electronic receipts were already issued on this till, so the country can no longer be changed.");

            var tax = Clean(taxNumber);
            var branch = Clean(branchCode);
            var activity = Clean(activityCode);
            switch (country)
            {
                case FiscalCountry.Egypt:
                    if (tax is null || tax.Length != 9 || !tax.All(char.IsAsciiDigit))
                        return Error.Validation("Fiscal.Settings.TaxNumberInvalid", "The tax registration number (RIN) is 9 digits.");
                    if (branch is null || !branch.All(char.IsAsciiDigit))
                        return Error.Validation("Fiscal.Settings.BranchInvalid", "The branch code is a number (0 for the head office).");
                    if (activity is null || activity.Length != 4 || !activity.All(char.IsAsciiDigit))
                        return Error.Validation("Fiscal.Settings.ActivityInvalid", "The activity code is 4 digits.");
                    break;
                case FiscalCountry.SaudiArabia:
                    if (tax is null || tax.Length != 15 || !tax.All(char.IsAsciiDigit) || tax[0] != '3' || tax[^1] != '3')
                        return Error.Validation("Fiscal.Settings.TaxNumberInvalid", "The VAT number is 15 digits, starting and ending with 3.");
                    branch = null;
                    activity = null;
                    break;
            }

            if (country != FiscalCountry.None && Clean(sellerNameArabic) is null)
                return Error.Validation("Fiscal.Settings.NameRequired", "Give the shop's legal name in Arabic, as registered with the tax authority.");

            var where = (address ?? FiscalAddress.Empty).Cleaned();
            var (scheme, id) = (Clean(sellerIdScheme)?.ToUpperInvariant(), Clean(sellerId));
            if (country == FiscalCountry.SaudiArabia)   // MISS-11d: ZATCA's simplified invoice carries the seller's ID and national address
            {
                if (scheme is null || !SaudiSellerIdSchemes.Contains(scheme))
                    return Error.Validation("Fiscal.Settings.SellerIdTypeRequired", "Choose the type of the shop's identification (CRN for the commercial registration).");
                if (id is null || id.Length > 50 || !id.All(char.IsAsciiLetterOrDigit))
                    return Error.Validation("Fiscal.Settings.SellerIdInvalid", "Give the shop's identification number (letters and digits, for example the 10-digit CRN).");
                if (where.Street is null || where.District is null || where.City is null)
                    return Error.Validation("Fiscal.Settings.AddressRequired", "Give the national address's street, district and city, as registered.");
                if (where.BuildingNumber is not { Length: 4 } building || !building.All(char.IsAsciiDigit))
                    return Error.Validation("Fiscal.Settings.BuildingNumberInvalid", "The national address's building number is 4 digits.");
                if (where.PostalCode is not { Length: 5 } postal || !postal.All(char.IsAsciiDigit))
                    return Error.Validation("Fiscal.Settings.PostalCodeInvalid", "The national address's postal code is 5 digits.");
                if (where.AdditionalNumber is { } extra && (extra.Length != 4 || !extra.All(char.IsAsciiDigit)))
                    return Error.Validation("Fiscal.Settings.AdditionalNumberInvalid", "The national address's additional number is 4 digits (or leave it empty).");
            }
            else
            {
                (scheme, id) = (null, null);
            }
            if (country == FiscalCountry.Egypt)   // MISS-11c: Egypt's receipt carries the branch address in these fields
            {
                if (where.Governorate is null || !FiscalAddress.EgyptGovernorates.Contains(where.Governorate))
                    return Error.Validation("Fiscal.Settings.GovernorateRequired", "Choose the governorate of the shop's branch.");
                if (where.City is null || where.Street is null || where.BuildingNumber is null)
                    return Error.Validation("Fiscal.Settings.AddressRequired",
                        "Give the branch's city, street and building number, as registered with the tax authority.");
            }

            Country = country;
            TaxNumber = tax;
            BranchCode = branch;
            ActivityCode = activity;
            SellerNameArabic = Clean(sellerNameArabic);
            (Governorate, City, Street, BuildingNumber, PostalCode) = (where.Governorate, where.City, where.Street, where.BuildingNumber, where.PostalCode);
            (Floor, Room, Landmark, AdditionalInformation) = (where.Floor, where.Room, where.Landmark, where.AdditionalInformation);
            (District, AdditionalNumber, SellerIdScheme, SellerId) = (where.District, where.AdditionalNumber, scheme, id);
            UpdatedBy = Clean(updatedBy);
            UpdatedAt = nowUtc;
            return Result.Success();
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// MISS-11c: the seller's branch address in the fields the authority's documents carry (Egypt: governorate, city, street and building
    /// number required; the rest optional). In Arabic, as registered with the authority. MISS-11d: Saudi Arabia's national address - street,
    /// building number (4 digits), district, city, postal code (5 digits) and the optional additional number (4 digits).
    /// </summary>
    public sealed record FiscalAddress(string? Governorate, string? City, string? Street, string? BuildingNumber, string? PostalCode = null,
        string? Floor = null, string? Room = null, string? Landmark = null, string? AdditionalInformation = null, string? District = null,
        string? AdditionalNumber = null)
    {
        public static FiscalAddress Empty { get; } = new(null, null, null, null);

        /// <summary>Egypt's 27 governorates, as the branch address names them *(to confirm: the ETA accepts the Arabic names)*.</summary>
        public static IReadOnlyList<string> EgyptGovernorates { get; } =
        [
            "القاهرة", "الجيزة", "الإسكندرية", "القليوبية", "الشرقية", "الدقهلية", "البحيرة", "الغربية", "المنوفية", "كفر الشيخ", "دمياط", "بورسعيد",
            "الإسماعيلية", "السويس", "الفيوم", "بني سويف", "المنيا", "أسيوط", "سوهاج", "قنا", "الأقصر", "أسوان", "البحر الأحمر", "الوادي الجديد", "مطروح",
            "شمال سيناء", "جنوب سيناء",
        ];

        /// <summary>Blank fields become null; the rest is trimmed.</summary>
        public FiscalAddress Cleaned() => new(C(Governorate), C(City), C(Street), C(BuildingNumber), C(PostalCode), C(Floor), C(Room), C(Landmark), C(AdditionalInformation),
            C(District), C(AdditionalNumber));

        private static string? C(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// MISS-11a: THIS till - the installation (one database is one till). It keeps the chain of its fiscal documents: the counter of the last
    /// document and the hash of the last document (each document carries the previous one's, Egypt's previous UUID / Saudi Arabia's PIH),
    /// and whether it is registered with the authority.
    /// </summary>
    public sealed class FiscalTill
    {
        private FiscalTill() { }

        public Guid Id { get; private set; }

        /// <summary>The computer's name when the till was first used (shown on the Fiscal screen).</summary>
        public string Name { get; private set; } = string.Empty;

        public DateTime CreatedAt { get; private set; }

        /// <summary>The counter of the last fiscal document issued (0 = none yet; the next one is <see cref="Counter"/> + 1).</summary>
        public long Counter { get; private set; }

        /// <summary>The hash of the last fiscal document (null before the first).</summary>
        public string? LastDocumentHash { get; private set; }

        public FiscalTillRegistration Registration { get; private set; }

        /// <summary>
        /// MISS-11c: the till's serial number as the authority knows the device (Egypt: the POS serial registered on the ETA portal, carried by
        /// every receipt). Matjar gives each till one at its first use (MTJ-XXXX-XXXX-XXXX from its id); it never changes once a document was
        /// issued.
        /// </summary>
        public string? DeviceSerial { get; private set; }

        /// <summary>A document was issued: the country can no longer change.</summary>
        public bool HasIssued => Counter > 0;

        public static FiscalTill Create(string name, DateTime nowUtc)
        {
            var till = new FiscalTill { Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(name) ? "Till" : name.Trim(), CreatedAt = nowUtc };
            till.EnsureDeviceSerial();
            return till;
        }

        // ---- MISS-11c-3: the registration with the authority (Egypt: the POS registered on the ETA portal). The client secret and the
        // pre-shared key are NOT here: they are kept protected by Windows outside the database (never in backups or logs).

        public FiscalEnvironment Environment { get; private set; }

        /// <summary>Egypt: the client ID the ETA portal gave this POS.</summary>
        public string? ClientId { get; private set; }

        /// <summary>Egypt: the POS's operating system version and model framework, as registered on the portal.</summary>
        public string? PosOsVersion { get; private set; }
        public string? PosModelFramework { get; private set; }

        public DateTime? RegisteredAt { get; private set; }
        public string? RegisteredBy { get; private set; }

        /// <summary>When a document was first handed to the authority (from then on the environment is locked).</summary>
        public DateTime? FirstSentAt { get; private set; }

        /// <summary>The last test of the sign-in: when, and whether the authority accepted it.</summary>
        public DateTime? LastSignInAt { get; private set; }
        public bool? LastSignInOk { get; private set; }

        public bool EnvironmentLocked => FirstSentAt is not null;

        /// <summary>
        /// Records the till's registration. The environment cannot change once a document has been handed over (owner's decision 2026-10-21:
        /// test receipts never mix with real ones in one chain).
        /// </summary>
        public Result Register(FiscalEnvironment environment, string? clientId, string? osVersion, string? modelFramework, string? by, DateTime nowUtc)
        {
            if (environment is not (FiscalEnvironment.PreProduction or FiscalEnvironment.Production))
                return Error.Validation("Fiscal.Till.EnvironmentRequired", "Choose the tax authority's environment: pre-production (testing) or production.");
            if (EnvironmentLocked && Environment != FiscalEnvironment.None && environment != Environment)
                return Error.Conflict("Fiscal.Till.EnvironmentLocked",
                    "Receipts from this till have already reached the tax authority, so its environment can no longer change. Use a new installation.");
            var id = clientId?.Trim();
            if (string.IsNullOrEmpty(id) || id.Length > 100) return Error.Validation("Fiscal.Till.ClientIdInvalid", "Give the client ID from the tax authority's portal (up to 100 characters).");
            var os = osVersion?.Trim();
            if (string.IsNullOrEmpty(os) || os.Length > 50) return Error.Validation("Fiscal.Till.OsVersionInvalid", "Give the POS's operating system version as registered (up to 50 characters).");
            var model = modelFramework?.Trim();
            if (string.IsNullOrEmpty(model) || model.Length > 10) return Error.Validation("Fiscal.Till.ModelInvalid", "Give the POS model framework as registered (up to 10 characters).");

            (Environment, ClientId, PosOsVersion, PosModelFramework) = (environment, id, os, model);
            (RegisteredAt, RegisteredBy, Registration) = (nowUtc, string.IsNullOrWhiteSpace(by) ? null : by.Trim(), FiscalTillRegistration.Registered);
            (LastSignInAt, LastSignInOk) = (null, null);
            return Result.Success();
        }

        /// <summary>A document was handed to the authority: from now on the environment is locked.</summary>
        public void MarkSent(DateTime nowUtc) => FirstSentAt ??= nowUtc;

        public void RecordSignIn(bool ok, DateTime nowUtc) => (LastSignInAt, LastSignInOk) = (nowUtc, ok);

        /// <summary>Gives a till made before MISS-11c its serial number. True when it was given now (to be saved).</summary>
        public bool EnsureDeviceSerial()
        {
            if (!string.IsNullOrWhiteSpace(DeviceSerial)) return false;
            var hex = Id.ToString("N").ToUpperInvariant();
            DeviceSerial = $"MTJ-{hex[..4]}-{hex[4..8]}-{hex[8..12]}";
            return true;
        }

        /// <summary>Takes the next number of the chain for a new document and records its hash as the chain's last link.</summary>
        public long Chain(string documentHash)
        {
            if (string.IsNullOrWhiteSpace(documentHash)) throw new ArgumentException("A fiscal document needs its hash.", nameof(documentHash));
            Counter++;
            LastDocumentHash = documentHash;
            return Counter;
        }
    }

    /// <summary>
    /// MISS-11a-2: the fiscal document of one sale or return, made in the SAME transaction as it: the country part's content (JSON for Egypt,
    /// UBL XML for Saudi Arabia), its place in this till's chain (number and the previous document's hash), its own hash and the QR the
    /// receipt prints. Never deleted; a rejected document is corrected the way the authority allows.
    /// </summary>
    public sealed class FiscalDocument
    {
        private FiscalDocument() { }

        public Guid Id { get; private set; }
        public FiscalDocumentKind Kind { get; private set; }

        /// <summary>The sale or the return it records.</summary>
        public Guid SourceId { get; private set; }

        /// <summary>The number printed on the receipt ("S-1A2B3C4D", "R-...").</summary>
        public string Reference { get; private set; } = string.Empty;

        /// <summary>A return: the sale it returns and its receipt number.</summary>
        public Guid? OriginalSourceId { get; private set; }
        public string? OriginalReference { get; private set; }

        public FiscalCountry Country { get; private set; }
        public Guid TillId { get; private set; }

        /// <summary>Its number in this till's chain (null when not built).</summary>
        public long? Number { get; private set; }

        /// <summary>The previous document's hash (null for the first of the chain).</summary>
        public string? PreviousHash { get; private set; }

        public string? Hash { get; private set; }

        /// <summary>The document as the authority receives it.</summary>
        public string? Payload { get; private set; }

        /// <summary>What the receipt's QR code holds.</summary>
        public string? QrPayload { get; private set; }

        public decimal Total { get; private set; }
        public DateTime IssuedAt { get; private set; }
        public FiscalDocumentState State { get; private set; }

        /// <summary>Why it was not built, or the authority's reasons for a rejection.</summary>
        public string? StateReason { get; private set; }

        public int Attempts { get; private set; }
        public DateTime? LastAttemptAt { get; private set; }
        public DateTime CreatedAt { get; private set; }

        /// <summary>MISS-11a-3: when the sender may try again (null = at once); it backs off after each failed attempt.</summary>
        public DateTime? NextAttemptAt { get; private set; }

        /// <summary>The authority's reference once handed over (Egypt: the submission id; Saudi Arabia: the reporting answer).</summary>
        public string? AuthorityReference { get; private set; }

        /// <summary>When the authority answered (accepted or rejected).</summary>
        public DateTime? AnsweredAt { get; private set; }

        /// <summary>
        /// MISS-11c-4: what the document was made from (the sale or return as POS described it, JSON), so it can be built again with the
        /// current codes and details ("Correct and resend", "Build and send"). Null for documents made before 11c-4.
        /// </summary>
        public string? Request { get; private set; }

        /// <summary>MISS-11c-4: the refused document this one corrects (Egypt: its UUID is the corrected receipt's referenceOldUUID).</summary>
        public Guid? ReplacesDocumentId { get; private set; }

        /// <summary>MISS-11c-4: the corrected copy that replaced this refused document.</summary>
        public Guid? ReplacedByDocumentId { get; private set; }

        /// <summary>MISS-11c-4: the shop asked to send it although it is late (after the authority's approval): it is no longer held.</summary>
        public bool SendLate { get; private set; }

        /// <summary>Waiting or Sent: not answered by the authority yet.</summary>
        public bool IsOutstanding => State is FiscalDocumentState.Waiting or FiscalDocumentState.Sent;

        /// <summary>NotBuilt, Rejected or Late: someone must act.</summary>
        public bool NeedsAttention => State is FiscalDocumentState.NotBuilt or FiscalDocumentState.Rejected or FiscalDocumentState.Late;

        /// <summary>
        /// MISS-11c-4: a waiting document past the limit is held as Late - unless the shop asked to send it late, or it corrects a refused one
        /// (the authority takes a corrected copy after the 24 hours).
        /// </summary>
        public bool IsOverdue(DateTime nowUtc, TimeSpan limit)
            => State == FiscalDocumentState.Waiting && !SendLate && ReplacesDocumentId is null && IssuedAt < nowUtc - limit;

        public void MarkLate(string reason, DateTime nowUtc)
        {
            if (State != FiscalDocumentState.Waiting) return;
            State = FiscalDocumentState.Late;
            StateReason = reason;
            NextAttemptAt = null;
            LastAttemptAt ??= nowUtc;
        }

        /// <summary>"Send late receipts" (after the authority's approval): waiting again, now allowed past the limit.</summary>
        public void ReleaseLate()
        {
            if (State != FiscalDocumentState.Late) return;
            State = FiscalDocumentState.Waiting;
            SendLate = true;
            StateReason = null;
            NextAttemptAt = null;
        }

        /// <summary>A refused document was replaced by its corrected copy.</summary>
        public void MarkReplaced(Guid correctionId, DateTime nowUtc)
        {
            if (State != FiscalDocumentState.Rejected) return;
            State = FiscalDocumentState.Replaced;
            ReplacedByDocumentId = correctionId;
            AnsweredAt ??= nowUtc;
        }

        /// <summary>A document that could not be built at the sale, built now at the next place of the chain ("Build and send").</summary>
        public void Rebuild(long number, string? previousHash, string hash, string payload, string? qrPayload)
        {
            if (State != FiscalDocumentState.NotBuilt) return;
            (Number, PreviousHash, Hash, Payload, QrPayload) = (number, previousHash, hash, payload, qrPayload);
            (State, StateReason, NextAttemptAt) = (FiscalDocumentState.Waiting, null, null);
        }

        /// <summary>The corrected copy of a refused document: the same sale, the same place in the chain and the same previous hash.</summary>
        public static FiscalDocument Correction(FiscalDocument refused, string hash, string payload, string? qrPayload, DateTime nowUtc)
            => new()
            {
                Id = Guid.NewGuid(), Kind = refused.Kind, SourceId = refused.SourceId, Reference = refused.Reference, OriginalSourceId = refused.OriginalSourceId,
                OriginalReference = refused.OriginalReference, Country = refused.Country, TillId = refused.TillId, Number = refused.Number,
                PreviousHash = refused.PreviousHash, Hash = hash, Payload = payload, QrPayload = qrPayload, Total = refused.Total, IssuedAt = refused.IssuedAt,
                State = FiscalDocumentState.Waiting, CreatedAt = nowUtc, Request = refused.Request, ReplacesDocumentId = refused.Id,
            };

        /// <summary>The longest the sender waits between two attempts.</summary>
        public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

        /// <summary>Handed to the authority; its answer comes later (a submission to check).</summary>
        public void MarkSent(string? authorityReference, DateTime nowUtc)
        {
            if (State != FiscalDocumentState.Waiting) return;
            State = FiscalDocumentState.Sent;
            AuthorityReference = string.IsNullOrWhiteSpace(authorityReference) ? AuthorityReference : authorityReference.Trim();
            StateReason = null;
            Attempts++;
            LastAttemptAt = nowUtc;
            NextAttemptAt = null;
        }

        public void MarkAccepted(string? authorityReference, DateTime nowUtc)
        {
            if (!IsOutstanding) return;
            if (State == FiscalDocumentState.Waiting) { Attempts++; LastAttemptAt = nowUtc; }
            State = FiscalDocumentState.Accepted;
            AuthorityReference = string.IsNullOrWhiteSpace(authorityReference) ? AuthorityReference : authorityReference.Trim();
            StateReason = null;
            AnsweredAt = nowUtc;
            NextAttemptAt = null;
        }

        public void MarkRejected(string reasons, DateTime nowUtc)
        {
            if (!IsOutstanding) return;
            if (State == FiscalDocumentState.Waiting) { Attempts++; LastAttemptAt = nowUtc; }
            State = FiscalDocumentState.Rejected;
            StateReason = string.IsNullOrWhiteSpace(reasons) ? "The tax authority refused the document without a reason." : reasons.Trim();
            AnsweredAt = nowUtc;
            NextAttemptAt = null;
        }

        /// <summary>Could not be handed over (offline, the authority unavailable, the till not registered): tried again later, backing off.</summary>
        public void MarkNotSent(string reason, DateTime nowUtc)
        {
            if (State != FiscalDocumentState.Waiting) return;
            Attempts++;
            LastAttemptAt = nowUtc;
            StateReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            var wait = TimeSpan.FromMinutes(Math.Pow(2, Math.Min(Attempts - 1, 10)));
            NextAttemptAt = nowUtc + (wait > MaxBackoff ? MaxBackoff : wait);
        }

        public static FiscalDocument Issued(FiscalDocumentKind kind, Guid sourceId, string reference, Guid? originalSourceId, string? originalReference,
            FiscalCountry country, Guid tillId, long number, string? previousHash, string hash, string payload, string? qrPayload, decimal total,
            DateTime issuedAt, DateTime nowUtc, string? request = null)
            => new()
            {
                Id = Guid.NewGuid(), Kind = kind, SourceId = sourceId, Reference = reference, OriginalSourceId = originalSourceId, OriginalReference = originalReference,
                Country = country, TillId = tillId, Number = number, PreviousHash = previousHash, Hash = hash, Payload = payload, QrPayload = qrPayload,
                Total = total, IssuedAt = issuedAt, State = FiscalDocumentState.Waiting, CreatedAt = nowUtc, Request = request,
            };

        public static FiscalDocument NotBuilt(FiscalDocumentKind kind, Guid sourceId, string reference, Guid? originalSourceId, string? originalReference,
            FiscalCountry country, Guid tillId, decimal total, DateTime issuedAt, string reason, DateTime nowUtc, string? request = null)
            => new()
            {
                Id = Guid.NewGuid(), Kind = kind, SourceId = sourceId, Reference = reference, OriginalSourceId = originalSourceId, OriginalReference = originalReference,
                Country = country, TillId = tillId, Total = total, IssuedAt = issuedAt, State = FiscalDocumentState.NotBuilt,
                StateReason = string.IsNullOrWhiteSpace(reason) ? "The document could not be built." : reason.Trim(), CreatedAt = nowUtc, Request = request,
            };
    }
}
