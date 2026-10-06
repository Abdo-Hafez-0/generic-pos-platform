using Platform.Core.Results;

namespace Platform.Application.Abstractions.Data;

/// <summary>
/// Runs a multi-step business operation as ONE database transaction, even when the steps are performed by different modules
/// (each with its own DbContext) through their Contracts.
///
/// The work either succeeds - and everything every module wrote is committed together - or it fails and NOTHING is kept:
/// a failed Result and an exception both roll the whole operation back. Rolling back is done by the database, never by
/// compensating writes. A call made while an operation is already running simply joins it.
///
/// Only the operation's own database work belongs inside: hardware, network and other external effects must happen AFTER the
/// operation has committed, because they cannot be rolled back.
/// </summary>
public interface IAtomicOperation
{
    /// <summary>True while an operation is running (the next call joins it).</summary>
    bool IsActive { get; }

    /// <summary>Runs <paramref name="work"/> in one transaction; commits only when it returns a successful Result.</summary>
    Task<Result<T>> ExecuteAsync<T>(Func<Task<Result<T>>> work, CancellationToken cancellationToken = default);
}
