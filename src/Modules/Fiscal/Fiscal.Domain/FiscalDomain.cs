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

        /// <summary>The seller's address in Arabic, as registered with the authority.</summary>
        public string? AddressArabic { get; private set; }

        public DateTime UpdatedAt { get; private set; }
        public string? UpdatedBy { get; private set; }

        public bool IsOn => Country != FiscalCountry.None;

        public static FiscalSettings Default() => new() { Country = FiscalCountry.None };

        /// <summary>
        /// Changes the settings. A country other than None needs its tax number in the authority's form; <paramref name="countryLocked"/>
        /// (documents were issued) refuses any change of country.
        /// </summary>
        public Result Update(FiscalCountry country, string? taxNumber, string? branchCode, string? activityCode, string? sellerNameArabic, string? addressArabic,
            bool countryLocked, string? updatedBy, DateTime nowUtc)
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

            Country = country;
            TaxNumber = tax;
            BranchCode = branch;
            ActivityCode = activity;
            SellerNameArabic = Clean(sellerNameArabic);
            AddressArabic = Clean(addressArabic);
            UpdatedBy = Clean(updatedBy);
            UpdatedAt = nowUtc;
            return Result.Success();
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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

        /// <summary>A document was issued: the country can no longer change.</summary>
        public bool HasIssued => Counter > 0;

        public static FiscalTill Create(string name, DateTime nowUtc)
            => new() { Id = Guid.NewGuid(), Name = string.IsNullOrWhiteSpace(name) ? "Till" : name.Trim(), CreatedAt = nowUtc };

        /// <summary>Takes the next number of the chain for a new document and records its hash as the chain's last link.</summary>
        public long Chain(string documentHash)
        {
            if (string.IsNullOrWhiteSpace(documentHash)) throw new ArgumentException("A fiscal document needs its hash.", nameof(documentHash));
            Counter++;
            LastDocumentHash = documentHash;
            return Counter;
        }
    }
}
