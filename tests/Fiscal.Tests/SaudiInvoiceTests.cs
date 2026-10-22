using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Saudi;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11d-1: ZATCA's simplified tax invoice and credit note - the UBL XML, the ICV and PIH chain, the hash (the canonical XML without the
/// stamp and the QR, SHA-256, Base64; checked against .NET's own C14N), the amounts net of VAT, the VAT categories, the credit note's
/// reference and reason, and the QR's first five fields before onboarding. Checked against ZATCA's Security Features Implementation
/// Standards v1.2 and the UBL 2.1 order of elements.
/// </summary>
public sealed class SaudiInvoiceTests
{
    private static readonly Guid Water = Guid.NewGuid(), Dates = Guid.NewGuid(), Bread = Guid.NewGuid();
    private static readonly DateTime IssuedAt = new(2026, 10, 22, 9, 30, 15, DateTimeKind.Utc);   // 12:30:15 in Riyadh
    private static readonly SaudiInvoiceOptions Options = new();
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ubl = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";

    internal static readonly FiscalAddress Riyadh = new(null, "الرياض", "طريق الملك فهد", "2322", "12214", District: "العليا", AdditionalNumber: "7788");

    internal static FiscalSettings Settings(FiscalAddress? address = null, string scheme = "CRN")
    {
        var settings = FiscalSettings.Default();
        var saved = settings.Update(FiscalCountry.SaudiArabia, "300000000000003", null, null, "متجر النور & شركاه", address ?? Riyadh, false, "owner", IssuedAt,
            scheme, "1010010000");
        Assert.True(saved.IsSuccess, saved.IsFailure ? saved.Error.Description : null);
        return settings;
    }

    private static readonly Dictionary<Guid, FiscalLineCodes> Codes = new()
    {
        [Water] = new(null, null, "PCE", null, null, "S", null, null),
        [Dates] = new(null, null, null, null, null, "S", null, null),
        [Bread] = new(null, null, null, null, null, "Z", "VATEX-SA-35", "Medicines and medical equipment"),
    };

    /// <summary>Water 2 x 1.15 and Dates 1 x 2.03 (15% included): the till's VAT-included amounts.</summary>
    private static FiscalDocumentRequest Sale(IReadOnlyList<FiscalPaymentRequest>? payments = null, string? buyer = null, IReadOnlyList<FiscalLineRequest>? lines = null)
    {
        lines ??=
        [
            new FiscalLineRequest(Water, "WATER", "مياه 500 مل", 2m, 1.15m, 0m, 2.30m, 0.15m, 0.30m),
            new FiscalLineRequest(Dates, "DATES", "Dates <Ajwa>", 1m, 2.03m, 0m, 2.03m, 0.15m, 0.26m),
        ];
        var total = lines.Sum(l => l.Total);
        return new FiscalDocumentRequest(FiscalDocumentKindContract.Sale, Guid.NewGuid(), "S-1A2B3C4D", IssuedAt, lines, total, lines.Sum(l => l.Tax),
            payments ?? [new FiscalPaymentRequest("Cash", total)], "owner", buyer);
    }

    private static FiscalBuildInput Input(FiscalDocumentRequest request, string? previous = null, long number = 1, FiscalSettings? settings = null,
        Dictionary<Guid, FiscalLineCodes>? codes = null, Guid? till = null)
        => new(request, settings ?? Settings(), till ?? Guid.Parse("8d1f3a52-0000-4000-8000-000000000001"), "SHOP-PC", number, previous, codes ?? Codes);

    private static ZatcaInvoiceBuilder.Built Build(FiscalBuildInput input)
    {
        var built = ZatcaInvoiceBuilder.Build(input, Options);
        Assert.True(built.IsSuccess, built.IsFailure ? built.Error.Description : null);
        return built.Value;
    }

    private static XElement Root(ZatcaInvoiceBuilder.Built built) => built.Document.Root!;

    private static string Value(XElement element, params XName[] path)
    {
        var at = element;
        foreach (var name in path) at = at.Element(name) ?? throw new Xunit.Sdk.XunitException($"no {name} under {at.Name}");
        return at.Value;
    }

