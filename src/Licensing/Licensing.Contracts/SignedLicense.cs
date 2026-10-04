using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Licensing.Contracts;

/// <summary>Constants describing how licenses are signed.</summary>
public static class LicenseSigning
{
    /// <summary>ECDSA over NIST P-256 with SHA-256; signature in IEEE P1363 (r||s) format. (JOSE name "ES256".)</summary>
    public const string Algorithm = "ES256";
}

/// <summary>Server-controlled commercial status carried INSIDE the signed payload.</summary>
public enum LicenseStatusClaim
{
    Active = 1,
    Suspended = 2,
    Revoked = 3
}

/// <summary>
/// Everything the client needs to make licensing decisions offline. Serialized to JSON and signed by the server.
/// Contains identifiers only (module and feature IDs as strings); it knows nothing about module implementations.
/// </summary>
/// <param name="LicenseId">Identity of the license (separate from the installation).</param>
/// <param name="CustomerId">Customer the license was issued to.</param>
/// <param name="InstallationId">The installation this license is bound to.</param>
/// <param name="ProductId">The licensed product.</param>
/// <param name="LicenseVersion">Increments every time the server re-issues the license (renewal). Prevents rollback.</param>
/// <param name="IssuedAt">When this signed document was issued.</param>
/// <param name="ValidFrom">Start of commercial validity.</param>
/// <param name="ValidUntil">End of commercial validity (hard expiry).</param>
/// <param name="LeaseValidUntil">End of the offline lease: how long the client may run without contacting the server.</param>
/// <param name="GracePeriodUntil">End of the grace period that follows the lease.</param>
/// <param name="Status">Active / Suspended / Revoked as of IssuedAt.</param>
/// <param name="Modules">Licensed module IDs (e.g. "pos").</param>
/// <param name="Features">Licensed feature IDs (e.g. "advancedreports").</param>
/// <param name="Issuer">Who issued the license.</param>
/// <param name="KeyId">Identifier of the signing key (supports key rotation). Also present on the envelope.</param>
public sealed record LicensePayload(
    Guid LicenseId,
    string CustomerId,
    Guid InstallationId,
    string ProductId,
    int LicenseVersion,
    DateTimeOffset IssuedAt,
    DateTimeOffset ValidFrom,
    DateTimeOffset ValidUntil,
    DateTimeOffset LeaseValidUntil,
    DateTimeOffset GracePeriodUntil,
    LicenseStatusClaim Status,
    IReadOnlyList<string> Modules,
    IReadOnlyList<string> Features,
    string Issuer,
    string KeyId);

/// <summary>
/// The signed license as stored on disk and sent over the wire.
/// <see cref="Payload"/> is Base64(UTF-8 JSON of <see cref="LicensePayload"/>) and is signed EXACTLY as transmitted,
/// so no canonicalization is needed: any change to the string invalidates the signature.
/// </summary>
/// <param name="Payload">Base64 of the UTF-8 JSON payload bytes (the signed bytes).</param>
/// <param name="KeyId">Which trusted key signed it.</param>
/// <param name="Algorithm">Signature algorithm (<see cref="LicenseSigning.Algorithm"/>).</param>
/// <param name="Signature">Base64 signature over the decoded payload bytes.</param>
public sealed record SignedLicense(string Payload, string KeyId, string Algorithm, string Signature);

/// <summary>JSON (de)serialization of license payloads and signed licenses.</summary>
public static class LicenseSerializer
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>Serializes the payload to the exact bytes that are signed.</summary>
    public static byte[] SerializePayloadBytes(LicensePayload payload)
        => JsonSerializer.SerializeToUtf8Bytes(payload, Options);

    /// <summary>Base64 text form of the payload bytes (the <see cref="SignedLicense.Payload"/> value).</summary>
    public static string ToPayloadText(byte[] payloadBytes) => Convert.ToBase64String(payloadBytes);

    /// <summary>Decodes and parses a payload. Returns null if the text is not a valid payload. Does NOT verify any signature.</summary>
    public static LicensePayload? TryParsePayload(string payloadText)
    {
        try
        {
            var bytes = Convert.FromBase64String(payloadText);
            return JsonSerializer.Deserialize<LicensePayload>(Encoding.UTF8.GetString(bytes), Options);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static string Serialize(SignedLicense license) => JsonSerializer.Serialize(license, Options);

    public static SignedLicense? TryDeserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SignedLicense>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
