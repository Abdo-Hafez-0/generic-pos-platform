using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Saudi;

/// <summary>MISS-11d-3: one of ZATCA's environments - its API's base address and the certificate template its CSR names.</summary>
public sealed class ZatcaHost
{
    public string Api { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;
}

/// <summary>
/// MISS-11d-3: a CSID ZATCA issued - the certificate (binarySecurityToken, Base64 of the certificate's Base64), the secret and the request id.
/// The token and the secret are the API's Basic sign-in.
/// </summary>
internal sealed record ZatcaCsid(string RequestId, string Token, string Secret)
{
    /// <summary>The certificate inside the token.</summary>
    public X509Certificate2 Certificate() => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(Encoding.ASCII.GetString(Convert.FromBase64String(Token))));

    public override string ToString() => $"CSID {RequestId}";   // the secret never reaches a log line
}

/// <summary>MISS-11d-3: ZATCA's answer to one compliance check (or report): passed (with warnings) or not, and its messages.</summary>
internal sealed record ZatcaValidation(bool Passed, string Status, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

/// <summary>
/// MISS-11d-3: ZATCA's onboarding API (the E-Invoicing developer portal's documentation), through the client's HTTP transport:
/// POST /compliance (OTP, the CSR) for the compliance CSID; POST /compliance/invoices for the compliance checks (Basic sign-in with it);
/// POST /production/csids (the compliance request id) for the production CSID; PATCH /production/csids (OTP, a new CSR) to renew.
/// Every call sends Accept-Version V2. A failure is a failed result with ZATCA's words or the reason it could not be reached.
/// </summary>
internal sealed class ZatcaApi(SaudiInvoiceOptions options, IFiscalHttpTransport? transport)
{
    public const string NoTransport = "Matjar cannot reach the internet from this installation (no connection to ZATCA).";

    public ZatcaHost Host(FiscalEnvironment environment) => options.Host(environment);

    public Task<Result<ZatcaCsid>> ComplianceCsidAsync(FiscalEnvironment environment, string otp, string csrPem, CancellationToken cancellationToken)
        => CsidAsync(new FiscalHttpRequest("POST", Url(environment, "/compliance"), Headers(("OTP", otp.Trim())),
            JsonSerializer.Serialize(new { csr = ZatcaCsr.ForApi(csrPem) })), cancellationToken);

    public Task<Result<ZatcaCsid>> ProductionCsidAsync(FiscalEnvironment environment, ZatcaCsid compliance, CancellationToken cancellationToken)
        => CsidAsync(new FiscalHttpRequest("POST", Url(environment, "/production/csids"), Headers(Basic(compliance)),
            JsonSerializer.Serialize(new { compliance_request_id = compliance.RequestId })), cancellationToken);

    public Task<Result<ZatcaCsid>> RenewAsync(FiscalEnvironment environment, ZatcaCsid production, string otp, string csrPem, CancellationToken cancellationToken)
        => CsidAsync(new FiscalHttpRequest("PATCH", Url(environment, "/production/csids"), Headers(Basic(production), ("OTP", otp.Trim())),
            JsonSerializer.Serialize(new { csr = ZatcaCsr.ForApi(csrPem) })), cancellationToken);

    /// <summary>One compliance check: the invoice (stamped with the compliance CSID) as ZATCA would receive it.</summary>
    public async Task<Result<ZatcaValidation>> CheckAsync(FiscalEnvironment environment, ZatcaCsid compliance, string hash, string uuid, string xml,
        CancellationToken cancellationToken)
    {
        var request = new FiscalHttpRequest("POST", Url(environment, "/compliance/invoices"), Headers(Basic(compliance), ("Accept-Language", "en")),
            JsonSerializer.Serialize(new { invoiceHash = hash, uuid, invoice = Convert.ToBase64String(Encoding.UTF8.GetBytes(xml)) }));
        var answer = await SendAsync(request, cancellationToken);
        if (answer.IsFailure) return Result.Failure<ZatcaValidation>(answer.Error);
        return Validation(answer.Value) is { } validation
            ? Result.Success(validation)
            : Result.Failure<ZatcaValidation>(Error.Failure("Fiscal.Zatca.Refused", Describe(answer.Value)));
    }

    /// <summary>ZATCA's validation results in an answer (200, 202 or 400 with them), or null.</summary>
    public static ZatcaValidation? Validation(FiscalHttpResponse response)
    {
        try
        {
            using var json = JsonDocument.Parse(response.Body);
            if (!json.RootElement.TryGetProperty("validationResults", out var results) || results.ValueKind != JsonValueKind.Object) return null;
            var status = results.TryGetProperty("status", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            return new ZatcaValidation(response.IsSuccess && status is "PASS" or "WARNING", status, Messages(results, "errorMessages"), Messages(results, "warningMessages"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<Result<ZatcaCsid>> CsidAsync(FiscalHttpRequest request, CancellationToken cancellationToken)
    {
        var answer = await SendAsync(request, cancellationToken);
        if (answer.IsFailure) return Result.Failure<ZatcaCsid>(answer.Error);
        try
        {
            using var json = JsonDocument.Parse(answer.Value.Body);
            var root = json.RootElement;
            var token = root.TryGetProperty("binarySecurityToken", out var t) ? t.GetString() : null;
            var secret = root.TryGetProperty("secret", out var s) ? s.GetString() : null;
            var id = root.TryGetProperty("requestID", out var r) ? (r.ValueKind == JsonValueKind.Number ? r.GetRawText() : r.GetString()) : null;
            if (!answer.Value.IsSuccess || string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(secret))
                return Result.Failure<ZatcaCsid>(Error.Failure("Fiscal.Zatca.Refused", Describe(answer.Value)));
            return Result.Success(new ZatcaCsid(id ?? string.Empty, token, secret));
        }
        catch (JsonException)
        {
            return Result.Failure<ZatcaCsid>(Error.Failure("Fiscal.Zatca.Refused", Describe(answer.Value)));
        }
    }

    private async Task<Result<FiscalHttpResponse>> SendAsync(FiscalHttpRequest request, CancellationToken cancellationToken)
    {
        if (transport is null) return Result.Failure<FiscalHttpResponse>(Error.Failure("Fiscal.Zatca.Unreachable", NoTransport));
        var response = await transport.SendAsync(request, cancellationToken);
        return response.Answered
            ? Result.Success(response)
            : Result.Failure<FiscalHttpResponse>(Error.Failure("Fiscal.Zatca.Unreachable", "ZATCA could not be reached: " + response.Body + "."));
    }

    private string Url(FiscalEnvironment environment, string path) => Host(environment).Api.TrimEnd('/') + path;

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] extra)
    {
        var headers = new Dictionary<string, string> { ["Accept-Version"] = "V2" };
        foreach (var (name, value) in extra) headers[name] = value;
        return headers;
    }

    private static (string, string) Basic(ZatcaCsid csid)
        => ("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(csid.Token + ":" + csid.Secret)));

    private static IReadOnlyList<string> Messages(JsonElement results, string name)
        => results.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(m => (m.TryGetProperty("code", out var c) ? c.GetString() + ": " : string.Empty)
                + (m.TryGetProperty("message", out var text) ? text.GetString() : m.GetRawText())).ToList()
            : [];

    /// <summary>ZATCA's words for a refusal: its error messages, its message or code, or the status.</summary>
    public static string Describe(FiscalHttpResponse response)
    {
        try
        {
            using var json = JsonDocument.Parse(response.Body);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (Validation(response) is { Errors.Count: > 0 } validation) return "ZATCA refused it: " + string.Join("; ", validation.Errors.Take(5));
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                    return "ZATCA refused it: " + string.Join("; ", errors.EnumerateArray().Take(5).Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()
                        : (e.TryGetProperty("message", out var m) ? m.GetString() : e.GetRawText())));
                foreach (var name in new[] { "message", "dispositionMessage", "code" })
                    if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                        return $"ZATCA refused it ({response.StatusCode}): {value.GetString()}";
            }
        }
        catch (JsonException)
        {
        }

        return response.StatusCode == 401
            ? "ZATCA did not accept the till's sign-in (401)."
            : $"ZATCA answered {response.StatusCode}" + (string.IsNullOrWhiteSpace(response.Body) ? "." : ": " + (response.Body.Length > 200 ? response.Body[..200] : response.Body));
    }
}
