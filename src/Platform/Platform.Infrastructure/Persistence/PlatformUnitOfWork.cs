using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.Application.Abstractions.Data;

namespace Platform.Infrastructure.Persistence;

/// <summary>
/// Platform-level Unit of Work implementation backed by EF Core.
///
/// This implementation wraps <see cref="PlatformDbContext"/> and provides:
/// - Atomic persistence of platform-level changes.
/// - Transaction management (begin, commit, rollback).
/// - Async operation.
///
/// IMPORTANT SCOPE:
/// This UoW is for PLATFORM-LEVEL operations only.
/// Business modules will have their own IUnitOfWork implementations
/// that wrap their own DbContexts (Stage 5+).
///
/// Business modules must NOT inject this class directly.
/// They must inject the IUnitOfWork registered by their own module infrastructure.
///
/// The Application layer's IUnitOfWork abstraction is defined in Platform.Application.
/// This is the EF Core implementation of that abstraction for the platform context.
/// </summary>
internal sealed class PlatformUnitOfWork : IUnitOfWork, IDisposable, IAsyncDisposable
{
    private readonly PlatformDbContext _context;
    private IDbContextTransaction? _currentTransaction;

    public PlatformUnitOfWork(PlatformDbContext context)
    {
        _context = context;
    }

    /// <inheritdoc/>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return await _context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Begins a database transaction.
    /// The caller is responsible for committing or rolling back.
    /// </summary>
    public async Task<IDbContextTransaction> BeginTransactionAsync(
        CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A transaction is already in progress. " +
                "Nested transactions are not supported. " +
                "Commit or roll back the current transaction before starting a new one.");
        }

        _currentTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        return _currentTransaction;
    }

    /// <summary>
    /// Commits the current transaction and resets the transaction reference.
    /// </summary>
    public async Task CommitTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is null)
        {
            throw new InvalidOperationException(
                "No active transaction to commit. " +
                "Call BeginTransactionAsync before CommitTransactionAsync.");
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            await _currentTransaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await RollbackTransactionAsync(cancellationToken);
            throw;
        }
        finally
        {
            await DisposeTransactionAsync();
        }
    }

    /// <summary>
    /// Rolls back the current transaction.
    /// </summary>
    public async Task RollbackTransactionAsync(CancellationToken cancellationToken = default)
    {
        if (_currentTransaction is null)
            return;

        try
        {
            await _currentTransaction.RollbackAsync(cancellationToken);
        }
        finally
        {
            await DisposeTransactionAsync();
        }
    }

    private async Task DisposeTransactionAsync()
    {
        if (_currentTransaction is not null)
        {
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeTransactionAsync();
        await _context.DisposeAsync();
    }

    public void Dispose()
    {
        // Synchronous dispose delegates to async implementation.
        // This is safe here because scope disposal happens on normal threads,
        // not inside an active async context (no deadlock risk).
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
