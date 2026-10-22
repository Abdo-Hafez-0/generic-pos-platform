using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Platform.Core.Results;

namespace Fiscal.Saudi;

/// <summary>MISS-11d: Saudi Arabia's settings (section "Fiscal:Saudi").</summary>
public sealed class SaudiInvoiceOptions
{
    public const string SectionName = "Fiscal:Saudi";

    public string Currency { get; set; } = "SAR";

    /// <summary>The invoice's issue date and time are the shop's local time - Saudi Arabia, UTC+3, no daylight saving.</summary>
    public double UtcOffsetHours { get; set; } = 3;
}

/// <summary>
/// MISS-11d: builds ZATCA's simplified tax invoice (a sale, type 388) or simplified credit note (a return, type 381; owner's decision: in
/// 11d, with the return's reason) as UBL 2.1 XML at its place in the till's chain: the invoice counter (ICV) and the previous invoice's
/// hash (PIH), the invoice's own hash (the chain's next link) and - before the till is onboarded (owner's decision: a short QR now, the
/// invoice signed later) - the QR's first five fields. The stamp (signature, tags 6-9) is added later without changing the hash. Pure and
/// offline.
///
/// Matjar's prices include VAT, ZATCA's lines are net: each line's net amount is its charged total divided by (1 + rate), to the halala;
/// its VAT is the net times the rate; the net unit price is the net divided by the quantity. Each VAT category's VAT is its taxable amount
/// times its rate (ZATCA's rule), and the invoice's payable amount is what was charged, any difference of a halala or so being the
/// rounding amount *(to confirm against ZATCA's validator: PayableRoundingAmount, and the QR's time without a zone)*.
/// </summary>
internal static class ZatcaInvoiceBuilder
{
    /// <summary>The PIH of the till's first invoice: ZATCA's fixed value, the Base64 of the SHA-256 (hex) of "0".</summary>
    public const string FirstPreviousHash = "NWZlY2ViNjZmZmM4NmYzOGQ5NTI3ODZjNmQ2OTZjNzljMmRiYzIzOWRkNGU5MWI0NjcyOWQ3M2EyN2ZiNTdlOQ==";

    public const string ProfileId = "reporting:1.0";
    public const string InvoiceType = "388";
    public const string CreditNoteType = "381";

    /// <summary>The transaction's kind (KSA-2): "02" a simplified invoice, then no third party, nominal, export, summary or self-billing.</summary>
    public const string SimplifiedName = "0200000";

    /// <summary>When a return has no reason (made before 11d), the credit note says so in general words.</summary>
    public const string DefaultReturnReason = "Goods returned by the customer";

    public const int MaxLines = 1000;

    /// <summary>The built invoice: the XML sent, its hash (Base64), its UUID, the QR's Base64 text, its totals, the document (for the tests).</summary>
    public sealed record Built(string Xml, string Hash, string Uuid, string Qr, ZatcaTotals Totals, XDocument Document);

    /// <summary>The invoice's totals as ZATCA reads them (BT-106, BT-109, BT-110, BT-112, BT-114, BT-115).</summary>
    public sealed record ZatcaTotals(decimal LineExtension, decimal TaxExclusive, decimal Tax, decimal TaxInclusive, decimal Rounding, decimal Payable);

