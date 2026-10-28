using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Fiscal.Saudi;

/// <summary>
/// MISS-11d-2: what signs this till's invoices - its private key (kept in the Windows key store, never exported) and the certificate ZATCA
/// issued for it (the CSID, 11d-3). Disposed after use.
/// </summary>
public sealed class ZatcaSigner(ECDsa key, X509Certificate2 certificate) : IDisposable
{
    public ECDsa Key { get; } = key;
    public X509Certificate2 Certificate { get; } = certificate;

    public void Dispose()
    {
        Key.Dispose();
        Certificate.Dispose();
    }
}

/// <summary>
/// MISS-11d-2 (owner's decision: in the Windows key store, non-exportable, this Windows user): the till's ECDSA key on ZATCA's curve
/// (secp256k1). Implemented by Fiscal.Infrastructure with Windows CNG.
/// </summary>
public interface IZatcaKeyStore
{
    bool Exists();

    /// <summary>Makes a new key (replacing any old one) and opens it.</summary>
    ECDsa Create();

    /// <summary>The key, or null when there is none for this Windows user.</summary>
    ECDsa? Open();

    void Delete();
}

/// <summary>
/// MISS-11d-2: ZATCA's cryptographic stamp on a simplified invoice (Security Features Implementation Standards v1.2, section 2.3.3, as
/// ZATCA's SDK applies it): an enveloped XAdES signature in the invoice's UBLExtensions, the cac:Signature reference and the QR code with
/// all nine tags. The invoice's hash does not change (what the stamp adds is left out of it), so an invoice made before the till was
/// onboarded is stamped later at its place in the chain.
///
/// As ZATCA's SDK does it *(to confirm against the SDK's signed samples and its validator)*: the signature value is the ECDSA signature
/// (SHA-256, DER) of the invoice hash's 32 bytes; the invoice reference's digest is the hash itself; the signed properties' and the
/// certificate's digests are the Base64 of the SHA-256's lowercase hex; the signed properties are digested in their exclusive canonical
/// form; the QR's tag 6 and 7 carry the hash's and the signature's Base64 text, tag 8 the public key (DER) and tag 9 the certificate's
/// signature by ZATCA's CA.
/// </summary>
internal static class ZatcaStamp
{
    public static readonly XNamespace Sig = "urn:oasis:names:specification:ubl:schema:xsd:CommonSignatureComponents-2";
    public static readonly XNamespace Sac = "urn:oasis:names:specification:ubl:schema:xsd:SignatureAggregateComponents-2";
    public static readonly XNamespace Sbc = "urn:oasis:names:specification:ubl:schema:xsd:SignatureBasicComponents-2";
    public static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    public static readonly XNamespace Xades = "http://uri.etsi.org/01903/v1.3.2#";

    private const string Sha256 = "http://www.w3.org/2001/04/xmlenc#sha256";
    private const string C14n11 = "http://www.w3.org/2006/12/xml-c14n11";
    private const string XPath = "http://www.w3.org/TR/1999/REC-xpath-19991116";

    /// <summary>The stamped invoice: the XML sent, its hash (unchanged), the QR's Base64 text and the signature value.</summary>
    public sealed record Stamped(string Xml, string Hash, string Qr, string Signature);

    /// <summary>Stamps an invoice built by <see cref="ZatcaInvoiceBuilder"/> (unstamped) with the till's key and certificate at <paramref name="signedAtUtc"/>.</summary>
    public static Stamped Apply(string xml, ZatcaSigner signer, DateTime signedAtUtc, SaudiInvoiceOptions options)
        => Apply(xml, signer.Key, signer.Certificate, signedAtUtc, options);

