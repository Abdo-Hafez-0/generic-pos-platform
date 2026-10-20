using Fiscal.Application.Repositories;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
using Fiscal.Domain.Enums;
using Fiscal.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Fiscal.Infrastructure.Repositories;

internal sealed class EfFiscalSetupRepository(FiscalDbContext dbContext) : IFiscalSetupRepository
{
    public Task<FiscalSettings?> GetSettingsAsync(CancellationToken cancellationToken = default)
        => dbContext.Settings.FirstOrDefaultAsync(s => s.Id == FiscalSettings.SingletonId, cancellationToken);

    public async Task AddSettingsAsync(FiscalSettings settings, CancellationToken cancellationToken = default)
        => await dbContext.Settings.AddAsync(settings, cancellationToken);

    // one database is one till: the oldest record is this till
    public Task<FiscalTill?> GetTillAsync(CancellationToken cancellationToken = default)
        => dbContext.Tills.OrderBy(t => t.CreatedAt).FirstOrDefaultAsync(cancellationToken);

    public async Task AddTillAsync(FiscalTill till, CancellationToken cancellationToken = default)
        => await dbContext.Tills.AddAsync(till, cancellationToken);
}

/// <summary>Implements Fiscal.Contracts' setup reader (no permission: other modules only learn whether receipts are on, and for which country).</summary>
internal sealed class FiscalSetupReader(FiscalDbContext dbContext) : IFiscalSetupReader
{
    public async Task<FiscalCountryContract> GetCountryAsync(CancellationToken cancellationToken = default)
    {
        var settings = await dbContext.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == FiscalSettings.SingletonId, cancellationToken);
        return settings is null ? FiscalCountryContract.None : (FiscalCountryContract)(int)settings.Country;
    }
}

internal sealed class EfFiscalDocumentRepository(FiscalDbContext dbContext) : IFiscalDocumentRepository
{
    public Task<FiscalDocument?> FindBySourceAsync(Fiscal.Domain.Enums.FiscalDocumentKind kind, Guid sourceId, CancellationToken cancellationToken = default)
        => dbContext.Documents.FirstOrDefaultAsync(d => d.Kind == kind && d.SourceId == sourceId, cancellationToken);

    public async Task AddAsync(FiscalDocument document, CancellationToken cancellationToken = default)
        => await dbContext.Documents.AddAsync(document, cancellationToken);

    public async Task<IReadOnlyList<FiscalDocument>> GetDueAsync(DateTime nowUtc, bool force, int take, CancellationToken cancellationToken = default)
        => await dbContext.Documents
            .Where(d => d.State == FiscalDocumentState.Waiting && (force || d.NextAttemptAt == null || d.NextAttemptAt <= nowUtc))
            .OrderBy(d => d.Number).Take(take).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FiscalDocument>> GetSentAsync(int take, CancellationToken cancellationToken = default)
        => await dbContext.Documents.Where(d => d.State == FiscalDocumentState.Sent).OrderBy(d => d.Number).Take(take).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FiscalDocument>> ListAsync(IReadOnlyCollection<FiscalDocumentState>? states, int take, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Documents.AsNoTracking();
        if (states is { Count: > 0 }) query = query.Where(d => states.Contains(d.State));
        return await query.OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Number).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<FiscalDocumentCounts> CountAsync(DateTime managerSinceUtc, DateTime tillSinceUtc, CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.Documents.AsNoTracking()
            .Where(d => d.State == FiscalDocumentState.Waiting || d.State == FiscalDocumentState.Sent
                        || d.State == FiscalDocumentState.NotBuilt || d.State == FiscalDocumentState.Rejected)
            .Select(d => new { d.State, d.CreatedAt })
            .ToListAsync(cancellationToken);
        var outstanding = rows.Where(r => r.State is FiscalDocumentState.Waiting or FiscalDocumentState.Sent).ToList();
        return new FiscalDocumentCounts(outstanding.Count, rows.Count(r => r.State == FiscalDocumentState.NotBuilt), rows.Count(r => r.State == FiscalDocumentState.Rejected),
            outstanding.Count(r => r.CreatedAt < managerSinceUtc), outstanding.Count(r => r.CreatedAt < tillSinceUtc),
            outstanding.Count == 0 ? null : outstanding.Min(r => r.CreatedAt));
    }
}

/// <summary>
/// MISS-11a-2: implements Fiscal.Contracts' recorder for POS's checkout and refund. An unexpected failure (disk, lock) becomes a failed
/// result, so the caller rolls its transaction back: a sale is never saved without its fiscal document.
/// </summary>
internal sealed class FiscalRecorder(Fiscal.Application.Commands.RecordFiscalDocumentCommandHandler handler,
    Microsoft.Extensions.Logging.ILogger<FiscalRecorder>? logger = null) : IFiscalRecorder
{
    public async Task<FiscalDocumentResult> RecordAsync(FiscalDocumentRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return await handler.ExecuteAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (logger is not null)
                Microsoft.Extensions.Logging.LoggerExtensions.LogError(logger, ex, "The fiscal document of {Kind} {SourceId} could not be saved.", request.Kind, request.SourceId);
            return FiscalDocumentResult.Failure("Fiscal.Document.NotSaved", "The electronic receipt could not be saved.");
        }
    }
}

/// <summary>MISS-11a-3: implements the till's notice from the alert counts.</summary>
internal sealed class FiscalNotices(Fiscal.Application.Queries.FiscalAlertsReader alerts, Fiscal.Application.Queries.FiscalWarningOptions? options = null) : IFiscalNotices
{
    public async Task<FiscalTillNotice?> GetTillNoticeAsync(CancellationToken cancellationToken = default)
        => await alerts.ReadAsync(cancellationToken) is { OverTillLimit: > 0, OldestOutstandingAt: { } oldest } counts
            ? new FiscalTillNotice(counts.OverTillLimit, DateTime.SpecifyKind(oldest, DateTimeKind.Utc), (options ?? new()).TillHours)
            : null;
}

/// <summary>MISS-11b: the authority's codes for products, units and tax rates.</summary>
internal sealed class EfFiscalCodeRepository(FiscalDbContext dbContext) : IFiscalCodeRepository
{
    public async Task<IReadOnlyList<FiscalProductCode>> GetProductCodesAsync(IReadOnlyCollection<Guid>? productIds = null, CancellationToken cancellationToken = default)
        => productIds is null
            ? await dbContext.ProductCodes.ToListAsync(cancellationToken)
            : await dbContext.ProductCodes.Where(c => productIds.Contains(c.ProductId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FiscalUnitCode>> GetUnitCodesAsync(CancellationToken cancellationToken = default)
        => await dbContext.UnitCodes.ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<FiscalTaxCode>> GetTaxCodesAsync(CancellationToken cancellationToken = default)
        => await dbContext.TaxCodes.ToListAsync(cancellationToken);

    public async Task AddAsync(FiscalProductCode code, CancellationToken cancellationToken = default) => await dbContext.ProductCodes.AddAsync(code, cancellationToken);
    public async Task AddAsync(FiscalUnitCode code, CancellationToken cancellationToken = default) => await dbContext.UnitCodes.AddAsync(code, cancellationToken);
    public async Task AddAsync(FiscalTaxCode code, CancellationToken cancellationToken = default) => await dbContext.TaxCodes.AddAsync(code, cancellationToken);
    public void Remove(FiscalProductCode code) => dbContext.ProductCodes.Remove(code);
    public void Remove(FiscalUnitCode code) => dbContext.UnitCodes.Remove(code);
}