    public static Result<Built> Build(FiscalBuildInput input, SaudiInvoiceOptions options)
    {
        var request = input.Request;
        var settings = input.Settings;
        var isReturn = request.Kind == FiscalDocumentKindContract.Return;

        var problem = CheckSeller(settings);
        if (problem is not null) return Fail("Fiscal.Saudi.SellerIncomplete", problem);
        if (request.Lines.Count == 0) return Fail("Fiscal.Saudi.NoLines", "An invoice needs at least one line.");
        if (request.Lines.Count > MaxLines) return Fail("Fiscal.Saudi.TooManyLines", $"An invoice carries at most {MaxLines} lines.");
        if (isReturn && string.IsNullOrWhiteSpace(request.OriginalReference))
            return Fail("Fiscal.Saudi.NoOriginal", "A return must name the sale it returns, so its credit note can refer to that invoice.");

        // the lines, each with its VAT category (MISS-11b)
        var missing = new List<string>();
        var lines = new List<Line>();
        foreach (var line in request.Lines)
        {
            var codes = input.LineCodes?.GetValueOrDefault(line.ProductId);
            var category = codes?.SaudiCategory;
            if (category is null || (category != "S" && (codes!.SaudiExemptionCode is null || codes.SaudiExemptionReason is null)))
            {
                missing.Add($"'{line.Name}'");
                continue;
            }

            if (line.Quantity <= 0m) return Fail("Fiscal.Saudi.QuantityInvalid", $"'{line.Name}' has no quantity.");
            if ((category == "S") != (line.TaxRate > 0m))
                return Fail("Fiscal.Saudi.CategoryMismatch", category == "S"
                    ? $"'{line.Name}' is sold without VAT, but its tax rate's VAT category is S (standard). Choose Z, E or O for a rate of 0%."
                    : $"'{line.Name}' is sold with VAT, but its tax rate's VAT category is {category}. Choose S for a rate above 0%.");
            lines.Add(Line.Of(line, category, category == "S" ? null : codes!.SaudiExemptionCode, category == "S" ? null : codes!.SaudiExemptionReason, codes!.UnitCode));
        }

        if (missing.Count > 0)
            return Fail("Fiscal.Saudi.CodesMissing", "Without a VAT category for ZATCA: " + string.Join("; ", missing.Take(5))
                + (missing.Count > 5 ? $" and {missing.Count - 5} more" : string.Empty) + ". Set the tax rates' categories on the Electronic receipts screen.");

        // the totals: each category's VAT is its taxable amount times its rate; what was charged is payable
        var categories = lines.GroupBy(l => (l.Category, l.Percent, l.ExemptionCode, l.ExemptionReason))
            .Select(g => (g.Key.Category, g.Key.Percent, g.Key.ExemptionCode, g.Key.ExemptionReason, Taxable: g.Sum(l => l.Net),
                Tax: R2(g.Sum(l => l.Net) * g.Key.Percent / 100m)))
            .ToList();
        var net = lines.Sum(l => l.Net);
        var tax = categories.Sum(c => c.Tax);
        var charged = request.Lines.Sum(l => l.Total);
        var totals = new ZatcaTotals(net, net, tax, net + tax, charged - (net + tax), charged);

        var local = DateTime.SpecifyKind(request.IssuedAtUtc, DateTimeKind.Utc).AddHours(options.UtcOffsetHours);
        var (date, time) = (local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), local.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        var uuid = Uuid(input);
        var previous = string.IsNullOrWhiteSpace(input.PreviousHash) ? FirstPreviousHash : input.PreviousHash;
        var currency = options.Currency;

        XElement Amount(XName name, decimal value) => new(name, new XAttribute("currencyID", currency), Money(value));
        XNamespace cac = ZatcaXml.Cac, cbc = ZatcaXml.Cbc;

        var invoice = new XElement(ZatcaXml.Invoice + "Invoice",
            new XAttribute("xmlns", ZatcaXml.Invoice.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cac", cac.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cbc", cbc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ext", ZatcaXml.Ext.NamespaceName),
            new XElement(cbc + "ProfileID", ProfileId),
            new XElement(cbc + "ID", Clean(request.Reference, 127)),
            new XElement(cbc + "UUID", uuid),
            new XElement(cbc + "IssueDate", date),
            new XElement(cbc + "IssueTime", time),
            new XElement(cbc + "InvoiceTypeCode", new XAttribute("name", SimplifiedName), isReturn ? CreditNoteType : InvoiceType),
            new XElement(cbc + "DocumentCurrencyCode", currency),
            new XElement(cbc + "TaxCurrencyCode", currency),
            isReturn ? new XElement(cac + "BillingReference", new XElement(cac + "InvoiceDocumentReference",
                new XElement(cbc + "ID", Clean(request.OriginalReference, 127)))) : null,
            new XElement(cac + "AdditionalDocumentReference", new XElement(cbc + "ID", "ICV"), new XElement(cbc + "UUID", input.Number.ToString(CultureInfo.InvariantCulture))),
            new XElement(cac + "AdditionalDocumentReference", new XElement(cbc + "ID", "PIH"),
                new XElement(cac + "Attachment", new XElement(cbc + "EmbeddedDocumentBinaryObject", new XAttribute("mimeCode", "text/plain"), previous))),
            Seller(settings, cac, cbc),
            Buyer(isReturn ? null : request.BuyerName, cac, cbc),
            new XElement(cac + "PaymentMeans",
                new XElement(cbc + "PaymentMeansCode", PaymentCode(request.Payments)),
                isReturn ? new XElement(cbc + "InstructionNote", Clean(string.IsNullOrWhiteSpace(request.Reason) ? DefaultReturnReason : request.Reason, 1000)) : null),
            new XElement(cac + "TaxTotal", Amount(cbc + "TaxAmount", totals.Tax)),
            new XElement(cac + "TaxTotal", Amount(cbc + "TaxAmount", totals.Tax),
                categories.Select(c => new XElement(cac + "TaxSubtotal",
                    Amount(cbc + "TaxableAmount", c.Taxable),
                    Amount(cbc + "TaxAmount", c.Tax),
                    new XElement(cac + "TaxCategory",
                        new XElement(cbc + "ID", new XAttribute("schemeID", "UN/ECE 5305"), new XAttribute("schemeAgencyID", "6"), c.Category),
                        new XElement(cbc + "Percent", Money(c.Percent)),
                        c.ExemptionCode is { } code ? new XElement(cbc + "TaxExemptionReasonCode", code) : null,
                        c.ExemptionReason is { } why ? new XElement(cbc + "TaxExemptionReason", Clean(why, 1000)) : null,
                        new XElement(cac + "TaxScheme", new XElement(cbc + "ID", new XAttribute("schemeID", "UN/ECE 5153"), new XAttribute("schemeAgencyID", "6"), "VAT")))))),
            new XElement(cac + "LegalMonetaryTotal",
                Amount(cbc + "LineExtensionAmount", totals.LineExtension),
                Amount(cbc + "TaxExclusiveAmount", totals.TaxExclusive),
                Amount(cbc + "TaxInclusiveAmount", totals.TaxInclusive),
                Amount(cbc + "AllowanceTotalAmount", 0m),
                Amount(cbc + "PrepaidAmount", 0m),
                totals.Rounding != 0m ? Amount(cbc + "PayableRoundingAmount", totals.Rounding) : null,
                Amount(cbc + "PayableAmount", totals.Payable)),
            lines.Select((l, i) => new XElement(cac + "InvoiceLine",
                new XElement(cbc + "ID", (i + 1).ToString(CultureInfo.InvariantCulture)),
                new XElement(cbc + "InvoicedQuantity", new XAttribute("unitCode", l.UnitCode), Quantity(l.Quantity)),
                Amount(cbc + "LineExtensionAmount", l.Net),
                new XElement(cac + "TaxTotal", Amount(cbc + "TaxAmount", l.Tax), Amount(cbc + "RoundingAmount", l.Net + l.Tax)),
                new XElement(cac + "Item",
                    new XElement(cbc + "Name", Clean(l.Name, 1000)),
                    new XElement(cac + "ClassifiedTaxCategory",
                        new XElement(cbc + "ID", l.Category),
                        new XElement(cbc + "Percent", Money(l.Percent)),
                        new XElement(cac + "TaxScheme", new XElement(cbc + "ID", "VAT")))),
                new XElement(cac + "Price", new XElement(cbc + "PriceAmount", new XAttribute("currencyID", currency), Price(l.Price))))));

        var xml = ZatcaXml.Write(new XDocument(invoice));
        var sent = ZatcaXml.Read(xml);
        var hash = ZatcaXml.Hash(sent);
        var qr = ShortQr(settings, date, time, totals);
        return Result.Success(new Built(xml, hash, uuid, qr, totals, sent));
    }

    /// <summary>
    /// Owner's decision (before onboarding): the QR's first five fields - the seller's name, the VAT number, the invoice's time, the total
    /// with VAT and the VAT total (tags 1-5).
    /// </summary>
    public static string ShortQr(FiscalSettings settings, string date, string time, ZatcaTotals totals)
        => ZatcaQr.Encode([
            ZatcaQr.Text(1, settings.SellerNameArabic!.Trim()),
            ZatcaQr.Text(2, settings.TaxNumber!),
            ZatcaQr.Text(3, date + "T" + time),
            ZatcaQr.Text(4, Money(totals.TaxInclusive)),
            ZatcaQr.Text(5, Money(totals.Tax)),
        ]);

    /// <summary>
    /// ZATCA's payment means (UNTDID 4461) of the largest payment, like Egypt's (owner's decision for 11c): cash 10, card 48 (a bank card),
    /// anything else 1 (not defined); none recorded = cash.
    /// </summary>
    public static string PaymentCode(IReadOnlyList<FiscalPaymentRequest> payments)
    {
        var largest = payments.Where(p => p.Amount > 0m).OrderByDescending(p => p.Amount).FirstOrDefault();
        return largest?.Method switch
        {
            null or "Cash" => "10",
            "Card" => "48",
            _ => "1",
        };
    }

    /// <summary>
    /// The invoice's UUID: made from the till, the place in the chain and the sale, so building the same document again gives the same one
    /// (a random-looking version 4 UUID).
    /// </summary>
    private static string Uuid(FiscalBuildInput input)
    {
        var seed = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"matjar-zatca|{input.TillId:N}|{input.Number}|{input.Request.Kind}|{input.Request.SourceId:N}|{input.ReferenceOldHash}"));
        var bytes = seed[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);   // version 4
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(bytes, bigEndian: true).ToString("D");
    }

    private static XElement Seller(FiscalSettings settings, XNamespace cac, XNamespace cbc)
    {
        var a = settings.Address;
        return new XElement(cac + "AccountingSupplierParty", new XElement(cac + "Party",
            new XElement(cac + "PartyIdentification", new XElement(cbc + "ID", new XAttribute("schemeID", settings.SellerIdScheme!), settings.SellerId)),
            new XElement(cac + "PostalAddress",
                new XElement(cbc + "StreetName", Clean(a.Street, 1000)),
                new XElement(cbc + "BuildingNumber", a.BuildingNumber),
                a.AdditionalNumber is { } extra ? new XElement(cbc + "PlotIdentification", extra) : null,
                new XElement(cbc + "CitySubdivisionName", Clean(a.District, 1000)),
                new XElement(cbc + "CityName", Clean(a.City, 1000)),
                new XElement(cbc + "PostalZone", a.PostalCode),
                new XElement(cac + "Country", new XElement(cbc + "IdentificationCode", "SA"))),
            new XElement(cac + "PartyTaxScheme", new XElement(cbc + "CompanyID", settings.TaxNumber), new XElement(cac + "TaxScheme", new XElement(cbc + "ID", "VAT"))),
            new XElement(cac + "PartyLegalEntity", new XElement(cbc + "RegistrationName", Clean(settings.SellerNameArabic, 1000)))));
    }

    /// <summary>A simplified invoice needs no buyer; a name given at the till is carried *(to confirm: ZATCA asks nothing more with it)*.</summary>
    private static XElement Buyer(string? name, XNamespace cac, XNamespace cbc)
        => string.IsNullOrWhiteSpace(name)
            ? new XElement(cac + "AccountingCustomerParty")
            : new XElement(cac + "AccountingCustomerParty", new XElement(cac + "Party",
                new XElement(cac + "PartyLegalEntity", new XElement(cbc + "RegistrationName", Clean(name, 1000)))));

    /// <summary>What the seller's details lack for ZATCA's invoice, or null.</summary>
    private static string? CheckSeller(FiscalSettings settings)
    {
        var lacks = new List<string>();
        if (string.IsNullOrWhiteSpace(settings.TaxNumber)) lacks.Add("the VAT number");
        if (string.IsNullOrWhiteSpace(settings.SellerNameArabic)) lacks.Add("the shop's legal name");
        if (string.IsNullOrWhiteSpace(settings.SellerIdScheme) || string.IsNullOrWhiteSpace(settings.SellerId)) lacks.Add("the shop's identification (CRN)");
        var a = settings.Address;
        if (a.Street is null || a.BuildingNumber is null || a.District is null || a.City is null || a.PostalCode is null)
            lacks.Add("the national address (street, building number, district, city and postal code)");
        return lacks.Count == 0 ? null : "The shop's details for ZATCA are incomplete: " + string.Join(", ", lacks) + ". Complete them on the Electronic receipts screen.";
    }

    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Price(decimal value) => value.ToString("0.00####", CultureInfo.InvariantCulture);

    private static string Quantity(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static decimal R2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Clean(string? value, int max)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= max ? text : text[..max];
    }

    private static Result<Built> Fail(string code, string description) => Result.Failure<Built>(Error.Validation(code, description));

    /// <summary>One invoice line as ZATCA reads it: net of VAT, its VAT, the net unit price.</summary>
    private sealed record Line(string Name, decimal Quantity, decimal Net, decimal Tax, decimal Price, string Category, decimal Percent, string? ExemptionCode,
        string? ExemptionReason, string UnitCode)
    {
        public static Line Of(FiscalLineRequest line, string category, string? exemptionCode, string? exemptionReason, string? unitCode)
        {
            var rate = line.TaxRate;   // Matjar's rates are fractions (0.15 = 15%)
            var net = rate > 0m ? R2(line.Total / (1m + rate)) : line.Total;
            var tax = R2(net * rate);
            var price = decimal.Round(net / line.Quantity, 6, MidpointRounding.AwayFromZero);
            return new Line(line.Name, line.Quantity, net, tax, price, category, rate * 100m, exemptionCode, exemptionReason,
                string.IsNullOrWhiteSpace(unitCode) ? "PCE" : unitCode.Trim());
        }
    }
}
