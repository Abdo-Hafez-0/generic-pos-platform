using Pricing.Application.Abstractions;

namespace Pricing.Infrastructure.Persistence;

internal sealed class PricingUnitOfWork(PricingDbContext dbContext) : IPricingUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
