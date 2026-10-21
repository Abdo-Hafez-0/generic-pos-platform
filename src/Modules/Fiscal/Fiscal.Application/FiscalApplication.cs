using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;

namespace Fiscal.Application.Security
{
    /// <summary>The capabilities of the Fiscal module (declared to the platform catalog by <see cref="FiscalCapabilityProvider"/>).</summary>
    public static class FiscalCapabilities
    {
        public const string Module = "fiscal";

        /// <summary>Set up electronic receipts (country, the shop's tax details, the till's registration) and follow the documents.</summary>
        public const string Manage = "fiscal.manage";

        public static IReadOnlyList<CapabilityDescriptor> All { get; } =
        [
            new(Manage, Module, "Manage electronic receipts",
                "Choose the tax authority, set the shop's tax details, register the till and follow the documents sent to the authority.",
                LicenseRequirement.Module, IsSensitive: true),
        ];
    }

    public sealed class FiscalCapabilityProvider : ICapabilityProvider
    {
        public IReadOnlyCollection<CapabilityDescriptor> GetCapabilities() => FiscalCapabilities.All;
    }
}

namespace Fiscal.Application.Abstractions
{
    /// <summary>Saves changes to the Fiscal module's own persistence (FiscalDbContext).</summary>
    public interface IFiscalUnitOfWork
    {
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// MISS-11a: a country part (Egypt's ETA, 11c; Saudi Arabia's ZATCA, 11d) - it builds the authority's document, its hash and QR, and sends
    /// them. Without the part of the chosen country nothing is issued (the core alone does nothing).
    /// </summary>
    public interface IFiscalCountryPart
    {
        FiscalCountry Country { get; }

        /// <summary>
        /// Builds the document of a sale or return at its place in the chain. Pure and offline (the receipt prints its QR at once). A failure
        /// (for example a product without the authority's code) is a failed result with a reason the shop can act on - never an exception.
        /// </summary>
        Result<FiscalBuiltDocument> Build(FiscalBuildInput input);

        /// <summary>
        /// MISS-11a-3: hands documents to the authority (in the order of the chain; a batch up to the part's limit). One outcome per document.
        /// Being offline, the authority being unavailable or the till not being registered are NotSent outcomes (tried again later), never
        /// exceptions.
        /// </summary>
        Task<IReadOnlyList<FiscalSendOutcome>> SendAsync(FiscalSendInput input, CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: asks the authority for its answer on documents handed over earlier (Sent). One outcome per document.</summary>
        Task<IReadOnlyList<FiscalSendOutcome>> CheckAsync(FiscalSendInput input, CancellationToken cancellationToken = default);

        /// <summary>MISS-11c: when a sale must name its buyer (Egypt: a person's name and national ID above a total); null = never.</summary>
        Fiscal.Contracts.Models.FiscalBuyerRule? BuyerRule => null;
    }

    /// <summary>MISS-11a-3: documents to send or to check, with the seller and this till.</summary>
    public sealed record FiscalSendInput(IReadOnlyList<FiscalOutgoingDocument> Documents, FiscalSettings Settings, Guid TillId, string TillName,
        FiscalTillRegistration Registration);

    /// <summary>MISS-11a-3: a document as the sender hands it to a country part.</summary>
    public sealed record FiscalOutgoingDocument(Guid DocumentId, FiscalDocumentKind Kind, string Reference, long Number, string Hash, string Payload,
        DateTime IssuedAt, string? AuthorityReference);

    /// <summary>MISS-11a-3: what the authority did with a document.</summary>
    public enum FiscalSendResult
    {
        /// <summary>Not handed over (offline, unavailable, not registered): tried again later.</summary>
        NotSent = 0,

        /// <summary>Handed over; the answer is checked later.</summary>
        Sent = 1,

        Accepted = 2,
        Rejected = 3,

        /// <summary>Checking: no answer yet.</summary>
        StillWaiting = 4,
    }

    /// <summary>MISS-11a-3: the outcome for one document: what happened, the authority's reference, and its reasons (a refusal, or why not sent).</summary>
    public sealed record FiscalSendOutcome(Guid DocumentId, FiscalSendResult Result, string? AuthorityReference = null, string? Reason = null);

