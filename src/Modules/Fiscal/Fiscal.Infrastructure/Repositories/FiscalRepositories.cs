using Fiscal.Application.Repositories;
using Fiscal.Contracts.Interfaces;
using Fiscal.Contracts.Models;
using Fiscal.Domain.Entities;
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
