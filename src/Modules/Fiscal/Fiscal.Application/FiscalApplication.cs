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
    }

    /// <summary>MISS-11a-2: what a country part builds from: the request, the seller, and the document's place in this till's chain.</summary>
    public sealed record FiscalBuildInput(Fiscal.Contracts.Models.FiscalDocumentRequest Request, FiscalSettings Settings, Guid TillId, string TillName,
        long Number, string? PreviousHash);

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
    }

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
    /// <summary>
    /// MISS-11a: the Fiscal screen's setup - the settings, this till, and whether the chosen country's part is installed (without it nothing
    /// is issued yet) and whether the country is locked (documents exist).
    /// </summary>
    public sealed record FiscalSetupDto(
        FiscalCountry Country, string? TaxNumber, string? BranchCode, string? ActivityCode, string? SellerNameArabic, string? AddressArabic,
        DateTime? UpdatedAt, string? UpdatedBy,
        string TillName, FiscalTillRegistration TillRegistration, long NextNumber, bool CountryLocked,
        IReadOnlyList<FiscalCountry> InstalledCountryParts)
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
                settings.SellerNameArabic, settings.AddressArabic, saved ? settings.UpdatedAt : null, settings.UpdatedBy,
                till.Name, till.Registration, till.Counter + 1, till.HasIssued,
                (countryParts ?? []).Select(p => p.Country).Distinct().ToList()));
        }
    }

    /// <summary>This till's record, created the first time it is needed (named after the computer).</summary>
    internal static class FiscalTillAccess
    {
        public static async Task<FiscalTill> GetOrCreateAsync(IFiscalSetupRepository repository, IFiscalUnitOfWork unitOfWork, TimeProvider? timeProvider,
            CancellationToken cancellationToken)
        {
            if (await repository.GetTillAsync(cancellationToken) is { } till) return till;

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
    using Fiscal.Application.Queries;
    using Fiscal.Application.Repositories;
    using Fiscal.Application.Security;
    using Platform.Application.Abstractions.Auditing;

    /// <summary>MISS-11a: the country and the shop's tax details.</summary>
    public sealed record SaveFiscalSettingsCommand(FiscalCountry Country, string? TaxNumber, string? BranchCode, string? ActivityCode,
        string? SellerNameArabic, string? AddressArabic);

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
                command.AddressArabic, till.HasIssued, currentUser is { IsAuthenticated: true } me ? me.UserName : null,
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
        IEnumerable<IFiscalCountryPart>? countryParts = null, TimeProvider? timeProvider = null)
    {
        internal async Task<FiscalDocumentResult> ExecuteAsync(FiscalDocumentRequest request, CancellationToken cancellationToken = default)
        {
            var settings = await setup.GetSettingsAsync(cancellationToken);
            if (settings is not { IsOn: true }) return FiscalDocumentResult.NothingToIssue();
            var part = (countryParts ?? []).FirstOrDefault(p => p.Country == settings.Country);
            if (part is null) return FiscalDocumentResult.NothingToIssue();

            var kind = (FiscalDocumentKind)(int)request.Kind;
            if (await documents.FindBySourceAsync(kind, request.SourceId, cancellationToken) is { } existing)
                return existing.State == FiscalDocumentState.NotBuilt
                    ? FiscalDocumentResult.NotBuilt(existing.Id, existing.StateReason ?? string.Empty)
                    : FiscalDocumentResult.Issued(existing.Id, existing.Number!.Value, existing.QrPayload);

            var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            var till = await FiscalTillAccess.GetOrCreateAsync(setup, unitOfWork, timeProvider, cancellationToken);
            var issuedAt = DateTime.SpecifyKind(request.IssuedAtUtc, DateTimeKind.Utc);

            Result<FiscalBuiltDocument> built;
            try
            {
                built = part.Build(new FiscalBuildInput(request, settings, till.Id, till.Name, till.Counter + 1, till.LastDocumentHash));
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
}
