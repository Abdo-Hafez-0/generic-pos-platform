using System.Text;
using System.Text.Json;
using Fiscal.Application.Abstractions;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Enums;
using Platform.Core.Results;

namespace Fiscal.Egypt;

/// <summary>
/// MISS-11c-3: talks to the ETA's eReceipt API through the client's transport (the module itself never reaches the network): the POS's
/// sign-in for a token (kept in memory only, until shortly before it expires), the batch submission and reading a submission back.
/// Owner's decisions: no issuer signature for now *(to confirm on pre-production)*; batches of up to 500 receipts (and 1.5 MB).
/// Every problem is an outcome with a reason in plain words - never an exception.
/// </summary>
internal sealed class EtaService(EgyptReceiptOptions options, IFiscalHttpTransport? transport, TimeProvider? timeProvider = null)
{
    public const int MaxReceiptsPerSubmission = 500;

    /// <summary>The ETA takes at most 1.5 MB per submission; Matjar keeps a margin.</summary>
    public const int MaxSubmissionBytes = 1_400_000;

    public const string NoTransport = "This copy of Matjar has no connection to the tax authority set up.";

    private readonly Dictionary<(FiscalEnvironment, string), (string Token, DateTimeOffset Until)> _tokens = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private DateTimeOffset Now => (timeProvider ?? TimeProvider.System).GetUtcNow();

    // ---- the sign-in

