using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Platform.Application.Abstractions.Security;

namespace Fiscal.Tests;

/// <summary>
/// MISS-11c-3: the ETA as the tests see it, behind the transport contract: it signs a till in (checking the form and the POS headers), takes
/// submissions (remembering each batch, accepting or refusing receipts as scripted) and answers a submission's details page by page.
/// </summary>
public sealed class FakeEta : IFiscalHttpTransport
{
    public const string Secret = "s3cret-from-portal";
    public const string Key = "preshared-key-from-portal";

    public List<FiscalHttpRequest> Requests { get; } = [];
    public bool Offline { get; set; }
    public int TokensIssued { get; private set; }
    public string? SignInError { get; set; }

    /// <summary>Answers 401 to the next submission (an expired token).</summary>
    public bool ExpireNextToken { get; set; }

    /// <summary>A status other than 202 for the next submission, with its body.</summary>
    public (int Status, string Body)? NextSubmission { get; set; }

    /// <summary>The receipts refused at submission (by UUID) and why; the rest are taken.</summary>
    public Dictionary<string, string> RefuseAtSubmission { get; } = [];

    /// <summary>The verdict of each receipt when its submission is read back (by UUID): Valid / Invalid / Cancelled; missing = still in progress.</summary>
    public Dictionary<string, string> Verdicts { get; } = [];

    public List<(string Id, IReadOnlyList<string> Uuids)> Submissions { get; } = [];

    public Task<FiscalHttpResponse> SendAsync(FiscalHttpRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (Offline) return Task.FromResult(new FiscalHttpResponse(0, "no connection to the internet or to the tax authority"));

        if (request.Url.EndsWith("/connect/token", StringComparison.Ordinal))
        {
            if (SignInError is { } error) return Answer(400, JsonSerializer.Serialize(new { error }));
            var form = request.Body!.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            if (form["client_secret"] != Secret || request.Headers["presharedkey"] != Key) return Answer(400, "{\"error\":\"invalid_client\"}");
            TokensIssued++;
            return Answer(200, JsonSerializer.Serialize(new { access_token = "token-" + TokensIssued, token_type = "Bearer", expires_in = 3600 }));
        }

        if (request.Method == "POST" && request.Url.EndsWith("/api/v1/receiptsubmissions", StringComparison.Ordinal))
        {
            if (ExpireNextToken) { ExpireNextToken = false; return Answer(401, ""); }
            if (NextSubmission is { } scripted) { NextSubmission = null; return Answer(scripted.Status, scripted.Body); }
            using var json = JsonDocument.Parse(request.Body!);
            var uuids = json.RootElement.GetProperty("receipts").EnumerateArray().Select(r => r.GetProperty("header").GetProperty("uuid").GetString()!).ToList();
            var id = "SUB" + (Submissions.Count + 1).ToString("D27");
            Submissions.Add((id, uuids));
            return Answer(202, JsonSerializer.Serialize(new
            {
                submissionUUID = id,
                acceptedDocuments = uuids.Where(u => !RefuseAtSubmission.ContainsKey(u)).Select((u, i) => new { uuid = u, longId = "L" + i, receiptNumber = "R" + i }),
                rejectedDocuments = uuids.Where(RefuseAtSubmission.ContainsKey).Select(u => new
                {
                    receiptNumber = "x", uuid = u,
                    error = new { message = "Validation error", target = u, details = new[] { new { message = RefuseAtSubmission[u], propertyPath = "receipt.itemData[0].itemCode" } } },
                }),
            }));
        }

        if (request.Method == "GET" && request.Url.Contains("/api/v1/receiptsubmissions/", StringComparison.Ordinal))
        {
            var id = request.Url.Split('/')[^2];
            var page = int.Parse(request.Url.Split("PageNo=")[1].Split('&')[0]);
            var uuids = Submissions.Single(s => s.Id == id).Uuids;
            var onPage = uuids.Skip((page - 1) * 100).Take(100).Where(Verdicts.ContainsKey).Select(u => new
            {
                uuid = u,
                status = Verdicts[u],
                errors = Verdicts[u] == "Invalid"
                    ? new[] { new { stepName = "Validate", error = new { errorCode = "4041", error = "The item code is not registered", propertyPath = "itemData[0].itemCode", innerError = Array.Empty<object>() } } }
                    : [],
            });
            return Answer(200, JsonSerializer.Serialize(new
            {
                submissionUUID = id,
                status = uuids.All(Verdicts.ContainsKey) ? "Valid" : "InProgress",
                receipts = onPage,
                metadata = new { totalPages = (uuids.Count + 99) / 100, totalCount = uuids.Count, currentPageNo = page },
            }));
        }

        return Answer(404, "");
    }

    private static Task<FiscalHttpResponse> Answer(int status, string body) => Task.FromResult(new FiscalHttpResponse(status, body));
}

/// <summary>A stand-in for Windows' DPAPI (the real one is tested on the desktop): reversible only with the same purpose.</summary>
public sealed class FakeProtector : ISecretProtector
{
    public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
        return [.. plaintext.ToArray().Select((b, i) => (byte)(b ^ key[i % key.Length]))];
    }

    public bool TryUnprotect(ReadOnlySpan<byte> protectedData, string purpose, out byte[] plaintext)
    {
        plaintext = Protect(protectedData, purpose);
        return true;
    }
}
