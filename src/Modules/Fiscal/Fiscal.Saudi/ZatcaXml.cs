using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Fiscal.Saudi;

/// <summary>
/// MISS-11d: the XML pieces of ZATCA's e-invoice - the UBL namespaces, writing a document as it is sent, its canonical form and its hash.
///
/// The invoice's hash (ZATCA's Security Features Implementation Standards v1.2, sections 2.3.3 and 3): the document without its
/// ext:UBLExtensions (the signature), cac:Signature and the QR's cac:AdditionalDocumentReference, in canonical XML (C14N 1.1; for these
/// documents - no xml:id or xml:base - the same as C14N 1.0), SHA-256, Base64. The next invoice carries it as its PIH. Matjar writes its
/// documents without indentation, so removing those elements leaves no stray whitespace and the hash does not depend on formatting.
/// </summary>
internal static class ZatcaXml
{
    public static readonly XNamespace Invoice = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    public static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";

    /// <summary>The document as it is sent: UTF-8, the XML declaration, no indentation, line breaks kept as they are (no CR added).</summary>
    public static string Write(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, OmitXmlDeclaration = false, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Reads a document keeping every character (whitespace included): what is hashed is what was sent.</summary>
    public static XDocument Read(string xml) => XDocument.Parse(xml, LoadOptions.PreserveWhitespace);

    /// <summary>The invoice's hash: SHA-256 of its canonical form without the signature and the QR, in Base64.</summary>
    public static string Hash(XDocument document) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(WithoutStamp(document)))));

    /// <summary>A copy without ext:UBLExtensions, cac:Signature and the QR's cac:AdditionalDocumentReference (what the stamp leaves out).</summary>
    public static XDocument WithoutStamp(XDocument document)
    {
        var copy = new XDocument(document);
        copy.Descendants(Ext + "UBLExtensions").ToList().ForEach(e => e.Remove());
        copy.Descendants(Cac + "Signature").ToList().ForEach(e => e.Remove());
        copy.Descendants(Cac + "AdditionalDocumentReference").Where(e => (string?)e.Element(Cbc + "ID") == "QR").ToList().ForEach(e => e.Remove());
        return copy;
    }

    /// <summary>
    /// Canonical XML (W3C C14N, inclusive, without comments) of a whole document: no XML declaration, namespace declarations only where
    /// they change, namespaces then attributes in canonical order, empty elements as start and end tags, characters escaped as C14N says.
    /// </summary>
    public static string Canonical(XDocument document)
    {
        var text = new StringBuilder();
        var first = true;
        foreach (var node in document.Nodes())
        {
            switch (node)
            {
                case XElement root:
                    if (!first) text.Append('\n');
                    Element(root, text, new Dictionary<string, string>(StringComparer.Ordinal));
                    first = false;
                    break;
                case XProcessingInstruction pi:
                    if (!first) text.Append('\n');
                    Instruction(pi, text);
                    first = false;
                    break;
            }
        }

        return text.ToString();
    }

    private static void Element(XElement element, StringBuilder text, Dictionary<string, string> rendered)
    {
        var name = QualifiedName(element, element.Name);
        text.Append('<').Append(name);

        // the namespace declarations that change what the parent rendered, by prefix (the default one first)
        var scope = new Dictionary<string, string>(rendered, StringComparer.Ordinal);
        foreach (var declaration in element.Attributes().Where(a => a.IsNamespaceDeclaration)
                     .Select(a => (Prefix: a.Name.Namespace == XNamespace.Xmlns ? a.Name.LocalName : string.Empty, Uri: a.Value))
                     .OrderBy(d => d.Prefix, StringComparer.Ordinal))
        {
            var before = rendered.GetValueOrDefault(declaration.Prefix, string.Empty);
            if (before == declaration.Uri) continue;
            text.Append(' ').Append(declaration.Prefix.Length == 0 ? "xmlns" : "xmlns:" + declaration.Prefix).Append("=\"").Append(EscapeAttribute(declaration.Uri)).Append('"');
            scope[declaration.Prefix] = declaration.Uri;
        }

        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration)
                     .OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal).ThenBy(a => a.Name.LocalName, StringComparer.Ordinal))
            text.Append(' ').Append(QualifiedName(element, attribute.Name, isAttribute: true)).Append("=\"").Append(EscapeAttribute(attribute.Value)).Append('"');
        text.Append('>');

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child: Element(child, text, scope); break;
                case XText part: text.Append(EscapeText(part.Value)); break;   // CDATA included (XCData is an XText)
                case XProcessingInstruction pi: Instruction(pi, text); break;
            }
        }

        text.Append("</").Append(name).Append('>');
    }

    /// <summary>
    /// MISS-11d-2: Exclusive XML canonicalization (W3C exc-c14n, without comments) of one element and what it holds: a namespace is declared
    /// where it is visibly used (by the element's or an attribute's name) and not already declared above it in the output - how ZATCA
    /// digests the stamp's signed properties.
    /// </summary>
    public static string ExclusiveCanonical(XElement apex)
    {
        var text = new StringBuilder();
        ExclusiveElement(apex, text, new Dictionary<string, string>(StringComparer.Ordinal), selfCloseEmpty: false);
        return text.ToString();
    }

    /// <summary>
    /// MISS-11d-4: the signed properties as ZATCA's reporting digests them - found on ZATCA's developer sandbox: the exclusive form, but an
    /// empty element closed in its own tag ("&lt;ds:DigestMethod .../&gt;"); the strict canonical form ("&gt;&lt;/ds:DigestMethod&gt;") is
    /// refused with "Invalid signed properties hashing" (the compliance checks do not test it).
    /// </summary>
    public static string ZatcaSignedPropertiesForm(XElement signedProperties)
    {
        var text = new StringBuilder();
        ExclusiveElement(signedProperties, text, new Dictionary<string, string>(StringComparer.Ordinal), selfCloseEmpty: true);
        return text.ToString();
    }

    private static void ExclusiveElement(XElement element, StringBuilder text, Dictionary<string, string> rendered, bool selfCloseEmpty)
    {
        var name = QualifiedName(element, element.Name);
        text.Append('<').Append(name);

        var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
        void Use(XName n, bool isAttribute)
        {
            if (n.Namespace == XNamespace.Xml) return;
            if (n.Namespace == XNamespace.None) { if (!isAttribute) used[string.Empty] = string.Empty; return; }
            var prefix = element.GetPrefixOfNamespace(n.Namespace);
            if (prefix is not null) used[prefix] = n.NamespaceName;
            else if (!isAttribute) used[string.Empty] = n.NamespaceName;
        }

        Use(element.Name, isAttribute: false);
        var attributes = element.Attributes().Where(a => !a.IsNamespaceDeclaration).ToList();
        foreach (var attribute in attributes) Use(attribute.Name, isAttribute: true);

        var scope = new Dictionary<string, string>(rendered, StringComparer.Ordinal);
        foreach (var (prefix, uri) in used)
        {
            if (rendered.GetValueOrDefault(prefix, string.Empty) == uri) continue;
            text.Append(' ').Append(prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix).Append("=\"").Append(EscapeAttribute(uri)).Append('"');
            scope[prefix] = uri;
        }

        foreach (var attribute in attributes.OrderBy(a => a.Name.NamespaceName, StringComparer.Ordinal).ThenBy(a => a.Name.LocalName, StringComparer.Ordinal))
            text.Append(' ').Append(QualifiedName(element, attribute.Name, isAttribute: true)).Append("=\"").Append(EscapeAttribute(attribute.Value)).Append('"');
        if (selfCloseEmpty && !element.Nodes().Any())
        {
            text.Append("/>");
            return;
        }

        text.Append('>');

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XElement child: ExclusiveElement(child, text, scope, selfCloseEmpty); break;
                case XText part: text.Append(EscapeText(part.Value)); break;
                case XProcessingInstruction pi: Instruction(pi, text); break;
            }
        }

        text.Append("</").Append(name).Append('>');
    }

    private static void Instruction(XProcessingInstruction pi, StringBuilder text)
        => text.Append("<?").Append(pi.Target).Append(pi.Data.Length > 0 ? " " + pi.Data : string.Empty).Append("?>");

    private static string QualifiedName(XElement element, XName name, bool isAttribute = false)
    {
        if (name.Namespace == XNamespace.None) return name.LocalName;
        if (name.Namespace == XNamespace.Xml) return "xml:" + name.LocalName;
        var prefix = element.GetPrefixOfNamespace(name.Namespace);
        if (prefix is null && !isAttribute && element.GetDefaultNamespace() == name.Namespace) return name.LocalName;
        return prefix is null ? name.LocalName : prefix + ":" + name.LocalName;
    }

    private static string EscapeText(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "&#xD;");

    private static string EscapeAttribute(string value)
        => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace("\"", "&quot;").Replace("\t", "&#x9;").Replace("\n", "&#xA;").Replace("\r", "&#xD;");
}

