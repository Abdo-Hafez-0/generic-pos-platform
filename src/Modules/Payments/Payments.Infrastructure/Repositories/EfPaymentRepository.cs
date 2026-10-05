using Microsoft.EntityFrameworkCore;
using Payments.Application.Repositories;
using Payments.Domain.Entities;
using Payments.Domain.ValueObjects;
using Payments.Infrastructure.Persistence;

namespace Payments.Infrastructure.Repositories;

internal sealed class EfPaymentRepository(PaymentsDbContext dbContext) : IPaymentRepository
{
    public async Task<Payment?> GetByIdAsync(PaymentId id, CancellationToken cancellationToken = default)
        => await dbContext.Payments.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

    public async Task AddAsync(Payment payment, CancellationToken cancellationToken = default)
        => await dbContext.Payments.AddAsync(payment, cancellationToken);

    public async Task<IReadOnlyList<Payment>> ListForReferenceAsync(string referenceType, Guid referenceId, CancellationToken cancellationToken = default)
    {
        var type = referenceType.Trim().ToLowerInvariant();
        return await dbContext.Payments.AsNoTracking()
            .Where(p => p.ReferenceType == type && p.ReferenceId == referenceId)
            .ToListAsync(cancellationToken);
    }
}
