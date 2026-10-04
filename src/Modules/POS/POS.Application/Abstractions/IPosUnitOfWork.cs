namespace POS.Application.Abstractions;

/// <summary>
/// Saves changes to the POS module's own persistence (POSDbContext).
/// POS never commits other modules' data — they have their own units of work behind their Contracts.
/// </summary>
public interface IPosUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
