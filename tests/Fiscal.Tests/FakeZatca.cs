using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11d-3: ZATCA's onboarding API as the tests see it, behind the transport contract: it checks the OTP and the CSR (its signature, its
/// subject and its template), issues certificates for the CSR's own key signed by a stand-in CA (so invoices stamped with them verify), takes
/// compliance checks (checking each invoice's hash and signature, or refusing as scripted) and renews with a new OTP.
/// </summary>
public sealed class FakeZatca : IFiscalHttpTransport
{
    public const string Otp = "123456";
    public static readonly TimeSpan Validity = TimeSpan.FromDays(365 * 5);

    private readonly ECDsa _ca = ECDsa.Create(ECCurve.CreateFromFriendlyName("secP256k1"));
    private readonly X509Certificate2 _caCertificate;
    private readonly Dictionary<string, (string Token, string Secret)> _issued = [];
    private int _requests;

    public FakeZatca()
    {
        var request = new CertificateRequest("CN=PRZEINVOICESCA4-CA, DC=extgazt, DC=gov, DC=local", _ca, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _caCertificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    public List<FiscalHttpRequest> Requests { get; } = [];
    public bool Offline { get; set; }

    /// <summary>The compliance check that is refused (1, 2 or 3) and ZATCA's error.</summary>
    public (int Check, string Error)? RefuseCheck { get; set; }

    public List<string> CheckedInvoices { get; } = [];

    /// <summary>MISS-11d-4: the invoices reported (their numbers), the refusals and warnings to answer (by invoice number), a status for the next report.</summary>
    public List<string> Reported { get; } = [];
    public Dictionary<string, string> RefuseReport { get; } = [];
    public Dictionary<string, string> WarnReport { get; } = [];
    public int? NextReportStatus { get; set; }
    public List<CertificateRequest> Csrs { get; } = [];

    public Task<FiscalHttpResponse> SendAsync(FiscalHttpRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Offline) return Answer(0, "no connection to the internet or to the tax authority");
        if (request.Headers.GetValueOrDefault("Accept-Version") != "V2") return Answer(400, "{\"message\":\"Accept-Version missing\"}");
        var path = new Uri(request.Url).AbsolutePath;

        if (request.Method == "POST" && path.EndsWith("/compliance", StringComparison.Ordinal))
        {
            if (request.Headers.GetValueOrDefault("OTP") != Otp) return Answer(400, "{\"code\":\"Invalid-OTP\",\"message\":\"The provided OTP is invalid\"}");
            return Issue(Csr(request.Body!), "compliance");
        }

        if (request.Method == "POST" && path.EndsWith("/compliance/invoices", StringComparison.Ordinal))
        {
            if (!SignedIn(request, "compliance")) return Answer(401, "");
            using var json = JsonDocument.Parse(request.Body!);
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("invoice").GetString()!));
            CheckedInvoices.Add(xml);
            var hash = Fiscal.Saudi.ZatcaXml.Hash(Fiscal.Saudi.ZatcaXml.Read(xml));
            var errors = new List<object>();
            if (hash != json.RootElement.GetProperty("invoiceHash").GetString()) errors.Add(new { code = "invalid-invoice-hash", message = "The invoice hash API body does not match the (calculated) Hash of the XML" });
            if (!SignatureVerifies(xml, hash)) errors.Add(new { code = "invalid-signature", message = "The signature does not verify" });
            if (RefuseCheck is { } refuse && refuse.Check == CheckedInvoices.Count) errors.Add(new { code = "BR-KSA-XX", message = refuse.Error });
            return Answer(errors.Count == 0 ? 200 : 400, JsonSerializer.Serialize(new
            {
                validationResults = new { infoMessages = Array.Empty<object>(), warningMessages = Array.Empty<object>(), errorMessages = errors, status = errors.Count == 0 ? "PASS" : "ERROR" },
                reportingStatus = errors.Count == 0 ? "REPORTED" : "NOT_REPORTED",
            }));
        }

