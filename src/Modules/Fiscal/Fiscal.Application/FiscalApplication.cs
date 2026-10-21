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

        /// <summary>
        /// MISS-11c-3: tries the till's sign-in with the authority (Egypt: asks for a token). A failed result says why (the authority's
        /// words, or no connection); never an exception.
        /// </summary>
        Task<Result> TestSignInAsync(FiscalTillConnection connection, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Failure(Error.Conflict("Fiscal.SignIn.NotSupported", "This country's part has no sign-in to test.")));

        /// <summary>MISS-11c-2: what the printed receipt carries under its QR code (default: "Electronic receipt" and the short hash).</summary>
        Fiscal.Contracts.Models.FiscalReceiptMark Mark(string hash, string qrPayload) => new(qrPayload, "Electronic receipt", FiscalMarks.Short(hash));
    }

    /// <summary>MISS-11c-2: the marks' shared pieces.</summary>
    public static class FiscalMarks
    {
        /// <summary>A long hash shortened for the paper: its first and last 8 characters ("68e656b2...f368f320").</summary>
        public static string Short(string hash) => hash.Length <= 19 ? hash : hash[..8] + "..." + hash[^8..];

        /// <summary>The mark of a document that was built and has a QR code, from its country's part; null otherwise.</summary>
        public static Fiscal.Contracts.Models.FiscalReceiptMark? Of(FiscalDocument document, IEnumerable<IFiscalCountryPart>? parts)
            => document is { State: not FiscalDocumentState.NotBuilt, Hash: { } hash, QrPayload: { Length: > 0 } qr }
               && (parts ?? []).LastOrDefault(p => p.Country == document.Country) is { } part
                ? part.Mark(hash, qr)
                : null;
    }

    /// <summary>
    /// MISS-11a-3: documents to send or to check, with the seller and this till. MISS-11c-3: <see cref="Connection"/> how this till signs in
    /// (null: not registered, or its secrets cannot be opened on this computer - nothing can be sent).
    /// </summary>
    public sealed record FiscalSendInput(IReadOnlyList<FiscalOutgoingDocument> Documents, FiscalSettings Settings, Guid TillId, string TillName,
        FiscalTillRegistration Registration, FiscalTillConnection? Connection = null, bool FreshSignIn = false);   // MISS-11c-4: after a late request's approval

    /// <summary>
    /// MISS-11c-3: everything a part needs to sign this till in: the environment, the portal's client ID and secret, the POS serial, OS
    /// version, model framework and pre-shared key. Built for each round from the till's record and the protected secrets; never stored or
    /// logged as a whole.
    /// </summary>
    public sealed record FiscalTillConnection(FiscalEnvironment Environment, string ClientId, string ClientSecret, string PosSerial, string PosOsVersion,
        string PosModelFramework, string PresharedKey)
    {
        public override string ToString() => $"{Environment} {ClientId} {PosSerial}";   // the secrets never reach a log line
    }

    /// <summary>MISS-11c-3: the till's secrets - the client secret and the pre-shared key the authority's portal gave.</summary>
    public sealed record FiscalTillSecrets(string ClientSecret, string PresharedKey)
    {
        public override string ToString() => "(secrets)";
    }

    /// <summary>
    /// MISS-11c-3: keeps the till's secrets protected by Windows (DPAPI, this Windows user) in a file of their own - never in the database,
    /// so never in a backup, and never logged.
    /// </summary>
    public interface IFiscalSecretStore
    {
        /// <summary>False when this computer cannot protect secrets (they are then never written).</summary>
        bool CanProtect { get; }

        /// <summary>The secrets, or null when none were saved or they cannot be opened here (another computer or Windows user).</summary>
        Task<FiscalTillSecrets?> ReadAsync(CancellationToken cancellationToken = default);

        Task WriteAsync(FiscalTillSecrets secrets, CancellationToken cancellationToken = default);
    }

    /// <summary>MISS-11c-3: the till's connection when it is registered and its secrets open; null otherwise.</summary>
    public static class FiscalConnections
    {
        public static async Task<FiscalTillConnection?> ReadAsync(FiscalTill till, IFiscalSecretStore? secrets, CancellationToken cancellationToken)
        {
            if (till is not { Registration: FiscalTillRegistration.Registered, ClientId: { } clientId, PosOsVersion: { } os, PosModelFramework: { } model, DeviceSerial: { } serial }
                || till.Environment == FiscalEnvironment.None || secrets is null)
                return null;
            return await secrets.ReadAsync(cancellationToken) is { } open
                ? new FiscalTillConnection(till.Environment, clientId, open.ClientSecret, serial, os, model, open.PresharedKey)
                : null;
        }
    }

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
        string? DeviceSerial = null, FiscalEnvironment Environment = FiscalEnvironment.None,   // MISS-11c-3: Egypt's QR links to that environment's portal
        string? ReferenceOldHash = null);   // MISS-11c-4: a corrected copy names the refused document (Egypt: referenceOldUUID)

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

        /// <summary>MISS-11c-4: one document by its id, or null.</summary>
        Task<FiscalDocument?> FindAsync(Guid documentId, CancellationToken cancellationToken = default);

        /// <summary>MISS-11c-4: the documents held as Late, in the order of the chain.</summary>
        Task<IReadOnlyList<FiscalDocument>> GetLateAsync(CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: Sent documents whose answer is awaited, oldest first.</summary>
        Task<IReadOnlyList<FiscalDocument>> GetSentAsync(int take, CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: the newest documents, optionally in some states only.</summary>
        Task<IReadOnlyList<FiscalDocument>> ListAsync(IReadOnlyCollection<FiscalDocumentState>? states, int take, CancellationToken cancellationToken = default);

        /// <summary>MISS-11a-3: what needs attention or waits: counts and the oldest outstanding document's time.</summary>
        Task<FiscalDocumentCounts> CountAsync(DateTime managerSinceUtc, DateTime tillSinceUtc, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// MISS-11a-3: outstanding = Waiting or Sent; "over" = outstanding and created before the manager / till threshold. MISS-11c-4: Late, held
    /// after 24 hours (counted in the till's "over" too: they have waited).
    /// </summary>
    public sealed record FiscalDocumentCounts(int Outstanding, int NotBuilt, int Rejected, int OverManagerLimit, int OverTillLimit, DateTime? OldestOutstandingAt,
        int Late = 0);

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
        int MissingProductCodes = 0, int MissingUnitCodes = 0, int MissingTaxCodes = 0, int Late = 0)
    {
        public bool NeedsAttention => NotBuilt > 0 || Rejected > 0 || OverLimit > 0 || Late > 0 || MissingProductCodes + MissingUnitCodes + MissingTaxCodes > 0;
    }

    /// <summary>MISS-11a-3: the outcome of a sending round.</summary>
    /// <summary>
    /// MISS-11c-3: this till's registration for the screen (owner's choice: a summary box and a window) - never the secrets, only whether
    /// they are kept.
    /// </summary>
    public sealed record FiscalTillRegistrationDto(FiscalEnvironment Environment, string? ClientId, string? PosOsVersion, string? PosModelFramework,
        DateTime? RegisteredAt, string? RegisteredBy, bool EnvironmentLocked, bool HasSecrets, bool CanProtectSecrets, DateTime? LastSignInAt, bool? LastSignInOk);

    public sealed record FiscalSendSummary(bool Ran, int Sent, int Accepted, int Rejected, int NotSent, string? LastReason, int Late = 0)
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
        IReadOnlyList<FiscalCountry> InstalledCountryParts, string? DeviceSerial = null, FiscalTillRegistrationDto? Connection = null)
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
        IEnumerable<IFiscalCountryPart>? countryParts = null, TimeProvider? timeProvider = null, IFiscalSecretStore? secrets = null)
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
                (countryParts ?? []).Select(p => p.Country).Distinct().ToList(), till.DeviceSerial,
                new FiscalTillRegistrationDto(till.Environment, till.ClientId, till.PosOsVersion, till.PosModelFramework, till.RegisteredAt, till.RegisteredBy,
                    till.EnvironmentLocked, secrets is not null && await secrets.ReadAsync(cancellationToken) is not null, secrets?.CanProtect == true,
                    till.LastSignInAt, till.LastSignInOk)));
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
                    : FiscalDocumentResult.Issued(existing.Id, existing.Number!.Value, existing.QrPayload) with { Mark = FiscalMarks.Of(existing, countryParts) };

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            var issuedAt = DateTime.SpecifyKind(request.IssuedAtUtc, DateTimeKind.Utc);

            var built = await FiscalBuilding.BuildAsync(part, request, settings, till, till.Counter + 1, till.LastDocumentHash, documents, codeBook, null, cancellationToken);
            var made = FiscalBuilding.Serialize(request);   // MISS-11c-4: kept, to build it again

            FiscalDocument document;
            if (built.IsFailure || string.IsNullOrWhiteSpace(built.Value.Hash))
            {
                document = FiscalDocument.NotBuilt(kind, request.SourceId, request.Reference, request.OriginalSourceId, request.OriginalReference, settings.Country,
                    till.Id, request.Total, issuedAt, built.IsFailure ? built.Error.Description : "The country part returned a document without its hash.", now, made);
            }
            else
            {
                var previous = till.LastDocumentHash;
                var number = till.Chain(built.Value.Hash);
                document = FiscalDocument.Issued(kind, request.SourceId, request.Reference, request.OriginalSourceId, request.OriginalReference, settings.Country,
                    till.Id, number, previous, built.Value.Hash, built.Value.Payload, built.Value.QrPayload, request.Total, issuedAt, now, made);
            }

            await documents.AddAsync(document, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return document.State == FiscalDocumentState.NotBuilt
                ? FiscalDocumentResult.NotBuilt(document.Id, document.StateReason!)
                : FiscalDocumentResult.Issued(document.Id, document.Number!.Value, document.QrPayload) with { Mark = FiscalMarks.Of(document, countryParts) };
        }
    }

    /// <summary>
    /// MISS-11c-4: builds a document with the country's part - the sale's codes as they are now (MISS-11b), a return's sale document
    /// (Egypt: referenceUUID), this till's serial and environment - at a given place of the chain. A fault in the part is a failed result.
    /// </summary>
    internal static class FiscalBuilding
    {
        private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

        public static string Serialize(FiscalDocumentRequest request) => System.Text.Json.JsonSerializer.Serialize(request, Json);

        public static FiscalDocumentRequest? Deserialize(string? request)
        {
            if (string.IsNullOrWhiteSpace(request)) return null;
            try { return System.Text.Json.JsonSerializer.Deserialize<FiscalDocumentRequest>(request, Json); }
            catch (System.Text.Json.JsonException) { return null; }
        }

        public static async Task<Result<FiscalBuiltDocument>> BuildAsync(IFiscalCountryPart part, FiscalDocumentRequest request, FiscalSettings settings,
            FiscalTill till, long number, string? previousHash, IFiscalDocumentRepository documents, FiscalCodeBook? codeBook, string? referenceOldHash,
            CancellationToken cancellationToken)
        {
            // MISS-11c: a return names its sale's document (Egypt: referenceUUID)
            FiscalOriginalDocument? original = null;
            if (request.Kind == FiscalDocumentKindContract.Return && request.OriginalSourceId is { } saleId
                && await documents.FindBySourceAsync(FiscalDocumentKind.Sale, saleId, cancellationToken) is { Hash: { } saleHash, Payload: { } salePayload } sale
                && sale.State != FiscalDocumentState.NotBuilt)
                original = new FiscalOriginalDocument(sale.Id, saleHash, salePayload, sale.IssuedAt);

            try
            {
                var lineCodes = codeBook is null ? null
                    : await codeBook.ForProductsAsync(request.Lines.Select(l => l.ProductId).Distinct().ToList(), cancellationToken);   // MISS-11b
                return part.Build(new FiscalBuildInput(request, settings, till.Id, till.Name, number, previousHash, lineCodes,
                    original, till.DeviceSerial, till.Environment, referenceOldHash));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Result.Failure<FiscalBuiltDocument>(Error.Failure("Fiscal.Build.Failed", ex.Message));
            }
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
        IBusinessEventSink? businessEvents = null, IFiscalSecretStore? secrets = null, Fiscal.Application.Queries.FiscalWarningOptions? warnings = null)
    {
        public const int BatchSize = 500;   // MISS-11c-3: the ETA takes 1-500 receipts per submission

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

        /// <summary>The words a document held as Late carries (owner's decision: the shop files a late request on the portal).</summary>
        public const string LateReason = "More than 24 hours old: the tax authority takes it only after a late-submission request is approved on its portal. Then press \"Send late receipts\".";

        internal async Task<FiscalSendSummary> ExecuteAsync(bool force, CancellationToken cancellationToken = default, bool freshSignIn = false)
        {
            var settings = await setup.GetSettingsAsync(cancellationToken);
            if (settings is not { IsOn: true }) return FiscalSendSummary.NothingToDo;
            var part = (countryParts ?? []).LastOrDefault(p => p.Country == settings.Country);   // the last registered wins (a host may replace a part)
            if (part is null) return FiscalSendSummary.NothingToDo;

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            var connection = await FiscalConnections.ReadAsync(till, secrets, cancellationToken);   // MISS-11c-3: null = nothing can be sent yet
            var (sent, accepted, rejected, notSent) = (0, 0, 0, 0);
            string? lastReason = null;

            // 1. what waits goes out, in the order of the chain - MISS-11c-4: but what is past the 24 hours is held as Late (owner's decision)
            var due = (await documents.GetDueAsync(now, force, BatchSize, cancellationToken)).ToList();
            var limit = TimeSpan.FromHours((warnings ?? new Fiscal.Application.Queries.FiscalWarningOptions()).LateHours);
            var late = due.Where(d => d.IsOverdue(now, limit)).ToList();
            foreach (var document in late) document.MarkLate(LateReason, now);
            if (late.Count > 0)
            {
                due = due.Except(late).ToList();
                if (due.Count == 0) await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            if (due.Count > 0)
            {
                var outcomes = await AskAsync(part.SendAsync, due, settings, till, connection, cancellationToken, freshSignIn);
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

                if (sent + accepted + rejected > 0) till.MarkSent(now);   // MISS-11c-3: documents reached the authority - the environment is locked
                await unitOfWork.SaveChangesAsync(cancellationToken);   // what was handed over is kept even if checking fails
            }

            // 2. what was handed over earlier: the authority's answer
            var waiting = await documents.GetSentAsync(BatchSize, cancellationToken);
            if (waiting.Count > 0)
            {
                var outcomes = await AskAsync(part.CheckAsync, waiting, settings, till, connection, cancellationToken);
                foreach (var document in waiting)
                {
                    if (outcomes.GetValueOrDefault(document.Id) is not { } outcome) continue;
                    if (outcome.Result == FiscalSendResult.Accepted) { document.MarkAccepted(outcome.AuthorityReference, now); accepted++; }
                    else if (outcome.Result == FiscalSendResult.Rejected) { document.MarkRejected(outcome.Reason ?? string.Empty, now); rejected++; }
                }
            }

            if (waiting.Count > 0) await unitOfWork.SaveChangesAsync(cancellationToken);
            return new FiscalSendSummary(due.Count > 0 || waiting.Count > 0 || late.Count > 0, sent, accepted, rejected, notSent, lastReason, late.Count);
        }

        private static async Task<Dictionary<Guid, FiscalSendOutcome>> AskAsync(
            Func<FiscalSendInput, CancellationToken, Task<IReadOnlyList<FiscalSendOutcome>>> call, IReadOnlyList<FiscalDocument> batch,
            FiscalSettings settings, FiscalTill till, FiscalTillConnection? connection, CancellationToken cancellationToken, bool freshSignIn = false)
        {
            var input = new FiscalSendInput(batch.Select(d => new FiscalOutgoingDocument(d.Id, d.Kind, d.Reference, d.Number ?? 0, d.Hash ?? string.Empty,
                d.Payload ?? string.Empty, d.IssuedAt, d.AuthorityReference)).ToList(), settings, till.Id, till.Name, till.Registration, connection, freshSignIn);
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
                (options ?? new FiscalWarningOptions()).ManagerHours, missing?.MissingProducts ?? 0, missing?.MissingUnits ?? 0, missing?.MissingTaxRates ?? 0, counts.Late));
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

        /// <summary>MISS-11c-4: a document still unsent this long after its sale is held as Late (the authorities' 24 hours).</summary>
        public double LateHours { get; set; } = 24;
    }
}