    /// <summary>
    /// MISS-11a-2: what a country part builds from: the request, the seller, and the document's place in this till's chain. MISS-11b:
    /// <see cref="LineCodes"/> the authority's codes of each line's product (by product id; what the shop has not set is null).
    /// </summary>
    public sealed record FiscalBuildInput(Fiscal.Contracts.Models.FiscalDocumentRequest Request, FiscalSettings Settings, Guid TillId, string TillName,
        long Number, string? PreviousHash, IReadOnlyDictionary<Guid, FiscalLineCodes>? LineCodes = null, FiscalOriginalDocument? Original = null,
        string? DeviceSerial = null);

    /// <summary>
    /// MISS-11c: a return's sale as its fiscal document recorded it (Egypt: the return carries the sale's UUID as referenceUUID and its buyer);
    /// null when the sale has no document (sold before receipts were on) or it was not built.
    /// </summary>
    public sealed record FiscalOriginalDocument(Guid DocumentId, string Hash, string Payload, DateTime IssuedAt);

    /// <summary>MISS-11a-2: the authority's document as built: its content, its hash (the chain's next link) and what the QR code holds.</summary>
    public sealed record FiscalBuiltDocument(string Payload, string Hash, string? QrPayload);
}

namespace Fiscal.Application.Repositories
{
    /// <summary>MISS-11a-2: the fiscal documents.</summary>
    public interface IFiscalDocumentRepository
    {
        /// <summary>The document already made for a sale or a return, or null.</summary>
        Task<FiscalDocument?> FindBySourceAsync(FiscalDocumentKind kind, Guid sourceId, CancellationToken cancellationToken = default);