        if (request.Method == "POST" && path.EndsWith("/invoices/reporting/single", StringComparison.Ordinal))
        {
            // MISS-11d-4: reporting - signed in with the production CSID; the invoice's hash and signature checked; refusals and warnings as scripted
            if (NextReportStatus is { } status) { NextReportStatus = null; return Answer(status, status == 429 ? "{\"message\":\"Too many requests\"}" : ""); }
            if (!SignedIn(request, "production")) return Answer(401, "");
            if (request.Headers.GetValueOrDefault("Clearance-Status") != "0") return Answer(400, "{\"message\":\"Clearance-Status missing\"}");
            using var json = JsonDocument.Parse(request.Body!);
            var xml = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("invoice").GetString()!));
            var id = System.Xml.Linq.XDocument.Parse(xml).Root!.Element(Fiscal.Saudi.ZatcaXml.Cbc + "ID")!.Value;
            var uuid = System.Xml.Linq.XDocument.Parse(xml).Root!.Element(Fiscal.Saudi.ZatcaXml.Cbc + "UUID")!.Value;
            var hash = Fiscal.Saudi.ZatcaXml.Hash(Fiscal.Saudi.ZatcaXml.Read(xml));
            var errors = new List<object>();
            if (hash != json.RootElement.GetProperty("invoiceHash").GetString()) errors.Add(new { code = "invalid-invoice-hash", message = "The invoice hash API body does not match the (calculated) Hash of the XML" });
            if (uuid != json.RootElement.GetProperty("uuid").GetString()) errors.Add(new { code = "invalid-uuid", message = "The UUID does not match" });
            if (!SignatureVerifies(xml, hash)) errors.Add(new { code = "invalid-signature", message = "The signature does not verify" });
            if (RefuseReport.TryGetValue(id, out var refusal)) errors.Add(new { code = "BR-KSA-XX", message = refusal });
            if (errors.Count == 0 && !Reported.Contains(id)) Reported.Add(id);
            var warnings = WarnReport.TryGetValue(id, out var warning) ? new object[] { new { code = "BR-KSA-98", message = warning } } : [];
            return Answer(errors.Count > 0 ? 400 : warnings.Length > 0 ? 202 : 200, JsonSerializer.Serialize(new
            {
                validationResults = new { infoMessages = Array.Empty<object>(), warningMessages = warnings, errorMessages = errors, status = errors.Count > 0 ? "ERROR" : warnings.Length > 0 ? "WARNING" : "PASS" },
                reportingStatus = errors.Count > 0 ? "NOT_REPORTED" : "REPORTED",
            }));
        }

        if (request.Method == "POST" && path.EndsWith("/production/csids", StringComparison.Ordinal))
        {
            if (!SignedIn(request, "compliance")) return Answer(401, "");
            using var json = JsonDocument.Parse(request.Body!);
            var id = json.RootElement.GetProperty("compliance_request_id").GetString()!;
            if (CheckedInvoices.Count < 3) return Answer(400, "{\"message\":\"The compliance checks are not complete\"}");
            return Issue(Csrs[^1], "production", id);
        }

        if (request.Method == "PATCH" && path.EndsWith("/production/csids", StringComparison.Ordinal))
        {
            if (!SignedIn(request, "production")) return Answer(401, "");
            if (request.Headers.GetValueOrDefault("OTP") != Otp) return Answer(400, "{\"code\":\"Invalid-OTP\",\"message\":\"The provided OTP is invalid\"}");
            return Issue(Csr(request.Body!), "production");
        }

        return Answer(404, "");
    }

    /// <summary>The CSR in the body: Base64 of its PEM; its signature must verify (proof of possession).</summary>
    private CertificateRequest Csr(string body)
    {
        using var json = JsonDocument.Parse(body);
        var pem = Encoding.UTF8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("csr").GetString()!));
        var csr = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        Csrs.Add(csr);
        return csr;
    }

    private Task<FiscalHttpResponse> Issue(CertificateRequest csr, string kind, string? requestId = null)
    {
        var serial = new byte[8];
        RandomNumberGenerator.Fill(serial);
        serial[0] &= 0x7F;
        using var certificate = csr.Create(_caCertificate, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.Add(Validity), serial);
        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes(Convert.ToBase64String(certificate.RawData)));
        var secret = kind + "-secret-" + (++_requests);
        var id = requestId ?? (1000 + _requests).ToString();
        _issued[token] = (kind, secret);
        return Answer(200, JsonSerializer.Serialize(new { requestID = long.Parse(id), dispositionMessage = "ISSUED", binarySecurityToken = token, secret, errors = (object?)null }));
    }

    private bool SignedIn(FiscalHttpRequest request, string kind)
    {
        if (request.Headers.GetValueOrDefault("Authorization") is not { } auth || !auth.StartsWith("Basic ", StringComparison.Ordinal)) return false;
        var pair = Encoding.UTF8.GetString(Convert.FromBase64String(auth["Basic ".Length..])).Split(':', 2);
        return _issued.TryGetValue(pair[0], out var issued) && issued.Token == kind && issued.Secret == pair[1];
    }

    /// <summary>The invoice's signature verifies with the certificate it carries (as ZATCA checks it).</summary>
    private static bool SignatureVerifies(string xml, string hash)
    {
        var document = System.Xml.Linq.XDocument.Parse(xml);
        System.Xml.Linq.XNamespace ds = "http://www.w3.org/2000/09/xmldsig#";
        var value = document.Descendants(ds + "SignatureValue").SingleOrDefault()?.Value;
        var certificate = document.Descendants(ds + "X509Certificate").SingleOrDefault()?.Value;
        if (value is null || certificate is null) return false;
        using var cert = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(certificate));
        using var key = cert.GetECDsaPublicKey()!;
        return key.VerifyData(Convert.FromBase64String(hash), Convert.FromBase64String(value), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static Task<FiscalHttpResponse> Answer(int status, string body) => Task.FromResult(new FiscalHttpResponse(status, body));
}
