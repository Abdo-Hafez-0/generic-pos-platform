using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Fiscal.Saudi;

/// <summary>MISS-11d-3: the unit (this till) as ZATCA's certificate names it - given in the onboarding window.</summary>
/// <param name="Name">The unit's name or asset number (the certificate's CN).</param>
/// <param name="Branch">The branch's name (OU).</param>
/// <param name="Industry">The industry or sector it sells in (businessCategory).</param>
/// <param name="Location">Where it stands (registeredAddress): preferably the national short address.</param>
public sealed record ZatcaUnit(string Name, string Branch, string Industry, string Location);

/// <summary>
/// MISS-11d-3: the certificate signing request (PKCS#10) for this till's key (ZATCA's Security Features Implementation Standards v1.2,
/// section 2.2.2, table 1, and the SDK's CSR template): subject C=SA, OU = the branch, O = the taxpayer's name, CN = the unit; a directory
/// name holding SN "1-Matjar|2-{version}|3-{serial}", UID = the VAT number, title = "0100" (simplified invoices only), registeredAddress and
/// businessCategory; the certificate template name of the environment. Signed with the till's key (proof of possession).
/// </summary>
internal static class ZatcaCsr
{
    public const string Functions = "0100";   // T S C Z: simplified tax invoices only (B2B is LATER-11)

    private const string TemplateOid = "1.3.6.1.4.1.311.20.2";

    public static string Create(ECDsa key, ZatcaUnit unit, string vatNumber, string taxpayerName, string serial, string version, string template)
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCountryOrRegion("SA");
        subject.AddOrganizationalUnitName(Clean(unit.Branch));
        subject.AddOrganizationName(Clean(taxpayerName));
        subject.AddCommonName(Clean(unit.Name));

        var details = new X500DistinguishedNameBuilder();
        details.Add("2.5.4.4", $"1-Matjar|2-{version}|3-{serial}", UniversalTagNumber.UTF8String);   // SN: solution, version, serial
        details.Add("0.9.2342.19200300.100.1.1", vatNumber, UniversalTagNumber.UTF8String);           // UID: the VAT number
        details.Add("2.5.4.12", Functions, UniversalTagNumber.UTF8String);                             // title: the invoice types
        details.Add("2.5.4.26", Clean(unit.Location), UniversalTagNumber.UTF8String);                  // registeredAddress
        details.Add("2.5.4.15", Clean(unit.Industry), UniversalTagNumber.UTF8String);                  // businessCategory

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509Extension(TemplateOid, PrintableString(template), critical: false));
        request.CertificateExtensions.Add(new X509Extension("2.5.29.17", DirectoryName(details.Build()), critical: false));
        return request.CreateSigningRequestPem();
    }

    /// <summary>The request as ZATCA's API takes it: the PEM text in Base64.</summary>
    public static string ForApi(string pem) => Convert.ToBase64String(Encoding.UTF8.GetBytes(pem));

    private static byte[] PrintableString(string text)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteCharacterString(UniversalTagNumber.PrintableString, text);
        return writer.Encode();
    }

    /// <summary>subjectAltName = GeneralNames { directoryName [4] Name } (the CHOICE is tagged explicitly).</summary>
    private static byte[] DirectoryName(X500DistinguishedName name)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
            writer.WriteEncodedValue(name.RawData);
        return writer.Encode();
    }

    private static string Clean(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
