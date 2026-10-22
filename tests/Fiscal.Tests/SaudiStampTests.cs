using System.Formats.Asn1;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.Services;
using Fiscal.Saudi;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11d-2: ZATCA's cryptographic stamp - the XAdES signature in the invoice's UBLExtensions, the QR with all nine tags, the invoice's
/// hash unchanged; the till's key in the Windows key store (secp256k1, not exportable); the Saudi receipt's mark (owner's choices: the
/// title and VAT number on top, labels in Arabic and English, nothing under the QR). The signature is checked with .NET's own
/// cryptography and XML canonicalization, independently of Matjar's code.
/// </summary>
public sealed class SaudiStampTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    private static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    private static readonly XNamespace Xades = "http://uri.etsi.org/01903/v1.3.2#";
    private static readonly Guid Water = Guid.NewGuid();
    private static readonly DateTime IssuedAt = new(2026, 10, 22, 9, 30, 15, DateTimeKind.Utc);

    /// <summary>A key on ZATCA's curve and a certificate for it signed by a stand-in CA (ZATCA's certificate comes with onboarding).</summary>
    internal static ZatcaSigner TestSigner()
    {
        var ca = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
        var caRequest = new CertificateRequest("CN=TSZEINVOICE-SubCA-1, DC=extgazt, DC=gov, DC=local", ca, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        var caCertificate = caRequest.CreateSelfSigned(IssuedAt.AddDays(-1), IssuedAt.AddYears(1));
        var key = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
        var request = new CertificateRequest("CN=Till 1, OU=Olaya branch, O=Matjar Demo, C=SA", key, HashAlgorithmName.SHA256);
        using var issued = request.Create(caCertificate, IssuedAt.AddDays(-1), IssuedAt.AddYears(1), [0x01, 0x5A, 0x7C, 0x3E, 0x11]);
        return new ZatcaSigner(key, X509CertificateLoader.LoadCertificate(issued.RawData));
    }

    private static FiscalBuildInput Input(FiscalDocumentRequest? request = null)
        => new(request ?? Sale(), SaudiInvoiceTests.Settings(), Guid.Parse("8d1f3a52-0000-4000-8000-000000000001"), "SHOP-PC", 3, "cHJldmlvdXM=",
            new Dictionary<Guid, FiscalLineCodes> { [Water] = new(null, null, "PCE", null, null, "S", null, null) });

    private static FiscalDocumentRequest Sale()
        => new(FiscalDocumentKindContract.Sale, Guid.NewGuid(), "S-1A2B3C4D", IssuedAt,
            [new FiscalLineRequest(Water, "WATER", "مياه 500 مل", 2m, 1.15m, 0m, 2.30m, 0.15m, 0.30m)], 2.30m, 0.30m, [new FiscalPaymentRequest("Cash", 2.30m)]);

    /// <summary>A key store holding a test key (or none): each Open gives a fresh copy, as the Windows key store does.</summary>
    internal sealed class TestKeyStore(ECDsa? key) : IZatcaKeyStore
    {
        private ECParameters? _key = key?.ExportParameters(true);

        public bool Exists() => _key is not null;

        public ECDsa Create()
        {
            using var made = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
            _key = made.ExportParameters(true);
            return ECDsa.Create(_key.Value);
        }

        public ECDsa? Open() => _key is { } k ? ECDsa.Create(k) : null;

        public void Delete() => _key = null;
    }

    private static (ZatcaInvoiceBuilder.Built Unstamped, ZatcaStamp.Stamped Stamped, ZatcaSigner Signer) Stamp()
    {
        var built = ZatcaInvoiceBuilder.Build(Input(), new SaudiInvoiceOptions()).Value;
        var signer = TestSigner();
        return (built, ZatcaStamp.Apply(built.Xml, signer, IssuedAt.AddMinutes(1), new SaudiInvoiceOptions()), signer);
    }

    [Fact]
    public void The_stamp_sits_in_the_UBL_extensions_with_the_QR_and_the_signature_reference_and_leaves_the_hash_unchanged()
    {
        var (unstamped, stamped, _) = Stamp();
        var root = XDocument.Parse(stamped.Xml).Root!;

        Assert.Equal(unstamped.Hash, stamped.Hash);
        Assert.Equal(stamped.Hash, ZatcaXml.Hash(ZatcaXml.Read(stamped.Xml)));
        Assert.Equal(
            ["UBLExtensions", "ProfileID", "ID", "UUID", "IssueDate", "IssueTime", "InvoiceTypeCode", "DocumentCurrencyCode", "TaxCurrencyCode",
             "AdditionalDocumentReference", "AdditionalDocumentReference", "AdditionalDocumentReference", "Signature", "AccountingSupplierParty"],
            root.Elements().Take(14).Select(e => e.Name.LocalName));
        Assert.Equal(["ICV", "PIH", "QR"], root.Elements(Cac + "AdditionalDocumentReference").Select(r => r.Element(Cbc + "ID")!.Value));
        Assert.Equal(("urn:oasis:names:specification:ubl:signature:Invoice", "urn:oasis:names:specification:ubl:dsig:enveloped:xades"),
            (root.Element(Cac + "Signature")!.Element(Cbc + "ID")!.Value, root.Element(Cac + "Signature")!.Element(Cbc + "SignatureMethod")!.Value));
        Assert.Equal("urn:oasis:names:specification:ubl:dsig:enveloped:xades", root.Element(Ext + "UBLExtensions")!.Descendants(Ext + "ExtensionURI").Single().Value);
        Assert.DoesNotContain("\n", stamped.Xml);
        Assert.Throws<InvalidOperationException>(() => ZatcaStamp.Apply(stamped.Xml, TestSigner(), IssuedAt, new SaudiInvoiceOptions()));   // once only
    }

    [Fact]
    public void The_signature_is_the_ECDSA_signature_of_the_invoice_hash_by_the_tills_key()
    {
        var (_, stamped, signer) = Stamp();
        var signature = XDocument.Parse(stamped.Xml).Descendants(Ds + "Signature").Single();

        var value = Convert.FromBase64String(signature.Element(Ds + "SignatureValue")!.Value);
        using var publicKey = signer.Certificate.GetECDsaPublicKey()!;
        Assert.True(publicKey.VerifyData(Convert.FromBase64String(stamped.Hash), value, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Equal(stamped.Signature, signature.Element(Ds + "SignatureValue")!.Value);

        var info = signature.Element(Ds + "SignedInfo")!;
        Assert.Equal(("http://www.w3.org/2006/12/xml-c14n11", "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256"),
            ((string?)info.Element(Ds + "CanonicalizationMethod")!.Attribute("Algorithm"), (string?)info.Element(Ds + "SignatureMethod")!.Attribute("Algorithm")));
        var invoiceReference = info.Elements(Ds + "Reference").First();
        Assert.Equal(("invoiceSignedData", "", stamped.Hash), ((string?)invoiceReference.Attribute("Id"), (string?)invoiceReference.Attribute("URI"), invoiceReference.Element(Ds + "DigestValue")!.Value));
        Assert.Equal(
            ["not(//ancestor-or-self::ext:UBLExtensions)", "not(//ancestor-or-self::cac:Signature)", "not(//ancestor-or-self::cac:AdditionalDocumentReference[cbc:ID='QR'])"],
            invoiceReference.Descendants(Ds + "XPath").Select(x => x.Value));
        Assert.Equal(Convert.ToBase64String(signer.Certificate.RawData), signature.Descendants(Ds + "X509Certificate").Single().Value);
    }

    [Fact]
    public void The_signed_properties_name_the_certificate_and_are_digested_in_their_exclusive_canonical_form()
    {
        var (_, stamped, signer) = Stamp();
        var document = XDocument.Parse(stamped.Xml);
        var properties = document.Descendants(Xades + "SignedProperties").Single();

        Assert.Equal("xadesSignedProperties", (string?)properties.Attribute("Id"));
        Assert.Equal("2026-10-22T12:31:15", properties.Descendants(Xades + "SigningTime").Single().Value);   // Riyadh time
        var certHex = Convert.ToHexString(SHA256.HashData(signer.Certificate.RawData)).ToLowerInvariant();
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes(certHex)), properties.Descendants(Xades + "CertDigest").Single().Element(Ds + "DigestValue")!.Value);
        Assert.Equal("CN=TSZEINVOICE-SubCA-1, DC=extgazt, DC=gov, DC=local", properties.Descendants(Ds + "X509IssuerName").Single().Value);
        Assert.Equal("5813059089", properties.Descendants(Ds + "X509SerialNumber").Single().Value);   // 0x015A7C3E11

        // independently: .NET's exclusive C14N of the signed properties in the stamped document
        var xml = new XmlDocument { PreserveWhitespace = true };
        xml.LoadXml(stamped.Xml);
        var names = new XmlNamespaceManager(xml.NameTable);
        names.AddNamespace("xades", Xades.NamespaceName);
        var subset = xml.SelectNodes("(//xades:SignedProperties/descendant-or-self::node() | //xades:SignedProperties/descendant-or-self::*/@* | //xades:SignedProperties/descendant-or-self::*/namespace::*)", names)!;
        var transform = new XmlDsigExcC14NTransform();
        transform.LoadInput(subset);
        var canonical = new StreamReader((Stream)transform.GetOutput(typeof(Stream)), Encoding.UTF8).ReadToEnd();
        Assert.Equal(canonical, ZatcaXml.ExclusiveCanonical(properties));
        Assert.StartsWith("<xades:SignedProperties xmlns:xades=\"http://uri.etsi.org/01903/v1.3.2#\" Id=\"xadesSignedProperties\"><xades:SignedSignatureProperties>", canonical);
        Assert.Contains("<ds:DigestMethod xmlns:ds=\"http://www.w3.org/2000/09/xmldsig#\" Algorithm=\"http://www.w3.org/2001/04/xmlenc#sha256\"></ds:DigestMethod>", canonical);

        var digest = Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()));
        var reference = document.Descendants(Ds + "Reference").Single(r => (string?)r.Attribute("URI") == "#xadesSignedProperties");
        Assert.Equal(("http://www.w3.org/2000/09/xmldsig#SignatureProperties", digest), ((string?)reference.Attribute("Type"), reference.Element(Ds + "DigestValue")!.Value));
    }

    [Fact]
    public void The_QR_holds_all_nine_tags()
    {
        var (_, stamped, signer) = Stamp();
        var root = XDocument.Parse(stamped.Xml).Root!;
        var inDocument = root.Elements(Cac + "AdditionalDocumentReference").Single(r => r.Element(Cbc + "ID")!.Value == "QR").Descendants(Cbc + "EmbeddedDocumentBinaryObject").Single();
        Assert.Equal((stamped.Qr, "text/plain"), (inDocument.Value, (string?)inDocument.Attribute("mimeCode")));
        Assert.True(stamped.Qr.Length <= ZatcaQr.MaxLength, $"{stamped.Qr.Length} characters");

        var tags = ZatcaQr.Decode(stamped.Qr);
        Assert.Equal(Enumerable.Range(1, 9).Select(t => (byte)t), tags.Select(t => t.Tag));
        string Text(int tag) => Encoding.UTF8.GetString(tags[tag - 1].Value);
        Assert.Equal(["متجر النور & شركاه", "300000000000003", "2026-10-22T12:30:15", "2.30", "0.30", stamped.Hash, stamped.Signature], Enumerable.Range(1, 7).Select(Text));

        // tag 8: the public key (DER) - it verifies the signature; tag 9: the CA's signature of the certificate (its last BIT STRING)
        using var fromQr = ECDsa.Create();
        fromQr.ImportSubjectPublicKeyInfo(tags[7].Value, out _);
        Assert.True(fromQr.VerifyData(Convert.FromBase64String(stamped.Hash), Convert.FromBase64String(stamped.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var certificate = new AsnReader(signer.Certificate.RawData, AsnEncodingRules.DER).ReadSequence();
        certificate.ReadEncodedValue();
        certificate.ReadEncodedValue();
        Assert.Equal(certificate.ReadBitString(out _), tags[8].Value);
        Assert.Equal(tags[8].Value, ZatcaStamp.CertificateSignature(signer.Certificate));
    }

    [Fact]
    public void The_Saudi_part_stamps_at_the_sale_when_onboarded_and_keeps_the_short_QR_before()
    {
        using var signer = TestSigner();
        var part = new SaudiCountryPart(keys: new TestKeyStore(signer.Key));
        var sale = Sale();
        var stamped = part.Build(Input(sale) with { Certificate = Convert.ToBase64String(signer.Certificate.RawData) }).Value;
        var waiting = part.Build(Input(sale)).Value;                                                        // no certificate yet
        var noKey = new SaudiCountryPart(keys: new TestKeyStore(null)).Build(Input(sale) with { Certificate = Convert.ToBase64String(signer.Certificate.RawData) }).Value;

        Assert.Equal(9, ZatcaQr.Decode(stamped.QrPayload!).Count);
        Assert.Contains("<ext:UBLExtensions>", stamped.Payload);
        Assert.Equal(5, ZatcaQr.Decode(waiting.QrPayload!).Count);
        Assert.DoesNotContain("UBLExtensions", waiting.Payload);
        Assert.Equal(waiting.Hash, stamped.Hash);   // the chain does not depend on when it is stamped
        Assert.Equal(5, ZatcaQr.Decode(noKey.QrPayload!).Count);   // the key is not on this computer: it waits
    }

    [Fact]
    public void The_receipt_mark_has_the_title_and_VAT_number_on_top_the_invoices_totals_and_nothing_under_the_QR()
    {
        var part = new SaudiCountryPart();
        var built = part.Build(Input()).Value;

        var mark = part.Mark(built.Hash, built.QrPayload!, FiscalDocumentKind.Sale);
        Assert.Equal((built.QrPayload, "", ""), (mark.QrPayload, mark.Title, mark.Caption));
        Assert.Equal(["فاتورة ضريبية مبسطة", "Simplified Tax Invoice", "الرقم الضريبي VAT No. 300000000000003"], mark.TopLines);
        Assert.Equal((2.00m, 0.30m, 2.30m, true), (mark.TotalExcludingTax, mark.TaxTotal, mark.TotalIncludingTax, mark.BilingualLabels));

        var note = part.Mark(built.Hash, built.QrPayload!, FiscalDocumentKind.Return);
        Assert.Equal(["إشعار دائن مبسط", "Simplified Credit Note"], note.TopLines!.Take(2));
        Assert.Equal(mark with { TopLines = null }, part.Mark(built.Hash, built.QrPayload!) with { TopLines = null });   // a sale by default

        var unreadable = part.Mark(built.Hash, "not a QR");
        Assert.Equal((null, (decimal?)null), (unreadable.TotalIncludingTax, unreadable.TaxTotal));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void The_tills_key_is_made_in_the_Windows_key_store_on_ZATCAs_curve_and_cannot_be_exported()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new CngZatcaKeyStore("Matjar-ZATCA-Test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(store.Exists());
            Assert.Null(store.Open());

            byte[] signature;
            using (var key = store.Create())
            {
                Assert.True(store.Exists());
                Assert.Equal("1.3.132.0.10", key.ExportParameters(false).Curve.Oid.Value);   // secp256k1
                Assert.ThrowsAny<CryptographicException>(() => key.ExportParameters(true));   // the private key never leaves the store
                Assert.ThrowsAny<CryptographicException>(() => key.ExportPkcs8PrivateKey());
                signature = key.SignData(new byte[] { 1, 2, 3 }, HashAlgorithmName.SHA256);
            }

            using (var again = store.Open()!)
                Assert.True(again.VerifyData(new byte[] { 1, 2, 3 }, signature, HashAlgorithmName.SHA256));   // the same key, reopened

            Assert.StartsWith("Matjar-ZATCA-", CngZatcaKeyStore.NameFor(@"C:\shop\genericpos.db"));
            Assert.Equal(CngZatcaKeyStore.NameFor(@"C:\shop\genericpos.db"), CngZatcaKeyStore.NameFor(@"c:\SHOP\genericpos.db"));
            Assert.NotEqual(CngZatcaKeyStore.NameFor(@"C:\shop\genericpos.db"), CngZatcaKeyStore.NameFor(@"C:\other\genericpos.db"));
        }
        finally
        {
            store.Delete();
            Assert.False(store.Exists());
        }
    }
}
