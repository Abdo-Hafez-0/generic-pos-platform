using Platform.Core.Results;

namespace Fiscal.Domain.Enums
{
    /// <summary>MISS-11b: the coding schema of a product's code for Egypt's e-receipt (itemType): a GS1 barcode, or an EGS code registered by the shop.</summary>
    public enum FiscalItemCodeType
    {
        GS1 = 1,
        EGS = 2,
    }
}

namespace Fiscal.Domain.Entities
{
    using System.Text.RegularExpressions;
    using Fiscal.Domain.Enums;

    /// <summary>MISS-11b: GS1 identification numbers (GTIN-8 / UPC-12 / EAN-13 / GTIN-14) and their check digit.</summary>
    public static class Gs1
    {
        /// <summary>True for 8, 12, 13 or 14 digits whose last digit is the GS1 check digit of the others.</summary>
        public static bool IsValid(string? code)
        {
            if (code is null || code.Length is not (8 or 12 or 13 or 14) || !code.All(char.IsAsciiDigit)) return false;
            var sum = 0;
            var weight = 3;   // from the digit next to the check digit: 3, 1, 3, 1 ...
            for (var i = code.Length - 2; i >= 0; i--, weight = weight == 3 ? 1 : 3)
                sum += (code[i] - '0') * weight;
            return (10 - sum % 10) % 10 == code[^1] - '0';
        }
    }

    /// <summary>
    /// MISS-11b: Egypt's EGS codes - the shop's own codes for goods or services, registered on the ETA portal as "EG-" + the shop's tax
    /// registration number + "-" + its internal code *(to confirm against the portal's current rules)*.
    /// </summary>
    public static partial class Egs
    {
        [GeneratedRegex(@"^EG-\d{9}-[A-Za-z0-9][A-Za-z0-9._-]{0,79}$", RegexOptions.CultureInvariant)]
        private static partial Regex Pattern();

        public static bool IsValid(string? code) => code is not null && Pattern().IsMatch(code);

        /// <summary>The code Matjar suggests for a product: EG-{RIN}-{SKU}, the SKU kept to letters, digits, dot, dash and underscore.</summary>
        public static string Suggest(string taxNumber, string sku)
        {
            var internalCode = new string((sku ?? string.Empty).Trim().ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-');
            return $"EG-{taxNumber}-{(internalCode.Length == 0 ? "ITEM" : internalCode)}";
        }
    }

    /// <summary>MISS-11b: a product's code for the tax authority (Egypt: itemType + itemCode). A product with a valid GS1 barcode needs none.</summary>
    public sealed class FiscalProductCode
    {
        private FiscalProductCode() { }

        public Guid ProductId { get; private set; }
        public FiscalItemCodeType Type { get; private set; }
        public string Code { get; private set; } = string.Empty;
        public DateTime UpdatedAt { get; private set; }
        public string? UpdatedBy { get; private set; }

        public static Result<FiscalProductCode> Create(Guid productId, FiscalItemCodeType type, string? code, string? by, DateTime nowUtc)
        {
            var made = new FiscalProductCode { ProductId = productId };
            var set = made.Change(type, code, by, nowUtc);
            return set.IsSuccess ? Result.Success(made) : Result.Failure<FiscalProductCode>(set.Error);
        }

        public Result Change(FiscalItemCodeType type, string? code, string? by, DateTime nowUtc)
        {
            var value = (code ?? string.Empty).Trim();
            if (type == FiscalItemCodeType.GS1 && !Gs1.IsValid(value))
                return Error.Validation("Fiscal.Code.Gs1Invalid", $"'{value}' is not a GS1 code (8, 12, 13 or 14 digits with a correct check digit).");
            if (type == FiscalItemCodeType.EGS && !Egs.IsValid(value))
                return Error.Validation("Fiscal.Code.EgsInvalid", $"'{value}' is not an EGS code (EG-, the 9-digit tax number, -, the product's code).");
            if (!Enum.IsDefined(type)) return Error.Validation("Fiscal.Code.TypeInvalid", "Choose GS1 or EGS.");

            (Type, Code, UpdatedBy, UpdatedAt) = (type, value, by, nowUtc);
            return Result.Success();
        }
    }

