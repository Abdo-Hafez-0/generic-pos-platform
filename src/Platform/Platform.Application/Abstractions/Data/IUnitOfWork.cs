namespace Platform.Application.Abstractions.Data;

/// <summary>
/// Abstraction for a unit of work that groups related data operations into a single atomic transaction.
/// The implementation lives in Platform.Infrastructure and uses SQLite transactions.
/// No persistence technology details are visible at this layer.
/// </summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Commits all changes made within the current unit of work.
    /// Returns the number of state entries written to the database.
    /// </summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
