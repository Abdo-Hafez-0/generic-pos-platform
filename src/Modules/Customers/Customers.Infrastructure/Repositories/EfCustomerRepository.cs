using Customers.Application.Repositories;
using Customers.Domain.Entities;
using Customers.Domain.Enums;
using Customers.Domain.ValueObjects;
using Customers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Customers.Infrastructure.Repositories;

internal sealed class EfCustomerRepository(CustomersDbContext dbContext) : ICustomerRepository
{
    public async Task<Customer?> GetByIdAsync(CustomerId id, CancellationToken cancellationToken = default)
        => await dbContext.Customers
            .Include(c => c.Addresses)
            .Include(c => c.Contacts)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<Customer?> GetByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = code.Trim().ToUpperInvariant();
        return await dbContext.Customers.FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
    }

    public async Task AddAsync(Customer customer, CancellationToken cancellationToken = default)
        => await dbContext.Customers.AddAsync(customer, cancellationToken);

    public async Task<IReadOnlyList<Customer>> ListAsync(int skip, int take, CustomerStatus? status, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Customers.AsNoTracking();
        if (status is { } s) query = query.Where(c => c.Status == s);

        return await query.OrderBy(c => c.Name).ThenBy(c => c.Code).Skip(skip).Take(take).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Customer>> SearchAsync(string text, int take, CancellationToken cancellationToken = default)
    {
        var pattern = $"%{Escape(text)}%";
        return await dbContext.Customers.AsNoTracking()
            .Where(c => EF.Functions.Like(c.Code, pattern, "\\")
                     || EF.Functions.Like(c.Name, pattern, "\\")
                     || (c.Email != null && EF.Functions.Like(c.Email, pattern, "\\"))
                     || (c.Phone != null && EF.Functions.Like(c.Phone, pattern, "\\")))
            .OrderBy(c => c.Name)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> CountAsync(CustomerStatus? status, CancellationToken cancellationToken = default)
    {
        var query = dbContext.Customers.AsQueryable();
        if (status is { } s) query = query.Where(c => c.Status == s);
        return await query.CountAsync(cancellationToken);
    }

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