        Task AddAsync(FiscalDocument document, CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: Waiting documents due for an attempt (all when <paramref name="force"/>), in the order of the chain.</summary>
        Task<IReadOnlyList<FiscalDocument>> GetDueAsync(DateTime nowUtc, bool force, int take, CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: Sent documents whose answer is awaited, oldest first.</summary>
        Task<IReadOnlyList<FiscalDocument>> GetSentAsync(int take, CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: the newest documents, optionally in some states only.</summary>
        Task<IReadOnlyList<FiscalDocument>> ListAsync(IReadOnlyCollection<FiscalDocumentState>? states, int take, CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: what needs attention or waits: counts and the oldest outstanding document's time.</summary>
        Task<FiscalDocumentCounts> CountAsync(DateTime managerSinceUtc, DateTime tillSinceUtc, CancellationToken cancellationToken = default);
    }

    /// <summary>MISS-11a-3: outstanding = Waiting or Sent; "over" = outstanding and created before the manager / till threshold.</summary>
    public sealed record FiscalDocumentCounts(int Outstanding, int NotBuilt, int Rejected, int OverManagerLimit, int OverTillLimit, DateTime? OldestOutstandingAt);

    public interface IFiscalSetupRepository
    {
        /// <summary>The settings row, or null before anything was saved.</summary>
        Task<FiscalSettings?> GetSettingsAsync(CancellationToken cancellationToken = default);

        Task AddSettingsAsync(FiscalSettings settings, CancellationToken cancellationToken = default);

        /// <summary>This till (the installation's only till), or null before its first use.</summary>
        Task<FiscalTill?> GetTillAsync(CancellationToken cancellationToken = default);

        Task AddTillAsync(FiscalTill till, CancellationToken cancellationToken = default);
    }
}

namespace Fiscal.Application.DTOs
{
    /// <summary>MISS-11a-3: one document for the Fiscal screen's list.</summary>
    public sealed record FiscalDocumentDto(Guid DocumentId, FiscalDocumentKind Kind, string Reference, string? OriginalReference, long? Number, decimal Total,
        DateTime IssuedAt, FiscalDocumentState State, string? StateReason, int Attempts, DateTime? LastAttemptAt, string? AuthorityReference);

    /// <summary>
    /// MISS-11a-3: what the warnings count: waiting (outstanding), not built, refused, and waiting longer than the managers' limit.
    /// MISS-11b: and the products, units and tax rates still without the authority's code.
    /// </summary>
    public sealed record FiscalAlertsDto(int Outstanding, int NotBuilt, int Rejected, int OverLimit, double LimitHours,
        int MissingProductCodes = 0, int MissingUnitCodes = 0, int MissingTaxCodes = 0)
    {
        public bool NeedsAttention => NotBuilt > 0 || Rejected > 0 || OverLimit > 0 || MissingProductCodes + MissingUnitCodes + MissingTaxCodes > 0;
    }

    /// <summary>MISS-11a-3: the outcome of a sending round.</summary>
    public sealed record FiscalSendSummary(bool Ran, int Sent, int Accepted, int Rejected, int NotSent, string? LastReason)
    {
        public static FiscalSendSummary NothingToDo { get; } = new(false, 0, 0, 0, 0, null);
    }
    /// <summary>
    /// MISS-11a: the Fiscal screen's setup - the settings, this till, and whether the chosen country's part is installed (without it nothing
    /// is issued yet) and whether the country is locked (documents exist).
    /// </summary>
    public sealed record FiscalSetupDto(
        FiscalCountry Country, string? TaxNumber, string? BranchCode, string? ActivityCode, string? SellerNameArabic, FiscalAddress Address,
        DateTime? UpdatedAt, string? UpdatedBy,
        string TillName, FiscalTillRegistration TillRegistration, long NextNumber, bool CountryLocked,
        IReadOnlyList<FiscalCountry> InstalledCountryParts, string? DeviceSerial = null)
    {
        public bool IsOn => Country != FiscalCountry.None;

        /// <summary>The chosen country's part is installed, so documents are issued (Country None: nothing to issue).</summary>
        public bool IsIssuing => IsOn && InstalledCountryParts.Contains(Country);
    }
}

namespace Fiscal.Application.Queries
{
    using Fiscal.Application.Abstractions;
    using Fiscal.Application.DTOs;
    using Fiscal.Application.Repositories;
    using Fiscal.Application.Security;

    public sealed record GetFiscalSetupQuery;

    /// <summary>MISS-11a: the setup for the Fiscal screen (fiscal.manage). Creates this till's record on first use.</summary>
    public sealed class GetFiscalSetupQueryHandler(IFiscalSetupRepository repository, IFiscalUnitOfWork unitOfWork, IAuthorizationService authorization,
        IEnumerable<IFiscalCountryPart>? countryParts = null, TimeProvider? timeProvider = null)
    {
        public async Task<Result<FiscalSetupDto>> HandleAsync(GetFiscalSetupQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<FiscalSetupDto>(allowed.Error);

            var settings = await repository.GetSettingsAsync(cancellationToken);
            var till = await FiscalTillAccess.GetOrCreateAsync(repository, unitOfWork, timeProvider, cancellationToken);
            var saved = settings is not null;
            settings ??= FiscalSettings.Default();
            return Result.Success(new FiscalSetupDto(settings.Country, settings.TaxNumber, settings.BranchCode, settings.ActivityCode,
                settings.SellerNameArabic, settings.Address, saved ? settings.UpdatedAt : null, settings.UpdatedBy,
                till.Name, till.Registration, till.Counter + 1, till.HasIssued,
                (countryParts ?? []).Select(p => p.Country).Distinct().ToList(), till.DeviceSerial));
        }
    }

    /// <summary>This till's record, created the first time it is needed (named after the computer).</summary>
    internal static class FiscalTillAccess
    {
        public static async Task<FiscalTill> GetOrCreateAsync(IFiscalSetupRepository repository, IFiscalUnitOfWork unitOfWork, TimeProvider? timeProvider,
            CancellationToken cancellationToken)
        {
            if (await repository.GetTillAsync(cancellationToken) is { } till)
            {
                if (till.EnsureDeviceSerial()) await unitOfWork.SaveChangesAsync(cancellationToken);   // MISS-11c: a till made before its serial
                return till;
            }

            till = FiscalTill.Create(Environment.MachineName, (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            await repository.AddTillAsync(till, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return till;
        }
    }
}

namespace Fiscal.Application.Commands
{
    using Fiscal.Contracts.Models;
    using Fiscal.Application.Abstractions;
    using Fiscal.Application.DTOs;
    using Fiscal.Application.Queries;
    using Fiscal.Application.Repositories;
    using Fiscal.Application.Security;
    using Platform.Application.Abstractions.Auditing;

    /// <summary>MISS-11a: the country and the shop's tax details.</summary>
    public sealed record SaveFiscalSettingsCommand(FiscalCountry Country, string? TaxNumber, string? BranchCode, string? ActivityCode,
        string? SellerNameArabic, FiscalAddress? Address);

    /// <summary>
    /// MISS-11a: saves the fiscal settings (fiscal.manage, audited). The country cannot change once this till has issued a document (owner's
    /// decision 2026-10-20).
    /// </summary>
    public sealed class SaveFiscalSettingsCommandHandler(IFiscalSetupRepository repository, IFiscalUnitOfWork unitOfWork, IAuthorizationService authorization,
        ICurrentUser? currentUser = null, TimeProvider? timeProvider = null, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(SaveFiscalSettingsCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var till = await FiscalTillAccess.GetOrCreateAsync(repository, unitOfWork, timeProvider, cancellationToken);
            var settings = await repository.GetSettingsAsync(cancellationToken);
            var isNew = settings is null;
            settings ??= FiscalSettings.Default();
            var before = settings.Country;

            var updated = settings.Update(command.Country, command.TaxNumber, command.BranchCode, command.ActivityCode, command.SellerNameArabic,
                command.Address, till.HasIssued, currentUser is { IsAuthenticated: true } me ? me.UserName : null,
                (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            if (updated.IsFailure) return updated;

            if (isNew) await repository.AddSettingsAsync(settings, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var summary = before == settings.Country
                ? $"Electronic receipt settings saved (country: {settings.Country})."
                : $"Electronic receipts country changed from {before} to {settings.Country}.";
            await businessEvents.TryRecordAsync(BusinessEvent.Create(FiscalCapabilities.Module, "fiscal.settings-saved", "fiscal-settings", "shop", summary,
                $"taxNumber={settings.TaxNumber ?? "none"};branch={settings.BranchCode ?? "none"};activity={settings.ActivityCode ?? "none"}"));
            return Result.Success();
        }
    }

    /// <summary>
    /// MISS-11a-2: makes the fiscal document of a sale or a return, inside the caller's transaction (POS checkout / refund). Electronic
    /// receipts off, or no part for the chosen country: nothing. Otherwise the part builds it at the next place of this till's chain and it is
    /// stored to be sent (11a-3); when the part cannot build it, a NotBuilt document records why (the sale stands - owner's decision: keep
    /// selling) and the chain is not advanced. The same source asked twice returns the document already made.
    /// No capability check: only reachable through Fiscal.Contracts' IFiscalRecorder, inside an operation the user was authorized for.
    /// </summary>
    public sealed class RecordFiscalDocumentCommandHandler(IFiscalSetupRepository setup, IFiscalDocumentRepository documents, IFiscalUnitOfWork unitOfWork,
        IEnumerable<IFiscalCountryPart>? countryParts = null, TimeProvider? timeProvider = null, FiscalCodeBook? codeBook = null)
    {
        internal async Task<FiscalDocumentResult> ExecuteAsync(FiscalDocumentRequest request, CancellationToken cancellationToken = default)
        {
            var settings = await setup.GetSettingsAsync(cancellationToken);
            if (settings is not { IsOn: true }) return FiscalDocumentResult.NothingToIssue();
            var part = (countryParts ?? []).LastOrDefault(p => p.Country == settings.Country);   // the last registered wins (a host may replace a part)
            if (part is null) return FiscalDocumentResult.NothingToIssue();

            var kind = (FiscalDocumentKind)(int)request.Kind;
            if (await documents.FindBySourceAsync(kind, request.SourceId, cancellationToken) is { } existing)
                return existing.State == FiscalDocumentState.NotBuilt
                    ? FiscalDocumentResult.NotBuilt(existing.Id, existing.StateReason ?? string.Empty)
                    : FiscalDocumentResult.Issued(existing.Id, existing.Number!.Value, existing.QrPayload);

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            var issuedAt = DateTime.SpecifyKind(request.IssuedAtUtc, DateTimeKind.Utc);

            // MISS-11c: a return names its sale's document (Egypt: referenceUUID)
            FiscalOriginalDocument? original = null;
            if (kind == FiscalDocumentKind.Return && request.OriginalSourceId is { } saleId
                && await documents.FindBySourceAsync(FiscalDocumentKind.Sale, saleId, cancellationToken) is { Hash: { } saleHash, Payload: { } salePayload } sale
                && sale.State != FiscalDocumentState.NotBuilt)
                original = new FiscalOriginalDocument(sale.Id, saleHash, salePayload, sale.IssuedAt);

            Result<FiscalBuiltDocument> built;
            try
            {
                var lineCodes = codeBook is null ? null
                    : await codeBook.ForProductsAsync(request.Lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken);   // MISS-11b
                built = part.Build(new FiscalBuildInput(request, settings, till.Id, till.Name, till.Counter + 1, till.LastDocumentHash, lineCodes,
                    original, till.DeviceSerial));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                built = Result.Failure<FiscalBuiltDocument>(Error.Failure("Fiscal.Build.Failed", ex.Message));
            }

            FiscalDocument document;
            if (built.IsFailure || string.IsNullOrWhiteSpace(built.Value.Hash))
            {
                document = FiscalDocument.NotBuilt(kind, request.SourceId, request.Reference, request.OriginalSourceId, request.OriginalReference, settings.Country,
                    till.Id, request.Total, issuedAt, built.IsFailure ? built.Error.Description : "The country part returned a document without its hash.", now);
            }
            else
            {
                var previous = till.LastDocumentHash;
                var number = till.Chain(built.Value.Hash);
                document = FiscalDocument.Issued(kind, request.SourceId, request.Reference, request.OriginalSourceId, request.OriginalReference, settings.Country,
                    till.Id, number, previous, built.Value.Hash, built.Value.Payload, built.Value.QrPayload, request.Total, issuedAt, now);
            }

            await documents.AddAsync(document, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return document.State == FiscalDocumentState.NotBuilt
                ? FiscalDocumentResult.NotBuilt(document.Id, document.StateReason!)
                : FiscalDocumentResult.Issued(document.Id, document.Number!.Value, document.QrPayload);
        }
    }

    /// <summary>
    /// MISS-11a-3: one round of the outbox. Waiting documents due for an attempt go to the country part in chain order (a batch); Sent ones are
    /// checked for the authority's answer. Not handed over: tried again later with a growing pause (1, 2, 4 ... up to 30 minutes). Nothing is
    /// ever deleted. Receipts off or no country part: nothing to do. "Send now" (the screen) forces an attempt and needs fiscal.manage; the
    /// background sender runs it without a user (internal).
    /// </summary>
    public sealed class SendFiscalDocumentsCommandHandler(IFiscalSetupRepository setup, IFiscalDocumentRepository documents, IFiscalUnitOfWork unitOfWork,
        IAuthorizationService authorization, IEnumerable<IFiscalCountryPart>? countryParts = null, TimeProvider? timeProvider = null,
        IBusinessEventSink? businessEvents = null)
    {
        public const int BatchSize = 100;

        /// <summary>"Send now" on the Fiscal screen (fiscal.manage, audited).</summary>
        public async Task<Result<FiscalSendSummary>> HandleAsync(CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<FiscalSendSummary>(allowed.Error);

            var summary = await ExecuteAsync(force: true, cancellationToken);
            if (summary.Ran)
                await businessEvents.TryRecordAsync(BusinessEvent.Create(FiscalCapabilities.Module, "fiscal.sent-now", "fiscal-documents", "outbox",
                    $"Electronic receipts sent on request: {summary.Sent} handed over, {summary.Accepted} accepted, {summary.Rejected} refused, {summary.NotSent} not sent."));
            return Result.Success(summary);
        }

        internal async Task<FiscalSendSummary> ExecuteAsync(bool force, CancellationToken cancellationToken = default)
        {
            var settings = await setup.GetSettingsAsync(cancellationToken);
            if (settings is not { IsOn: true }) return FiscalSendSummary.NothingToDo;
            var part = (countryParts ?? []).LastOrDefault(p => p.Country == settings.Country);   // the last registered wins (a host may replace a part)
            if (part is null) return FiscalSendSummary.NothingToDo;

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            var (sent, accepted, rejected, notSent) = (0, 0, 0, 0);
            string? lastReason = null;

            // 1. what waits goes out, in the order of the chain
            var due = await documents.GetDueAsync(now, force, BatchSize, cancellationToken);
            if (due.Count > 0)
            {
                var outcomes = await AskAsync(part.SendAsync, due, settings, till, cancellationToken);
                foreach (var document in due)
                {
                    var outcome = outcomes.GetValueOrDefault(document.Id) ?? new FiscalSendOutcome(document.Id, FiscalSendResult.NotSent, Reason: "The country part gave no answer for this document.");
                    switch (outcome.Result)
                    {
                        case FiscalSendResult.Sent: document.MarkSent(outcome.AuthorityReference, now); sent++; break;
                        case FiscalSendResult.Accepted: document.MarkAccepted(outcome.AuthorityReference, now); accepted++; break;
                        case FiscalSendResult.Rejected: document.MarkRejected(outcome.Reason ?? string.Empty, now); rejected++; break;
                        default: document.MarkNotSent(outcome.Reason ?? string.Empty, now); notSent++; lastReason = outcome.Reason ?? lastReason; break;
                    }
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);   // what was handed over is kept even if checking fails
            }

            // 2. what was handed over earlier: the authority's answer
            var waiting = await documents.GetSentAsync(BatchSize, cancellationToken);
            if (waiting.Count > 0)
            {
                var outcomes = await AskAsync(part.CheckAsync, waiting, settings, till, cancellationToken);
                foreach (var document in waiting)
                {
                    if (outcomes.GetValueOrDefault(document.Id) is not { } outcome) continue;
                    if (outcome.Result == FiscalSendResult.Accepted) { document.MarkAccepted(outcome.AuthorityReference, now); accepted++; }
                    else if (outcome.Result == FiscalSendResult.Rejected) { document.MarkRejected(outcome.Reason ?? string.Empty, now); rejected++; }
                }
            }

            if (waiting.Count > 0) await unitOfWork.SaveChangesAsync(cancellationToken);
            return new FiscalSendSummary(due.Count > 0 || waiting.Count > 0, sent, accepted, rejected, notSent, lastReason);
        }

        private static async Task<Dictionary<Guid, FiscalSendOutcome>> AskAsync(
            Func<FiscalSendInput, CancellationToken, Task<IReadOnlyList<FiscalSendOutcome>>> call, IReadOnlyList<FiscalDocument> batch,
            FiscalSettings settings, FiscalTill till, CancellationToken cancellationToken)
        {
            var input = new FiscalSendInput(batch.Select(d => new FiscalOutgoingDocument(d.Id, d.Kind, d.Reference, d.Number ?? 0, d.Hash ?? string.Empty,
                d.Payload ?? string.Empty, d.IssuedAt, d.AuthorityReference)).ToList(), settings, till.Id, till.Name, till.Registration);
            try
            {
                return (await call(input, cancellationToken)).GroupBy(o => o.DocumentId).ToDictionary(g => g.Key, g => g.Last());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // a broken connection or a fault in the part: nothing was answered, everything is tried again later
                return batch.ToDictionary(d => d.Id, d => new FiscalSendOutcome(d.Id, FiscalSendResult.NotSent, Reason: ex.Message));
            }
        }
    }
}

namespace Fiscal.Application.Queries
{
    using Fiscal.Application.DTOs;
    using Fiscal.Application.Repositories;
    using Fiscal.Application.Security;

    /// <summary>MISS-11a-3: the documents for the Fiscal screen, newest first. <see cref="States"/> null = all.</summary>
    public sealed record ListFiscalDocumentsQuery(IReadOnlyCollection<FiscalDocumentState>? States = null)
    {
        public const int MaxResults = 500;
    }

    public sealed class ListFiscalDocumentsQueryHandler(IFiscalDocumentRepository documents, IAuthorizationService authorization)
    {
        public async Task<Result<IReadOnlyList<FiscalDocumentDto>>> HandleAsync(ListFiscalDocumentsQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<IReadOnlyList<FiscalDocumentDto>>(allowed.Error);

            var found = await documents.ListAsync(query.States, ListFiscalDocumentsQuery.MaxResults, cancellationToken);
            return Result.Success<IReadOnlyList<FiscalDocumentDto>>(found.Select(d => new FiscalDocumentDto(d.Id, d.Kind, d.Reference, d.OriginalReference,
                d.Number, d.Total, d.IssuedAt, d.State, d.StateReason, d.Attempts, d.LastAttemptAt, d.AuthorityReference)).ToList());
        }
    }

    /// <summary>
    /// MISS-11a-3 (owner's decision: warnings for managers and the till): what the warnings say - documents the part could not build or the
    /// authority refused, and outstanding documents older than the manager's limit (6 hours) and the till's (12 hours). No permission: the
    /// till and the shell read it (the shell shows the full warning only to those who manage e-receipts).
    /// </summary>
    public sealed class FiscalAlertsReader(IFiscalSetupRepository setup, IFiscalDocumentRepository documents, FiscalWarningOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        internal async Task<FiscalDocumentCounts?> ReadAsync(CancellationToken cancellationToken = default)
        {
            if (await setup.GetSettingsAsync(cancellationToken) is not { IsOn: true }) return null;
            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var limits = options ?? new FiscalWarningOptions();
            return await documents.CountAsync(now.AddHours(-limits.ManagerHours), now.AddHours(-limits.TillHours), cancellationToken);
        }
    }

    public sealed record GetFiscalAlertsQuery;

    /// <summary>MISS-11a-3: the alert counts for the Fiscal screen and the status bar (fiscal.manage); null when receipts are off.</summary>
    public sealed class GetFiscalAlertsQueryHandler(FiscalAlertsReader alerts, IAuthorizationService authorization, FiscalWarningOptions? options = null,
        IFiscalSetupRepository? setup = null, Fiscal.Application.Abstractions.FiscalCodeBook? codeBook = null)
    {
        public async Task<Result<FiscalAlertsDto?>> HandleAsync(GetFiscalAlertsQuery query, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<FiscalAlertsDto?>(allowed.Error);

            var counts = await alerts.ReadAsync(cancellationToken);
            if (counts is null) return Result.Success<FiscalAlertsDto?>(null);

            // MISS-11b (owner's decision: missing codes in the status bar too)
            var country = setup is null ? FiscalCountry.None : (await setup.GetSettingsAsync(cancellationToken))?.Country ?? FiscalCountry.None;
            var missing = codeBook is null || country == FiscalCountry.None ? null : await codeBook.OverviewAsync(country, cancellationToken);
            return Result.Success<FiscalAlertsDto?>(new FiscalAlertsDto(counts.Outstanding, counts.NotBuilt, counts.Rejected, counts.OverManagerLimit,
                (options ?? new FiscalWarningOptions()).ManagerHours, missing?.MissingProducts ?? 0, missing?.MissingUnits ?? 0, missing?.MissingTaxRates ?? 0));
        }
    }

    /// <summary>MISS-11a-3: when documents have waited too long (section "Fiscal:Warnings").</summary>
    public sealed class FiscalWarningOptions
    {
        public const string SectionName = "Fiscal:Warnings";

        /// <summary>Outstanding documents older than this are in the managers' warning (the authorities want them within 24 hours).</summary>
        public double ManagerHours { get; set; } = 6;

        /// <summary>Outstanding documents older than this are also said on the till (owner's decision: 12 hours).</summary>
        public double TillHours { get; set; } = 12;
    }
}