    /// <summary>MISS-11b: a unit of measure's code in the authority's unit table (Egypt's unitType, e.g. "EA", "KGM"; UN/ECE codes).</summary>
    public sealed class FiscalUnitCode
    {
        private FiscalUnitCode() { }

        public Guid UnitId { get; private set; }
        public string Code { get; private set; } = string.Empty;
        public DateTime UpdatedAt { get; private set; }

        public static Result<FiscalUnitCode> Create(Guid unitId, string? code, DateTime nowUtc)
        {
            var made = new FiscalUnitCode { UnitId = unitId };
            var set = made.Change(code, nowUtc);
            return set.IsSuccess ? Result.Success(made) : Result.Failure<FiscalUnitCode>(set.Error);
        }

        public Result Change(string? code, DateTime nowUtc)
        {
            var value = (code ?? string.Empty).Trim().ToUpperInvariant();
            if (value.Length is 0 or > 30 || !value.All(c => char.IsAsciiLetterOrDigit(c)))
                return Error.Validation("Fiscal.Code.UnitInvalid", $"'{value}' is not a unit code (letters and digits, for example EA or KGM).");
            (Code, UpdatedAt) = (value, nowUtc);
            return Result.Success();
        }
    }

    /// <summary>
    /// MISS-11b: a tax rate as the authorities classify it. Egypt: the tax type and subtype (e.g. T1 / V009 for the general 14% VAT). Saudi
    /// Arabia: the VAT category (S standard, Z zero, E exempt, O out of scope) and, for Z / E / O, the exemption reason code and text.
    /// </summary>
    public sealed class FiscalTaxCode
    {
        private FiscalTaxCode() { }

        public static readonly IReadOnlyList<string> SaudiCategories = ["S", "Z", "E", "O"];

        public Guid TaxRateId { get; private set; }
        public string? EgyptTaxType { get; private set; }
        public string? EgyptSubType { get; private set; }
        public string? SaudiCategory { get; private set; }
        public string? SaudiExemptionCode { get; private set; }
        public string? SaudiExemptionReason { get; private set; }
        public DateTime UpdatedAt { get; private set; }

        public bool IsCompleteFor(FiscalCountry country) => country switch
        {
            FiscalCountry.Egypt => EgyptTaxType is not null && EgyptSubType is not null,
            FiscalCountry.SaudiArabia => SaudiCategory == "S" || (SaudiCategory is not null && SaudiExemptionCode is not null && SaudiExemptionReason is not null),
            _ => true,
        };

        public static FiscalTaxCode For(Guid taxRateId) => new() { TaxRateId = taxRateId };

        public Result ChangeEgypt(string? taxType, string? subType, DateTime nowUtc)
        {
            var (type, sub) = (Clean(taxType), Clean(subType));
            if (type is not null && (type.Length > 30 || !type.All(char.IsAsciiLetterOrDigit)))
                return Error.Validation("Fiscal.Code.TaxTypeInvalid", $"'{type}' is not a tax type code (for example T1).");
            if (sub is not null && (sub.Length > 50 || !sub.All(char.IsAsciiLetterOrDigit)))
                return Error.Validation("Fiscal.Code.TaxSubTypeInvalid", $"'{sub}' is not a tax subtype code (for example V009).");
            (EgyptTaxType, EgyptSubType, UpdatedAt) = (type, sub, nowUtc);
            return Result.Success();
        }

        public Result ChangeSaudi(string? category, string? exemptionCode, string? exemptionReason, DateTime nowUtc)
        {
            var cat = Clean(category);
            if (cat is not null && !SaudiCategories.Contains(cat))
                return Error.Validation("Fiscal.Code.CategoryInvalid", "The VAT category is S (standard), Z (zero rated), E (exempt) or O (out of scope).");
            var code = cat is null or "S" ? null : Clean(exemptionCode);
            var reason = cat is null or "S" ? null : string.IsNullOrWhiteSpace(exemptionReason) ? null : exemptionReason.Trim();
            (SaudiCategory, SaudiExemptionCode, SaudiExemptionReason, UpdatedAt) = (cat, code, reason, nowUtc);
            return Result.Success();
        }

        private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
    }
}