    private static XElement Reference(ZatcaInvoiceBuilder.Built built, string id)
        => Root(built).Elements(Cac + "AdditionalDocumentReference").Single(r => (string?)r.Element(Cbc + "ID") == id);

    /// <summary>.NET's own canonical XML (System.Security.Cryptography.Xml), as an independent reference for Matjar's.</summary>
    private static string DotNetCanonical(string xml)
    {
        var document = new XmlDocument { PreserveWhitespace = true };
        document.LoadXml(xml);
        var transform = new XmlDsigC14NTransform(includeComments: false);
        transform.LoadInput(document);
        using var reader = new StreamReader((Stream)transform.GetOutput(typeof(Stream)), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    [Fact]
    public void A_sale_is_a_simplified_tax_invoice_in_UBL_with_its_counter_and_the_previous_hash()
    {
        var built = Build(Input(Sale(), previous: "cHJldmlvdXM=", number: 42));
        var root = Root(built);

        Assert.Equal(Ubl + "Invoice", root.Name);
        Assert.Equal(("reporting:1.0", "S-1A2B3C4D", built.Uuid), (Value(root, Cbc + "ProfileID"), Value(root, Cbc + "ID"), Value(root, Cbc + "UUID")));
        Assert.Equal(("2026-10-22", "12:30:15"), (Value(root, Cbc + "IssueDate"), Value(root, Cbc + "IssueTime")));   // Riyadh time
        var type = root.Element(Cbc + "InvoiceTypeCode")!;
        Assert.Equal(("388", "0200000"), (type.Value, (string?)type.Attribute("name")));
        Assert.Equal(("SAR", "SAR"), (Value(root, Cbc + "DocumentCurrencyCode"), Value(root, Cbc + "TaxCurrencyCode")));
        Assert.Equal("42", Value(Reference(built, "ICV"), Cbc + "UUID"));
        var pih = Reference(built, "PIH").Element(Cac + "Attachment")!.Element(Cbc + "EmbeddedDocumentBinaryObject")!;
        Assert.Equal(("cHJldmlvdXM=", "text/plain"), (pih.Value, (string?)pih.Attribute("mimeCode")));
        Assert.Null(root.Element(Cac + "BillingReference"));
        Assert.Empty(root.Elements(Cac + "AccountingCustomerParty").Elements());   // a simplified invoice needs no buyer

        // the UBL 2.1 order of the invoice's elements
        Assert.Equal(
            ["ProfileID", "ID", "UUID", "IssueDate", "IssueTime", "InvoiceTypeCode", "DocumentCurrencyCode", "TaxCurrencyCode", "AdditionalDocumentReference",
             "AdditionalDocumentReference", "AccountingSupplierParty", "AccountingCustomerParty", "PaymentMeans", "TaxTotal", "TaxTotal", "LegalMonetaryTotal",
             "InvoiceLine", "InvoiceLine"],
            root.Elements().Select(e => e.Name.LocalName));
        Assert.DoesNotContain("\n", built.Xml);   // written without indentation: the hash cannot depend on formatting
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?><Invoice xmlns=", built.Xml);
    }

    [Fact]
    public void The_seller_carries_its_identification_VAT_number_name_and_national_address()
    {
        var seller = Root(Build(Input(Sale()))).Element(Cac + "AccountingSupplierParty")!.Element(Cac + "Party")!;

        var id = seller.Element(Cac + "PartyIdentification")!.Element(Cbc + "ID")!;
        Assert.Equal(("CRN", "1010010000"), ((string?)id.Attribute("schemeID"), id.Value));
        var address = seller.Element(Cac + "PostalAddress")!;
        Assert.Equal(["StreetName", "BuildingNumber", "PlotIdentification", "CitySubdivisionName", "CityName", "PostalZone", "Country"],
            address.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(("طريق الملك فهد", "2322", "7788", "العليا", "الرياض", "12214", "SA"),
            (Value(address, Cbc + "StreetName"), Value(address, Cbc + "BuildingNumber"), Value(address, Cbc + "PlotIdentification"),
             Value(address, Cbc + "CitySubdivisionName"), Value(address, Cbc + "CityName"), Value(address, Cbc + "PostalZone"),
             Value(address, Cac + "Country", Cbc + "IdentificationCode")));
        Assert.Equal(("300000000000003", "VAT"), (Value(seller, Cac + "PartyTaxScheme", Cbc + "CompanyID"), Value(seller, Cac + "PartyTaxScheme", Cac + "TaxScheme", Cbc + "ID")));
        Assert.Equal("متجر النور & شركاه", Value(seller, Cac + "PartyLegalEntity", Cbc + "RegistrationName"));

        // no additional number: no PlotIdentification
        var plain = Root(Build(Input(Sale(), settings: Settings(Riyadh with { AdditionalNumber = null }))));
        Assert.Empty(plain.Descendants(Cbc + "PlotIdentification"));
    }

    [Fact]
    public void The_lines_are_net_of_VAT_and_what_was_charged_is_payable_with_the_halala_as_rounding()
    {
        // Water 2.30 -> net 2.00, VAT 0.30; Dates 2.03 -> net 1.77 (2.03 / 1.15 = 1.7652), VAT 0.27 (0.2655)
        var built = Build(Input(Sale()));
        var root = Root(built);

        var lines = root.Elements(Cac + "InvoiceLine").ToList();
        Assert.Equal(("1", "2", "PCE"), (Value(lines[0], Cbc + "ID"), Value(lines[0], Cbc + "InvoicedQuantity"), (string?)lines[0].Element(Cbc + "InvoicedQuantity")!.Attribute("unitCode")));
        Assert.Equal(("2.00", "0.30", "2.30", "1.00"), (Value(lines[0], Cbc + "LineExtensionAmount"), Value(lines[0], Cac + "TaxTotal", Cbc + "TaxAmount"),
            Value(lines[0], Cac + "TaxTotal", Cbc + "RoundingAmount"), Value(lines[0], Cac + "Price", Cbc + "PriceAmount")));
        Assert.Equal(("1.77", "0.27", "2.04", "PCE"), (Value(lines[1], Cbc + "LineExtensionAmount"), Value(lines[1], Cac + "TaxTotal", Cbc + "TaxAmount"),
            Value(lines[1], Cac + "TaxTotal", Cbc + "RoundingAmount"), (string?)lines[1].Element(Cbc + "InvoicedQuantity")!.Attribute("unitCode")));   // no unit code: PCE
        Assert.Equal(("Dates <Ajwa>", "S", "15.00", "VAT"), (Value(lines[1], Cac + "Item", Cbc + "Name"), Value(lines[1], Cac + "Item", Cac + "ClassifiedTaxCategory", Cbc + "ID"),
            Value(lines[1], Cac + "Item", Cac + "ClassifiedTaxCategory", Cbc + "Percent"), Value(lines[1], Cac + "Item", Cac + "ClassifiedTaxCategory", Cac + "TaxScheme", Cbc + "ID")));

        // the category's VAT is its taxable amount times its rate: 3.77 x 15% = 0.5655 -> 0.57; 3.77 + 0.57 = 4.34, charged 4.33
        var subtotal = root.Elements(Cac + "TaxTotal").Last().Element(Cac + "TaxSubtotal")!;
        Assert.Equal(("3.77", "0.57", "S", "15.00"), (Value(subtotal, Cbc + "TaxableAmount"), Value(subtotal, Cbc + "TaxAmount"),
            Value(subtotal, Cac + "TaxCategory", Cbc + "ID"), Value(subtotal, Cac + "TaxCategory", Cbc + "Percent")));
        Assert.Equal(["0.57", "0.57"], root.Elements(Cac + "TaxTotal").Select(t => Value(t, Cbc + "TaxAmount")));
        Assert.Single(root.Elements(Cac + "TaxTotal").First().Elements());   // the first holds the total only
        var money = root.Element(Cac + "LegalMonetaryTotal")!;
        Assert.Equal(["LineExtensionAmount", "TaxExclusiveAmount", "TaxInclusiveAmount", "AllowanceTotalAmount", "PrepaidAmount", "PayableRoundingAmount", "PayableAmount"],
            money.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(["3.77", "3.77", "4.34", "0.00", "0.00", "-0.01", "4.33"], money.Elements().Select(e => e.Value));
        Assert.All(root.Descendants().Where(e => e.Attribute("currencyID") is not null), e => Assert.Equal("SAR", (string?)e.Attribute("currencyID")));
        Assert.Equal(new ZatcaInvoiceBuilder.ZatcaTotals(3.77m, 3.77m, 0.57m, 4.34m, -0.01m, 4.33m), built.Totals);

        // when the amounts meet, no rounding amount
        var even = Root(Build(Input(Sale(lines: [new FiscalLineRequest(Water, "WATER", "Water", 3m, 1.15m, 0m, 3.45m, 0.15m, 0.45m)]))));
        Assert.Empty(even.Descendants(Cbc + "PayableRoundingAmount"));
        Assert.Equal("3.45", Value(even, Cac + "LegalMonetaryTotal", Cbc + "PayableAmount"));
    }

    [Fact]
    public void A_discounted_or_weighed_line_carries_its_net_unit_price()
    {
        // 0.375 kg at 12.00 = 4.50 with 0.50 off -> charged 4.00, net 3.48 (4.00 / 1.15 = 3.478), price 3.48 / 0.375 = 9.28
        var root = Root(Build(Input(Sale(lines: [new FiscalLineRequest(Dates, "DATES", "Dates", 0.375m, 12m, 0.50m, 4.00m, 0.15m, 0.52m)]))));
        var line = root.Element(Cac + "InvoiceLine")!;

        Assert.Equal(("0.375", "3.48", "9.28"), (Value(line, Cbc + "InvoicedQuantity"), Value(line, Cbc + "LineExtensionAmount"), Value(line, Cac + "Price", Cbc + "PriceAmount")));
        var third = Root(Build(Input(Sale(lines: [new FiscalLineRequest(Water, "WATER", "Water", 3m, 1m, 0m, 3.00m, 0.15m, 0.39m)]))));
        Assert.Equal("0.87", Value(third.Element(Cac + "InvoiceLine")!, Cac + "Price", Cbc + "PriceAmount"));   // 2.61 / 3
    }

    [Fact]
    public void A_zero_rated_line_carries_its_exemption_reason_in_its_category()
    {
        var lines = new[]
        {
            new FiscalLineRequest(Water, "WATER", "Water", 1m, 1.15m, 0m, 1.15m, 0.15m, 0.15m),
            new FiscalLineRequest(Bread, "MED", "Medicine", 2m, 5m, 0m, 10m, 0m, 0m),
        };
        var root = Root(Build(Input(Sale(lines: lines))));

        var zero = root.Elements(Cac + "TaxTotal").Last().Elements(Cac + "TaxSubtotal").Single(s => Value(s, Cac + "TaxCategory", Cbc + "ID") == "Z");
        Assert.Equal(["ID", "Percent", "TaxExemptionReasonCode", "TaxExemptionReason", "TaxScheme"], zero.Element(Cac + "TaxCategory")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(("10.00", "0.00", "0.00", "VATEX-SA-35", "Medicines and medical equipment"), (Value(zero, Cbc + "TaxableAmount"), Value(zero, Cbc + "TaxAmount"),
            Value(zero, Cac + "TaxCategory", Cbc + "Percent"), Value(zero, Cac + "TaxCategory", Cbc + "TaxExemptionReasonCode"),
            Value(zero, Cac + "TaxCategory", Cbc + "TaxExemptionReason")));
        Assert.Equal(["11.00", "11.00", "11.15", "11.15"], new[] { "LineExtensionAmount", "TaxExclusiveAmount", "TaxInclusiveAmount", "PayableAmount" }
            .Select(n => Value(root, Cac + "LegalMonetaryTotal", Cbc + n)));
    }

    [Fact]
    public void A_line_whose_rate_does_not_match_its_category_or_without_a_category_is_not_built()
    {
        var zeroAsStandard = ZatcaInvoiceBuilder.Build(Input(Sale(lines: [new FiscalLineRequest(Water, "WATER", "Water", 1m, 1m, 0m, 1m, 0m, 0m)])), Options);
        var vatAsZero = ZatcaInvoiceBuilder.Build(Input(Sale(lines: [new FiscalLineRequest(Bread, "MED", "Medicine", 1m, 1.15m, 0m, 1.15m, 0.15m, 0.15m)])), Options);
        var missing = ZatcaInvoiceBuilder.Build(Input(Sale(), codes: new() { [Water] = Codes[Water] }), Options);
        var noReason = ZatcaInvoiceBuilder.Build(Input(Sale(lines: [new FiscalLineRequest(Bread, "MED", "Medicine", 1m, 1m, 0m, 1m, 0m, 0m)]),
            codes: new() { [Bread] = Codes[Bread] with { SaudiExemptionReason = null } }), Options);

        Assert.Equal("Fiscal.Saudi.CategoryMismatch", zeroAsStandard.Error.Code);
        Assert.Equal("Fiscal.Saudi.CategoryMismatch", vatAsZero.Error.Code);
        Assert.Equal("Fiscal.Saudi.CodesMissing", missing.Error.Code);
        Assert.Contains("'Dates <Ajwa>'", missing.Error.Description);
        Assert.Equal("Fiscal.Saudi.CodesMissing", noReason.Error.Code);
    }

    [Fact]
    public void The_hash_is_the_SHA256_of_the_canonical_invoice_without_the_stamp_and_the_QR_in_Base64()
    {
        var built = Build(Input(Sale(buyer: "عبدالله")));

        // independently: .NET's C14N of the invoice as sent (it has no stamp or QR yet)
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(DotNetCanonical(built.Xml))));
        Assert.Equal(expected, built.Hash);
        Assert.Equal(44, built.Hash.Length);   // 32 bytes in Base64

        // the stamp (UBLExtensions, cac:Signature) and the QR added later do not change it - so the invoice can be signed after the sale
        var stamped = XDocument.Parse(built.Xml, LoadOptions.PreserveWhitespace);
        XNamespace ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
        stamped.Root!.AddFirst(new XElement(ext + "UBLExtensions", new XElement(ext + "UBLExtension", new XElement(ext + "ExtensionContent", "signature"))));
        stamped.Root.Elements(Cac + "AdditionalDocumentReference").Last().AddAfterSelf(
            new XElement(Cac + "AdditionalDocumentReference", new XElement(Cbc + "ID", "QR"), new XElement(Cac + "Attachment", new XElement(Cbc + "EmbeddedDocumentBinaryObject", "AQID"))),
            new XElement(Cac + "Signature", new XElement(Cbc + "ID", "urn:oasis:names:specification:ubl:signature:Invoice")));
        Assert.Equal(built.Hash, ZatcaXml.Hash(stamped));

        // anything else changes it
        stamped.Root.Element(Cbc + "ID")!.Value = "S-OTHER";
        Assert.NotEqual(built.Hash, ZatcaXml.Hash(stamped));
    }

    [Fact]
    public void Matjars_canonical_XML_is_the_W3C_one()
    {
        // attributes in order, namespaces declared once, empty elements opened and closed, characters escaped, whitespace kept (a carriage
        // return as a character reference: .NET's XmlDocument does not normalise a literal one as the XML parsers do)
        const string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!-- comment -->\n<a:root xmlns:b=\"urn:b\" xmlns:a=\"urn:a\" z=\"2\" b:y=\"1\" a=\"&quot;q&quot; &lt;&#9;&#10;\">\n"
            + "  <a:empty/>\n  <b:child xmlns:b=\"urn:b\" xmlns=\"urn:d\"><inner attr='x &amp; y'>1 &lt; 2 &gt; 0 &amp; \"q\"&#13;\n</inner><x:other xmlns:x=\"urn:x\"/></b:child>\n"
            + "  <![CDATA[<cdata & text>]]><?pi data?>\n</a:root>\n<?after?>";

        Assert.Equal(DotNetCanonical(xml), ZatcaXml.Canonical(ZatcaXml.Read(xml)));

        var invoice = Build(Input(Sale(buyer: "Fatimah \"F\" <VIP>"))).Xml;
        Assert.Equal(DotNetCanonical(invoice), ZatcaXml.Canonical(ZatcaXml.Read(invoice)));
    }

    [Fact]
    public void The_first_invoice_carries_ZATCAs_fixed_previous_hash_and_each_next_one_the_hash_before_it()
    {
        var hexOfZero = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("0"))).ToLowerInvariant();
        Assert.Equal(ZatcaInvoiceBuilder.FirstPreviousHash, Convert.ToBase64String(Encoding.UTF8.GetBytes(hexOfZero)));

        var first = Build(Input(Sale(), previous: null, number: 1));
        var second = Build(Input(Sale(), previous: first.Hash, number: 2));

        Assert.Equal(ZatcaInvoiceBuilder.FirstPreviousHash, Value(Reference(first, "PIH"), Cac + "Attachment", Cbc + "EmbeddedDocumentBinaryObject"));
        Assert.Equal(first.Hash, Value(Reference(second, "PIH"), Cac + "Attachment", Cbc + "EmbeddedDocumentBinaryObject"));
        Assert.Equal(("1", "2"), (Value(Reference(first, "ICV"), Cbc + "UUID"), Value(Reference(second, "ICV"), Cbc + "UUID")));
    }

    [Fact]
    public void The_UUID_is_a_version_4_UUID_the_same_when_built_again()
    {
        var sale = Sale();
        var once = Build(Input(sale, number: 7));
        var again = Build(Input(sale, number: 7));
        var elsewhere = Build(Input(sale, number: 8));

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", once.Uuid);
        Assert.Equal((once.Uuid, once.Hash), (again.Uuid, again.Hash));
        Assert.NotEqual(once.Uuid, elsewhere.Uuid);
    }

    [Fact]
    public void A_return_is_a_simplified_credit_note_naming_the_sales_invoice_and_its_reason()
    {
        var sale = Sale(buyer: "عبدالله");
        var refund = sale with
        {
            Kind = FiscalDocumentKindContract.Return, SourceId = Guid.NewGuid(), Reference = "R-5E6F7A8B", OriginalSourceId = sale.SourceId,
            OriginalReference = "S-1A2B3C4D", Reason = "Damaged  box", Payments = [new FiscalPaymentRequest("Card", 4.33m)],
        };
        var root = Root(Build(Input(refund)));

        var type = root.Element(Cbc + "InvoiceTypeCode")!;
        Assert.Equal(("381", "0200000"), (type.Value, (string?)type.Attribute("name")));
        Assert.Equal("S-1A2B3C4D", Value(root, Cac + "BillingReference", Cac + "InvoiceDocumentReference", Cbc + "ID"));
        Assert.Equal(("48", "Damaged box"), (Value(root, Cac + "PaymentMeans", Cbc + "PaymentMeansCode"), Value(root, Cac + "PaymentMeans", Cbc + "InstructionNote")));
        Assert.Equal("R-5E6F7A8B", Value(root, Cbc + "ID"));
        Assert.Equal("4.33", Value(root, Cac + "LegalMonetaryTotal", Cbc + "PayableAmount"));   // amounts are positive
        Assert.Equal(["TaxCurrencyCode", "BillingReference", "AdditionalDocumentReference"],
            root.Elements().SkipWhile(e => e.Name.LocalName != "TaxCurrencyCode").Take(3).Select(e => e.Name.LocalName));

        // made before 11d (no reason kept): general words; without its sale's number: not built
        var noReason = Root(Build(Input(refund with { Reason = null })));
        Assert.Equal(ZatcaInvoiceBuilder.DefaultReturnReason, Value(noReason, Cac + "PaymentMeans", Cbc + "InstructionNote"));
        Assert.Equal("Fiscal.Saudi.NoOriginal", ZatcaInvoiceBuilder.Build(Input(refund with { OriginalReference = null }), Options).Error.Code);
    }

    [Theory]
    [InlineData("Cash", "10")]
    [InlineData("Card", "48")]
    [InlineData("Other", "1")]
    public void The_payment_means_is_the_largest_payments(string largest, string code)
    {
        var payments = new[] { new FiscalPaymentRequest(largest, 3m), new FiscalPaymentRequest(largest == "Cash" ? "Card" : "Cash", 1.33m) };
        Assert.Equal(code, ZatcaInvoiceBuilder.PaymentCode(payments));
        Assert.Equal("10", ZatcaInvoiceBuilder.PaymentCode([]));
        Assert.Equal(code, Value(Root(Build(Input(Sale(payments)))), Cac + "PaymentMeans", Cbc + "PaymentMeansCode"));
    }

    [Fact]
    public void Before_onboarding_the_QR_holds_the_first_five_fields()
    {
        var built = Build(Input(Sale()));

        var fields = ZatcaQr.Decode(built.Qr);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, fields.Select(f => f.Tag));
        Assert.Equal(["متجر النور & شركاه", "300000000000003", "2026-10-22T12:30:15", "4.34", "0.57"], fields.Select(f => Encoding.UTF8.GetString(f.Value)));
        Assert.Equal(Encoding.UTF8.GetByteCount("متجر النور & شركاه"), fields[0].Value.Length);   // the length is in bytes, not characters
        Assert.True(built.Qr.Length <= ZatcaQr.MaxLength);
        Assert.DoesNotContain(Root(built).Elements(Cac + "AdditionalDocumentReference"), r => (string?)r.Element(Cbc + "ID") == "QR");   // the full QR comes with the stamp
    }