    /// <summary>Stamps with a key and a certificate the caller keeps (onboarding's compliance checks).</summary>
    public static Stamped Apply(string xml, ECDsa key, X509Certificate2 certificate, DateTime signedAtUtc, SaudiInvoiceOptions options)
    {
        var document = ZatcaXml.Read(xml);
        var root = document.Root!;
        if (root.Element(ZatcaXml.Ext + "UBLExtensions") is not null) throw new InvalidOperationException("The invoice is already stamped.");
        XNamespace cac = ZatcaXml.Cac, cbc = ZatcaXml.Cbc;

        var hash = ZatcaXml.Hash(document);
        var signature = Convert.ToBase64String(key.SignData(Convert.FromBase64String(hash), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var signingTime = DateTime.SpecifyKind(signedAtUtc, DateTimeKind.Utc).AddHours(options.UtcOffsetHours)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        // the signed properties: when, and which certificate
        var signedProperties = new XElement(Xades + "SignedProperties", new XAttribute("Id", "xadesSignedProperties"),
            new XElement(Xades + "SignedSignatureProperties",
                new XElement(Xades + "SigningTime", signingTime),
                new XElement(Xades + "SigningCertificate", new XElement(Xades + "Cert",
                    new XElement(Xades + "CertDigest",
                        new XElement(Ds + "DigestMethod", new XAttribute("Algorithm", Sha256)),
                        new XElement(Ds + "DigestValue", HexDigest(Encoding.ASCII.GetBytes(Convert.ToBase64String(certificate.RawData))))),   // of the certificate's Base64 text (found on ZATCA's sandbox)
                    new XElement(Xades + "IssuerSerial",
                        new XElement(Ds + "X509IssuerName", certificate.IssuerName.Name),
                        new XElement(Ds + "X509SerialNumber", new BigInteger(certificate.GetSerialNumber(), isUnsigned: true).ToString(CultureInfo.InvariantCulture)))))));

        var signedInfo = new XElement(Ds + "SignedInfo",
            new XElement(Ds + "CanonicalizationMethod", new XAttribute("Algorithm", C14n11)),
            new XElement(Ds + "SignatureMethod", new XAttribute("Algorithm", "http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256")),
            new XElement(Ds + "Reference", new XAttribute("Id", "invoiceSignedData"), new XAttribute("URI", ""),
                new XElement(Ds + "Transforms",
                    new XElement(Ds + "Transform", new XAttribute("Algorithm", XPath), new XElement(Ds + "XPath", "not(//ancestor-or-self::ext:UBLExtensions)")),
                    new XElement(Ds + "Transform", new XAttribute("Algorithm", XPath), new XElement(Ds + "XPath", "not(//ancestor-or-self::cac:Signature)")),
                    new XElement(Ds + "Transform", new XAttribute("Algorithm", XPath), new XElement(Ds + "XPath", "not(//ancestor-or-self::cac:AdditionalDocumentReference[cbc:ID='QR'])")),
                    new XElement(Ds + "Transform", new XAttribute("Algorithm", C14n11))),
                new XElement(Ds + "DigestMethod", new XAttribute("Algorithm", Sha256)),
                new XElement(Ds + "DigestValue", hash)),
            new XElement(Ds + "Reference", new XAttribute("Type", "http://www.w3.org/2000/09/xmldsig#SignatureProperties"), new XAttribute("URI", "#xadesSignedProperties"),
                new XElement(Ds + "DigestMethod", new XAttribute("Algorithm", Sha256)),
                new XElement(Ds + "DigestValue", "")));   // filled once the signed properties sit in the document

        var extensions = new XElement(ZatcaXml.Ext + "UBLExtensions", new XElement(ZatcaXml.Ext + "UBLExtension",
            new XElement(ZatcaXml.Ext + "ExtensionURI", "urn:oasis:names:specification:ubl:dsig:enveloped:xades"),
            new XElement(ZatcaXml.Ext + "ExtensionContent",
                new XElement(Sig + "UBLDocumentSignatures",
                    new XAttribute(XNamespace.Xmlns + "sig", Sig.NamespaceName), new XAttribute(XNamespace.Xmlns + "sac", Sac.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "sbc", Sbc.NamespaceName),
                    new XElement(Sac + "SignatureInformation",
                        new XElement(cbc + "ID", "urn:oasis:names:specification:ubl:signature:1"),
                        new XElement(Sbc + "ReferencedSignatureID", "urn:oasis:names:specification:ubl:signature:Invoice"),
                        new XElement(Ds + "Signature", new XAttribute(XNamespace.Xmlns + "ds", Ds.NamespaceName), new XAttribute("Id", "signature"),
                            signedInfo,
                            new XElement(Ds + "SignatureValue", signature),
                            new XElement(Ds + "KeyInfo", new XElement(Ds + "X509Data", new XElement(Ds + "X509Certificate", Convert.ToBase64String(certificate.RawData)))),
                            new XElement(Ds + "Object",
                                new XElement(Xades + "QualifyingProperties", new XAttribute(XNamespace.Xmlns + "xades", Xades.NamespaceName), new XAttribute("Target", "signature"),
                                    signedProperties))))))));
        root.AddFirst(extensions);

        // the signed properties laid out as ZATCA's SDK lays them out (its reporting checks their digest in that form; outside the invoice hash)
        Indent(signedProperties, 32);

        // the signed properties' digest, in the form ZATCA's reporting checks (exclusive, empty elements self-closed - found on its sandbox)
        signedInfo.Elements(Ds + "Reference").Last().Element(Ds + "DigestValue")!.Value = HexDigest(Encoding.UTF8.GetBytes(ZatcaXml.ZatcaSignedPropertiesForm(signedProperties)));

        // the QR with all nine tags, then the reference to the signature (UBL order: after the document references)
        var qr = Qr(root, hash, signature, certificate);
        var references = root.Elements(cac + "AdditionalDocumentReference").ToList();
        references[^1].AddAfterSelf(
            new XElement(cac + "AdditionalDocumentReference", new XElement(cbc + "ID", "QR"),
                new XElement(cac + "Attachment", new XElement(cbc + "EmbeddedDocumentBinaryObject", new XAttribute("mimeCode", "text/plain"), qr))),
            new XElement(cac + "Signature",
                new XElement(cbc + "ID", "urn:oasis:names:specification:ubl:signature:Invoice"),
                new XElement(cbc + "SignatureMethod", "urn:oasis:names:specification:ubl:dsig:enveloped:xades")));

        var stamped = ZatcaXml.Write(document);
        if (ZatcaXml.Hash(ZatcaXml.Read(stamped)) != hash) throw new InvalidOperationException("Stamping changed the invoice's hash.");
        return new Stamped(stamped, hash, qr, signature);
    }

    /// <summary>The QR's nine tags: tags 1-5 from the invoice itself, then the hash, the signature, the public key and the CA's signature.</summary>
    public static string Qr(XElement invoice, string hash, string signature, X509Certificate2 certificate)
    {
        XNamespace cac = ZatcaXml.Cac, cbc = ZatcaXml.Cbc;
        var party = invoice.Element(cac + "AccountingSupplierParty")!.Element(cac + "Party")!;
        return ZatcaQr.Encode([
            ZatcaQr.Text(1, party.Element(cac + "PartyLegalEntity")!.Element(cbc + "RegistrationName")!.Value),
            ZatcaQr.Text(2, party.Element(cac + "PartyTaxScheme")!.Element(cbc + "CompanyID")!.Value),
            ZatcaQr.Text(3, invoice.Element(cbc + "IssueDate")!.Value + "T" + invoice.Element(cbc + "IssueTime")!.Value),
            ZatcaQr.Text(4, invoice.Element(cac + "LegalMonetaryTotal")!.Element(cbc + "PayableAmount")!.Value),   // the amount due (BT-115)
            ZatcaQr.Text(5, invoice.Elements(cac + "TaxTotal").First().Element(cbc + "TaxAmount")!.Value),
            ZatcaQr.Text(6, hash),
            ZatcaQr.Text(7, signature),
            (8, certificate.PublicKey.ExportSubjectPublicKeyInfo()),
            (9, CertificateSignature(certificate)),
        ]);
    }

    /// <summary>The signature value of a certificate - the CA's signature (its last BIT STRING).</summary>
    public static byte[] CertificateSignature(X509Certificate2 certificate)
    {
        var certificateSequence = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
        certificateSequence.ReadEncodedValue();   // tbsCertificate
        certificateSequence.ReadEncodedValue();   // signatureAlgorithm
        return certificateSequence.ReadBitString(out _);
    }

    /// <summary>
    /// MISS-11d-4: indents an element's children as ZATCA's SDK template does (a new line and four more spaces per level, the closing tag
    /// back at <paramref name="spaces"/>); elements holding text only are left as they are.
    /// </summary>
    private static void Indent(XElement element, int spaces)
    {
        var children = element.Elements().ToList();
        if (children.Count == 0) return;
        foreach (var child in children)
        {
            child.AddBeforeSelf(new XText("\n" + new string(' ', spaces + 4)));
            Indent(child, spaces + 4);
        }

        children[^1].AddAfterSelf(new XText("\n" + new string(' ', spaces)));
    }

    /// <summary>ZATCA's digest form for the certificate and the signed properties: Base64 of the SHA-256's lowercase hex text.</summary>
    public static string HexDigest(byte[] data)
        => Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()));
}
