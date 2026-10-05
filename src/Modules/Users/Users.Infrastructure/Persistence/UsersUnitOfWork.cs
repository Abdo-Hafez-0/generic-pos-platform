using Users.Application.Abstractions;

namespace Users.Infrastructure.Persistence;

internal sealed class UsersUnitOfWork(UsersDbContext dbContext) : IUsersUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => dbContext.SaveChangesAsync(cancellationToken);
}