    [Fact]
    public void A_QR_field_longer_than_255_bytes_is_cut_between_characters()
    {
        var name = new string('م', 200);   // 400 bytes in UTF-8
        var field = ZatcaQr.Decode(ZatcaQr.Encode([ZatcaQr.Text(1, name)])).Single();

        Assert.Equal(254, field.Value.Length);   // 127 whole letters
        Assert.Equal(new string('م', 127), Encoding.UTF8.GetString(field.Value));
    }

    [Fact]
    public void The_sellers_missing_details_are_named()
    {
        var settings = FiscalSettings.Default();
        Assert.True(settings.Update(FiscalCountry.Egypt, "123456789", "0", "4711", "متجر", new FiscalAddress("القاهرة", "مدينة نصر", "شارع", "1"), false, null, IssuedAt).IsSuccess);

        var refused = ZatcaInvoiceBuilder.Build(Input(Sale(), settings: settings), Options);   // Egypt's details: no CRN, no national address

        Assert.Equal("Fiscal.Saudi.SellerIncomplete", refused.Error.Code);
        Assert.Contains("the shop's identification (CRN)", refused.Error.Description);
        Assert.Contains("the national address", refused.Error.Description);
    }

    [Fact]
    public void The_Saudi_part_builds_waits_without_holding_late_ones_and_marks_the_receipt()
    {
        var part = new SaudiCountryPart();
        var built = part.Build(Input(Sale()));

        Assert.True(built.IsSuccess);
        Assert.Equal(FiscalCountry.SaudiArabia, part.Country);
        Assert.False(((IFiscalCountryPart)part).HoldsLateDocuments);
        Assert.Null(((IFiscalCountryPart)part).BuyerRule);
        var mark = part.Mark("AAAAAAAAxxxxxxxxxxxxxxxxxxxxBBBBBBBB", "qr");   // MISS-11d-2: the title on top, nothing under the QR
        Assert.Equal(("qr", "", "", "فاتورة ضريبية مبسطة"), (mark.QrPayload, mark.Title, mark.Caption, mark.TopLines![0]));
    }
}
