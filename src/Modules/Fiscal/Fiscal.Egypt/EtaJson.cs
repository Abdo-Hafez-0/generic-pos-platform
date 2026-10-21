using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fiscal.Egypt;

/// <summary>
/// MISS-11c: a receipt as an ordered tree, written two ways from the same nodes - the JSON sent to the ETA, and the ETA's serialized form that
/// is hashed into the receipt's UUID. One source for both means the hash always matches the text that is sent.
/// </summary>
internal abstract record EtaNode;

internal sealed record EtaText(string Value) : EtaNode;

/// <summary>A number, always written in one form (at least 2 decimals, at most 5: "114.00", "8.77193") - the ETA hashes it as written.</summary>
internal sealed record EtaNumber(decimal Value) : EtaNode
{
    public string Text => Format(Value);

    public static string Format(decimal value) => decimal.Round(value, 5, MidpointRounding.AwayFromZero).ToString("0.00###", CultureInfo.InvariantCulture);
}

internal sealed record EtaArray(IReadOnlyList<EtaNode> Items) : EtaNode;

internal sealed record EtaObject(IReadOnlyList<KeyValuePair<string, EtaNode>> Properties) : EtaNode
{
    /// <summary>An object from (name, value) pairs in order; a null value leaves the property out.</summary>
    public static EtaObject Of(params (string Name, EtaNode? Value)[] properties)
        => new(properties.Where(p => p.Value is not null).Select(p => new KeyValuePair<string, EtaNode>(p.Name, p.Value!)).ToList());

    /// <summary>The same object with one property's value replaced (found by its path of names).</summary>
    public EtaObject With(string[] path, EtaNode value)
        => new(Properties.Select(p => p.Key != path[0] ? p
            : new KeyValuePair<string, EtaNode>(p.Key, path.Length == 1 ? value : ((EtaObject)p.Value).With(path[1..], value))).ToList());
}

internal static class EtaJson
{
    /// <summary>The JSON text (compact, UTF-8 text kept as it is - Arabic is not escaped).</summary>
    public static string Write(EtaNode node)
    {
        var sb = new StringBuilder();
        Write(node, sb);
        return sb.ToString();
    }

    private static void Write(EtaNode node, StringBuilder sb)
    {
        switch (node)
        {
            case EtaText text:
                sb.Append('"').Append(Escape(text.Value)).Append('"');
                break;
            case EtaNumber number:
                sb.Append(number.Text);
                break;
            case EtaArray array:
                sb.Append('[');
                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(array.Items[i], sb);
                }

                sb.Append(']');
                break;
            case EtaObject obj:
                sb.Append('{');
                for (var i = 0; i < obj.Properties.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"').Append(obj.Properties[i].Key).Append("\":");
                    Write(obj.Properties[i].Value, sb);
                }

                sb.Append('}');
                break;
        }
    }

    /// <summary>
    /// The ETA's serialized form of a document (the SDK's "document serialization approach"): each property's name in invariant upper case
    /// within quotes, then its value - a simple value within quotes exactly as written in the JSON, an object by its properties in order, an
    /// array by its elements each preceded by the array's name again.
    /// </summary>
    public static string Serialize(EtaObject document)
    {
        var sb = new StringBuilder();
        SerializeProperties(document, sb);
        return sb.ToString();
    }

    private static void SerializeProperties(EtaObject obj, StringBuilder sb)
    {
        foreach (var (key, value) in obj.Properties)
        {
            var name = "\"" + key.ToUpperInvariant() + "\"";
            sb.Append(name);
            if (value is EtaArray array)
                foreach (var item in array.Items)
                {
                    sb.Append(name);
                    SerializeValue(item, sb);
                }
            else
                SerializeValue(value, sb);
        }
    }

    private static void SerializeValue(EtaNode node, StringBuilder sb)
    {
        switch (node)
        {
            case EtaText text: sb.Append('"').Append(Escape(text.Value)).Append('"'); break;
            case EtaNumber number: sb.Append('"').Append(number.Text).Append('"'); break;
            case EtaObject obj: SerializeProperties(obj, sb); break;
            case EtaArray array:
                foreach (var item in array.Items) SerializeValue(item, sb);
                break;
        }
    }

    /// <summary>The receipt's UUID: SHA-256 of the serialized form, 64 lower-case hexadecimal characters.</summary>
    public static string Hash(EtaObject document) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(document))));

    /// <summary>
    /// Text as the receipt carries it: control characters dropped, the double quote and the backslash replaced (so the JSON never needs an
    /// escape and the hashed form equals the sent one), trimmed and cut to the authority's length.
    /// </summary>
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsControl(c)) sb.Append(' ');
            else if (c == '"') sb.Append('”');
            else if (c == '\\') sb.Append('/');
            else sb.Append(c);
        }

        var text = sb.ToString().Trim();
        return text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
    }

    // Clean() leaves nothing to escape; this keeps the JSON valid should a raw value ever reach it.
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