/// <summary>
/// MISS-11d: the QR code of ZATCA's e-invoice (Security Features Implementation Standards v1.2, section 4): fields in Tag-Length-Value
/// form - the tag in one byte, the length of the value's UTF-8 bytes in one byte, the bytes - and the whole in Base64 (at most 700
/// characters). Tags 1-5 are the seller's name, VAT number, the time, the total with VAT and the VAT total; tags 6-9 (the hash, the
/// signature, the public key and ZATCA's signature of the certificate) come with the stamp (11d-2).
/// </summary>
internal static class ZatcaQr
{
    public const int MaxLength = 700;

    /// <summary>The Base64 text of the fields in tag order (a value longer than 255 bytes is cut at a character boundary).</summary>
    public static string Encode(IEnumerable<(byte Tag, byte[] Value)> fields)
    {
        using var bytes = new MemoryStream();
        foreach (var (tag, value) in fields.OrderBy(f => f.Tag))
        {
            var length = Math.Min(value.Length, 255);
            bytes.WriteByte(tag);
            bytes.WriteByte((byte)length);
            bytes.Write(value, 0, length);
        }

        return Convert.ToBase64String(bytes.ToArray());
    }

    public static (byte Tag, byte[] Value) Text(byte tag, string value) => (tag, Utf8Within(value, 255));

    /// <summary>The fields of a QR's Base64 text (for the tests and for reading a printed receipt).</summary>
    public static IReadOnlyList<(byte Tag, byte[] Value)> Decode(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var fields = new List<(byte, byte[])>();
        for (var i = 0; i + 1 < bytes.Length;)
        {
            var (tag, length) = (bytes[i], bytes[i + 1]);
            fields.Add((tag, bytes.AsSpan(i + 2, length).ToArray()));
            i += 2 + length;
        }

        return fields;
    }

    /// <summary>The UTF-8 bytes of a text, cut so they fit in <paramref name="max"/> bytes without breaking a character.</summary>
    private static byte[] Utf8Within(string value, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= max) return bytes;
        var cut = max;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80) cut--;   // never inside a character
        return bytes[..cut];
    }
}