namespace Fiscal.Application.Commands
{
    using Fiscal.Application.Abstractions;
    using Fiscal.Application.Queries;
    using Fiscal.Application.Repositories;
    using Fiscal.Application.Security;
    using Platform.Application.Abstractions.Auditing;

    /// <summary>
    /// MISS-11c-3: this till's registration with the authority (owner's choice: entered in a window). <see cref="ClientSecret"/> and
    /// <see cref="PresharedKey"/> null = keep the ones already kept.
    /// </summary>
    public sealed record SaveTillRegistrationCommand(FiscalEnvironment Environment, string? ClientId, string? ClientSecret, string? PosOsVersion,
        string? PosModelFramework, string? PresharedKey)
    {
        public override string ToString() => $"{Environment} {ClientId}";   // the secrets never reach a log line
    }

    /// <summary>
    /// MISS-11c-3: saves the registration (fiscal.manage, audited without the secrets). The secrets go to the protected store (DPAPI), never
    /// to the database; a computer that cannot protect them refuses. The environment is locked once documents reached the authority.
    /// </summary>
    public sealed class SaveTillRegistrationCommandHandler(IFiscalSetupRepository setup, IFiscalUnitOfWork unitOfWork, IAuthorizationService authorization,
        IFiscalSecretStore? secrets = null, ICurrentUser? currentUser = null, TimeProvider? timeProvider = null, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(SaveTillRegistrationCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;
            if (secrets is not { CanProtect: true })
                return Error.Conflict("Fiscal.Till.SecretsUnprotected", "This computer cannot keep the client secret protected, so the till cannot be registered here.");

            var kept = await secrets.ReadAsync(cancellationToken);
            var secret = string.IsNullOrWhiteSpace(command.ClientSecret) ? kept?.ClientSecret : command.ClientSecret.Trim();
            var key = string.IsNullOrWhiteSpace(command.PresharedKey) ? kept?.PresharedKey : command.PresharedKey.Trim();
            if (string.IsNullOrEmpty(secret) || secret.Length > 100)
                return Error.Validation("Fiscal.Till.SecretRequired", "Give the client secret from the tax authority's portal (up to 100 characters).");
            if (string.IsNullOrEmpty(key) || key.Length > 200)
                return Error.Validation("Fiscal.Till.KeyRequired", "Give the pre-shared key from the tax authority's portal (up to 200 characters).");

            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            var before = till.Environment;
            var registered = till.Register(command.Environment, command.ClientId, command.PosOsVersion, command.PosModelFramework,
                currentUser is { IsAuthenticated: true } me ? me.UserName : null, (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            if (registered.IsFailure) return registered;

            await secrets.WriteAsync(new FiscalTillSecrets(secret, key), cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            var changed = !string.IsNullOrWhiteSpace(command.ClientSecret) || !string.IsNullOrWhiteSpace(command.PresharedKey);
            await businessEvents.TryRecordAsync(BusinessEvent.Create(FiscalCapabilities.Module, "fiscal.till-registered", "fiscal-till", till.Id.ToString(),
                before == till.Environment
                    ? $"This till's registration with the tax authority was saved ({till.Environment})."
                    : $"This till's registration with the tax authority was saved; its environment changed from {before} to {till.Environment}.",
                $"clientId={till.ClientId};serial={till.DeviceSerial};secretsChanged={(changed ? "true" : "false")}"));
            return Result.Success();
        }
    }

    public sealed record TestFiscalSignInCommand;

    /// <summary>MISS-11c-3: "Test sign-in" (fiscal.manage, audited): asks the country's part to sign this till in; the result is kept on the till.</summary>
    public sealed class TestFiscalSignInCommandHandler(IFiscalSetupRepository setup, IFiscalUnitOfWork unitOfWork, IAuthorizationService authorization,
        IEnumerable<IFiscalCountryPart>? countryParts = null, IFiscalSecretStore? secrets = null, TimeProvider? timeProvider = null,
        IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result> HandleAsync(TestFiscalSignInCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var settings = await setup.GetSettingsAsync(cancellationToken);
            var part = settings is { IsOn: true } ? (countryParts ?? []).LastOrDefault(p => p.Country == settings.Country) : null;
            if (part is null) return Error.Conflict("Fiscal.SignIn.Off", "Electronic receipts are off, or this country is not supported yet.");
            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            if (await FiscalConnections.ReadAsync(till, secrets, cancellationToken) is not { } connection)
                return Error.Conflict("Fiscal.SignIn.NotRegistered", "Register this till first (its client ID, secret and pre-shared key from the tax authority's portal).");

            Result result;
            try
            {
                result = await part.TestSignInAsync(connection, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = Error.Failure("Fiscal.SignIn.Failed", ex.Message);
            }

            till.RecordSignIn(result.IsSuccess, (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            await businessEvents.TryRecordAsync(BusinessEvent.Create(FiscalCapabilities.Module, "fiscal.sign-in-tested", "fiscal-till", till.Id.ToString(),
                result.IsSuccess ? $"The tax authority accepted this till's sign-in ({till.Environment})." : $"The tax authority refused this till's sign-in: {result.Error.Description}"));
            return result;
        }
    }
}

namespace Fiscal.Application.Commands
{
    using Fiscal.Application.Abstractions;
    using Fiscal.Application.DTOs;
    using Fiscal.Application.Queries;
    using Fiscal.Application.Repositories;
    using Fiscal.Application.Security;
    using Platform.Application.Abstractions.Auditing;

    public sealed record SendLateFiscalDocumentsCommand;

    /// <summary>
    /// MISS-11c-4 (owner's decision): "Send late receipts" - after the authority approved the shop's late-submission request on its portal,
    /// the documents held as Late wait again (no longer held) and go out at once with a fresh sign-in (the approval reaches a new token).
    /// fiscal.manage, audited.
    /// </summary>
    public sealed class SendLateFiscalDocumentsCommandHandler(IFiscalDocumentRepository documents, IFiscalUnitOfWork unitOfWork, IAuthorizationService authorization,
        SendFiscalDocumentsCommandHandler sender, IBusinessEventSink? businessEvents = null)
    {
        public async Task<Result<FiscalSendSummary>> HandleAsync(SendLateFiscalDocumentsCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return Result.Failure<FiscalSendSummary>(allowed.Error);

            var late = await documents.GetLateAsync(cancellationToken);
            if (late.Count == 0) return Result.Failure<FiscalSendSummary>(Error.Conflict("Fiscal.Late.None", "No receipt is late."));
            foreach (var document in late) document.ReleaseLate();
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var summary = await sender.ExecuteAsync(force: true, cancellationToken, freshSignIn: true);
            await businessEvents.TryRecordAsync(BusinessEvent.Create(FiscalCapabilities.Module, "fiscal.late-sent", "fiscal-documents", "outbox",
                $"{late.Count} late electronic receipt(s) sent after the late-submission request: {summary.Sent} handed over, {summary.Accepted} accepted, {summary.Rejected} refused, {summary.NotSent} not sent."));
            return Result.Success(summary);
        }
    }

    public sealed record CorrectFiscalDocumentCommand(Guid DocumentId);

    /// <summary>
    /// MISS-11c-4 (owner's decisions): "Correct and resend" - a refused document is built again from what it was made from, with the current
    /// codes and shop details: a new hash at the same place in the chain with the same previous hash, naming the refused one (Egypt:
    /// referenceOldUUID); the refused one is kept as Replaced. "Build and send" - a document that could not be built is built now at the next
    /// place of the chain, with its sale's time. Both then wait to be sent. fiscal.manage, audited.
    /// </summary>
    public sealed class CorrectFiscalDocumentCommandHandler(IFiscalSetupRepository setup, IFiscalDocumentRepository documents, IFiscalUnitOfWork unitOfWork,
        IAuthorizationService authorization, IEnumerable<IFiscalCountryPart>? countryParts = null, TimeProvider? timeProvider = null,
        FiscalCodeBook? codeBook = null, IBusinessEventSink? businessEvents = null, Platform.Application.Abstractions.Data.IAtomicOperation? atomic = null)
    {
        public async Task<Result> HandleAsync(CorrectFiscalDocumentCommand command, CancellationToken cancellationToken = default)
        {
            var allowed = await authorization.AuthorizeAsync(FiscalCapabilities.Manage, cancellationToken);
            if (allowed.IsFailure) return allowed;

            var document = await documents.FindAsync(command.DocumentId, cancellationToken);
            if (document is not { State: FiscalDocumentState.Rejected or FiscalDocumentState.NotBuilt })
                return Error.Conflict("Fiscal.Correct.NotNeeded", "Only a refused document or one that could not be made can be built again.");
            var settings = await setup.GetSettingsAsync(cancellationToken);
            var part = settings is { IsOn: true } ? (countryParts ?? []).LastOrDefault(p => p.Country == settings.Country) : null;
            if (part is null || settings!.Country != document.Country)
                return Error.Conflict("Fiscal.Correct.Off", "Electronic receipts are off, or this country is not supported now.");
            if (FiscalBuilding.Deserialize(document.Request) is not { } request)
                return Error.Conflict("Fiscal.Correct.NoContent",
                    "This document was made before Matjar kept what is needed to build it again. Correct it on the tax authority's portal.");

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            var refused = document.State == FiscalDocumentState.Rejected;
            var built = refused
                ? await FiscalBuilding.BuildAsync(part, request, settings, till, document.Number!.Value, document.PreviousHash, documents, codeBook, document.Hash, cancellationToken)
                : await FiscalBuilding.BuildAsync(part, request, settings, till, till.Counter + 1, till.LastDocumentHash, documents, codeBook, null, cancellationToken);
            if (built.IsFailure) return Error.Validation("Fiscal.Correct.NotBuilt", built.Error.Description);
            if (string.IsNullOrWhiteSpace(built.Value.Hash)) return Error.Failure("Fiscal.Correct.NoHash", "The country part returned a document without its hash.");

            string summary;
            if (refused)
            {
                var copy = FiscalDocument.Correction(document, built.Value.Hash, built.Value.Payload, built.Value.QrPayload, now);
                async Task<Result<bool>> ReplaceAsync()
                {
                    document.MarkReplaced(copy.Id, now);
                    await unitOfWork.SaveChangesAsync(cancellationToken);   // the refused one leaves the unique indexes before its copy takes its place
                    await documents.AddAsync(copy, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    return Result.Success(true);
                }

                var replaced = atomic is null ? await ReplaceAsync() : await atomic.ExecuteAsync(ReplaceAsync, cancellationToken);   // both or neither
                if (replaced.IsFailure) return replaced;
                summary = $"The refused electronic receipt of {document.Reference} was corrected and waits to be sent again.";
            }
            else
            {
                var previous = till.LastDocumentHash;
                document.Rebuild(till.Chain(built.Value.Hash), previous, built.Value.Hash, built.Value.Payload, built.Value.QrPayload);
                summary = $"The electronic receipt of {document.Reference} that could not be made was built and waits to be sent.";
            }

            await unitOfWork.SaveChangesAsync(cancellationToken);
            await businessEvents.TryRecordAsync(BusinessEvent.Create(FiscalCapabilities.Module, refused ? "fiscal.corrected" : "fiscal.built-again",
                "fiscal-document", document.Id.ToString(), summary));
            return Result.Success();
        }
    }
}