    /// <summary>A token for the till (from memory while it is valid; <paramref name="fresh"/> asks again). Null with the reason when refused.</summary>
    public async Task<(string? Token, string? Problem)> TokenAsync(FiscalTillConnection connection, bool fresh, CancellationToken cancellationToken)
    {
        if (transport is null) return (null, NoTransport);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var key = (connection.Environment, connection.ClientId);
            if (!fresh && _tokens.TryGetValue(key, out var kept) && kept.Until > Now) return (kept.Token, null);
            _tokens.Remove(key);

            var form = $"grant_type=client_credentials&client_id={Uri.EscapeDataString(connection.ClientId)}&client_secret={Uri.EscapeDataString(connection.ClientSecret)}";
            var headers = new Dictionary<string, string>
            {
                ["posserial"] = connection.PosSerial,
                ["pososversion"] = connection.PosOsVersion,
                ["posmodelframework"] = connection.PosModelFramework,
                ["presharedkey"] = connection.PresharedKey,
            };
            var answer = await transport.SendAsync(new FiscalHttpRequest("POST", Url(options.Hosts(connection.Environment).Identity, "/connect/token"), headers, form,
                "application/x-www-form-urlencoded"), cancellationToken);
            if (!answer.Answered) return (null, Unreachable(answer));
            if (!answer.IsSuccess) return (null, SignInRefused(answer));

            using var json = Parse(answer.Body);
            if (json is null || Text(json.RootElement, "access_token") is not { Length: > 0 } token)
                return (null, "The tax authority's sign-in answer had no token.");
            var seconds = Number(json.RootElement, "expires_in") ?? 3600;
            _tokens[key] = (token, Now.AddSeconds(Math.Max(60, seconds - 120)));   // asked again two minutes before it expires
            return (token, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> TestSignInAsync(FiscalTillConnection connection, CancellationToken cancellationToken)
    {
        var (token, problem) = await TokenAsync(connection, fresh: true, cancellationToken);
        return token is not null ? Result.Success() : Result.Failure(Error.Conflict("Fiscal.Egypt.SignInRefused", problem!));
    }

    /// <summary>The ETA's sign-in errors (Authenticate POS) in the shop's words.</summary>
    private static string SignInRefused(FiscalHttpResponse answer)
    {
        using var json = Parse(answer.Body);
        var error = json is null ? null : Text(json.RootElement, "error");
        var why = error switch
        {
            "invalid_client" or "unauthorized_client" or "invalid_clientsecret" => "the client ID or the client secret is not right",
            "invalid_posserial" => "the POS serial is not the one registered for this client ID",
            "invalid_pososversion" => "the POS operating system version is not the one registered",
            "invalid_posmodelframework" => "the POS model framework is not the one registered",
            "invalid_presharedkey" => "the pre-shared key is not right",
            null => $"the answer was {answer.StatusCode}",
            _ => error,
        };
        return $"The tax authority refused the sign-in: {why}.";
    }

    // ---- the submission

    public async Task<IReadOnlyList<FiscalSendOutcome>> SubmitAsync(FiscalSendInput input, CancellationToken cancellationToken)
    {
        if (input.Connection is not { } connection)
            return All(input, FiscalSendResult.NotSent, EgyptCountryPart.NotRegistered);
        if (transport is null) return All(input, FiscalSendResult.NotSent, NoTransport);

        var outcomes = new List<FiscalSendOutcome>();
        foreach (var batch in Batches(input.Documents))
        {
            outcomes.AddRange(await SubmitBatchAsync(connection, batch, cancellationToken));
            if (outcomes.Count > 0 && outcomes[^1].Result == FiscalSendResult.NotSent && outcomes.All(o => o.Result == FiscalSendResult.NotSent))
            {
                // nothing got through (offline, refused sign-in): the rest waits for the next round
                var reason = outcomes[^1].Reason;
                outcomes.AddRange(input.Documents.Skip(outcomes.Count).Select(d => new FiscalSendOutcome(d.DocumentId, FiscalSendResult.NotSent, Reason: reason)));
                break;
            }
        }

        return outcomes;
    }

    /// <summary>The documents in the chain's order, in batches of at most 500 receipts and the size limit.</summary>
    internal static IEnumerable<IReadOnlyList<FiscalOutgoingDocument>> Batches(IReadOnlyList<FiscalOutgoingDocument> documents)
    {
        var batch = new List<FiscalOutgoingDocument>();
        var bytes = 20;
        foreach (var document in documents)
        {
            var size = Encoding.UTF8.GetByteCount(document.Payload) + 1;
            if (batch.Count > 0 && (batch.Count == MaxReceiptsPerSubmission || bytes + size > MaxSubmissionBytes))
            {
                yield return batch;
                (batch, bytes) = ([], 20);
            }

            batch.Add(document);
            bytes += size;
        }

        if (batch.Count > 0) yield return batch;
    }

    private async Task<IReadOnlyList<FiscalSendOutcome>> SubmitBatchAsync(FiscalTillConnection connection, IReadOnlyList<FiscalOutgoingDocument> batch,
        CancellationToken cancellationToken)
    {
        // owner's decision 2026-10-21: no issuer signature yet (the ETA does not validate it; to confirm on pre-production)
        var body = "{\"receipts\":[" + string.Join(",", batch.Select(d => d.Payload)) + "]}";
        var url = Url(options.Hosts(connection.Environment).Api, "/api/v1/receiptsubmissions");

        FiscalHttpResponse? answer = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (token, problem) = await TokenAsync(connection, fresh: attempt > 0, cancellationToken);
            if (token is null) return All(batch, FiscalSendResult.NotSent, problem!);
            answer = await transport!.SendAsync(new FiscalHttpRequest("POST", url, Bearer(token), body), cancellationToken);
            if (answer.StatusCode != 401) break;   // an expired token: signed in again once
        }

        if (!answer!.Answered) return All(batch, FiscalSendResult.NotSent, Unreachable(answer));
        if (answer.StatusCode == 401) return All(batch, FiscalSendResult.NotSent, "The tax authority did not accept this till's sign-in for sending.");
        if (answer.StatusCode == 422)
            return All(batch, FiscalSendResult.NotSent, "The tax authority received the same batch less than 10 minutes ago; it is sent again later.");
        if (!answer.IsSuccess) return All(batch, FiscalSendResult.NotSent, Refused(answer));

        using var json = Parse(answer.Body);
        if (json is null) return All(batch, FiscalSendResult.NotSent, "The tax authority's answer could not be read.");
        var root = json.RootElement;
        var submission = Text(root, "submissionUUID") ?? Text(root, "submissionId");
        var rejected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items(root, "rejectedDocuments"))
        {
            var reason = Property(item, "error") is { } error ? Describe(error) : "The tax authority refused it without a reason.";
            if (Text(item, "uuid") is { Length: > 0 } uuid) rejected[uuid] = reason;
            else if (Text(item, "receiptNumber") is { Length: > 0 } number) rejected["#" + number] = reason;
        }

        return batch.Select(d =>
                rejected.TryGetValue(d.Hash, out var why) || rejected.TryGetValue("#" + d.Reference, out why)
                    ? new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Rejected, submission, why)
                    : new FiscalSendOutcome(d.DocumentId, FiscalSendResult.Sent, submission))   // accepted for validation: the answer is read later
            .ToList();
    }

    // ---- reading a submission back

    public async Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken)
    {
        if (input.Connection is not { } connection || transport is null) return [];

        var outcomes = new List<FiscalSendOutcome>();
        foreach (var submission in input.Documents.Where(d => !string.IsNullOrWhiteSpace(d.AuthorityReference)).GroupBy(d => d.AuthorityReference!))
        {
            var answers = await ReadSubmissionAsync(connection, submission.Key, cancellationToken);
            if (answers is null) continue;   // no answer now: asked again next round
            foreach (var document in submission)
                if (answers.TryGetValue(document.Hash, out var outcome)) outcomes.Add(outcome with { DocumentId = document.DocumentId });
        }

        return outcomes;
    }

    /// <summary>Each receipt's answer in a submission (by UUID): Valid = accepted, Invalid / Cancelled = refused; null when unreachable.</summary>
    private async Task<Dictionary<string, FiscalSendOutcome>?> ReadSubmissionAsync(FiscalTillConnection connection, string submission, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, FiscalSendOutcome>(StringComparer.OrdinalIgnoreCase);
        for (int page = 1, pages = 1; page <= pages && page <= 50; page++)
        {
            var url = Url(options.Hosts(connection.Environment).Api,
                $"/api/v1/receiptsubmissions/{Uri.EscapeDataString(submission)}/details?PageNo={page}&PageSize=100");
            FiscalHttpResponse? answer = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (token, _) = await TokenAsync(connection, fresh: attempt > 0, cancellationToken);
                if (token is null) return null;
                answer = await transport!.SendAsync(new FiscalHttpRequest("GET", url, Bearer(token)), cancellationToken);
                if (answer.StatusCode != 401) break;
            }

            if (answer is not { IsSuccess: true } || Parse(answer.Body) is not { } json) return null;
            using (json)
            {
                var root = json.RootElement;
                if (Property(root, "metadata") is { } metadata && Number(metadata, "totalPages") is { } total) pages = (int)Math.Max(1, total);
                foreach (var receipt in Items(root, "receipts"))
                {
                    if (Text(receipt, "uuid") is not { Length: > 0 } uuid) continue;
                    switch (Text(receipt, "status")?.ToLowerInvariant())
                    {
                        case "valid":
                            result[uuid] = new FiscalSendOutcome(Guid.Empty, FiscalSendResult.Accepted, submission);
                            break;
                        case "invalid":
                            result[uuid] = new FiscalSendOutcome(Guid.Empty, FiscalSendResult.Rejected, submission,
                                string.Join(" ", Items(receipt, "errors").Select(e => Property(e, "error") is { } inner ? Describe(inner) : Describe(e)).Where(t => t.Length > 0))
                                    is { Length: > 0 } reasons ? reasons : "The tax authority found the receipt invalid without a reason.");
                            break;
                        case "cancelled":
                            result[uuid] = new FiscalSendOutcome(Guid.Empty, FiscalSendResult.Rejected, submission, "The receipt was cancelled at the tax authority.");
                            break;
                    }
                }
            }
        }

        return result;
    }

    // ---- helpers

    private static string Url(string host, string path) => host.TrimEnd('/') + path;

    private static Dictionary<string, string> Bearer(string token) => new() { ["Authorization"] = "Bearer " + token };

    private static IReadOnlyList<FiscalSendOutcome> All(FiscalSendInput input, FiscalSendResult result, string reason) => All(input.Documents, result, reason);

    private static IReadOnlyList<FiscalSendOutcome> All(IReadOnlyList<FiscalOutgoingDocument> documents, FiscalSendResult result, string reason)
        => documents.Select(d => new FiscalSendOutcome(d.DocumentId, result, Reason: reason)).ToList();

    private static string Unreachable(FiscalHttpResponse answer)
        => "The tax authority could not be reached" + (string.IsNullOrWhiteSpace(answer.Body) ? "." : $": {answer.Body}");

    /// <summary>A refused submission (BadStructure, MaximumSizeExceeded, IncorrectSubmitter ...): the ETA's words, or its status.</summary>
    private static string Refused(FiscalHttpResponse answer)
    {
        using var json = Parse(answer.Body);
        var said = json is null ? null : Property(json.RootElement, "error") is { } error ? Describe(error) : Describe(json.RootElement);
        return $"The tax authority refused the batch ({answer.StatusCode})" + (string.IsNullOrWhiteSpace(said) ? "." : $": {said}");
    }

    /// <summary>An ETA error object as one line: its message (or code), the field it concerns, and its details.</summary>
    internal static string Describe(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String) return error.GetString() ?? string.Empty;
        if (error.ValueKind != JsonValueKind.Object) return string.Empty;
        var message = Text(error, "message") ?? Text(error, "error") ?? Text(error, "errorCode") ?? Text(error, "code") ?? string.Empty;
        if (Text(error, "propertyPath") is { Length: > 0 } path) message += $" ({path})";
        var details = Items(error, "details").Concat(Items(error, "innerError")).Select(Describe).Where(t => t.Length > 0).ToList();
        if (details.Count > 0) message += (message.Length > 0 ? ": " : string.Empty) + string.Join("; ", details);
        return message.Trim();
    }

    private static JsonDocument? Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try { return JsonDocument.Parse(body); }
        catch (JsonException) { return null; }
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return null;
    }

    private static string? Text(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static double? Number(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    private static IEnumerable<JsonElement> Items(JsonElement element, string name)
        => Property(element, name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];
}
