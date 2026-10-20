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
