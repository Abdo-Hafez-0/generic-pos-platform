using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.Application.Abstractions.Data;
using Platform.Core.Results;

namespace Platform.Infrastructure.Persistence;

/// <summary>
/// The business database connection(s) of ONE dependency-injection scope, shared by every module DbContext of that scope, plus the
/// single SQLite transaction an <see cref="IAtomicOperation"/> opens on it.
///
/// Why: modules own separate DbContexts (and may never reference each other), but one business operation (a sale, a purchase
/// receipt) touches several of them. Sharing the connection lets all of them take part in ONE real transaction - rollback is the
/// database's, not a chain of compensating deletes.
///
/// While no operation is running nothing changes: Entity Framework opens and closes the connection around each call as before.
/// The transaction is started with plain SQL (BEGIN IMMEDIATE) so contexts need no knowledge of it; while it is active the
/// <see cref="AtomicSaveChangesInterceptor"/> stops Entity Framework from starting its own (nested) transaction.
/// </summary>
public sealed class SharedDatabaseScope : IAtomicOperation, IDisposable
{
    private readonly Dictionary<string, SqliteConnection> _connections = new(StringComparer.Ordinal);

    public bool IsActive { get; private set; }

    internal SqliteConnection GetConnection(string connectionString)
    {
        if (!_connections.TryGetValue(connectionString, out var connection))
        {
            connection = new SqliteConnection(connectionString);
            _connections[connectionString] = connection;
            if (IsActive)
            {
                connection.Open();
                Execute(connection, "BEGIN IMMEDIATE");
            }
        }

        return connection;
    }

    public async Task<Result<T>> ExecuteAsync<T>(Func<Task<Result<T>>> work, CancellationToken cancellationToken = default)
    {
        if (IsActive) return await work();   // joins the running operation

        var begun = new List<SqliteConnection>();
        IsActive = true;
        try
        {
            foreach (var connection in _connections.Values)
            {
                if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync(cancellationToken);
                await ExecuteAsync(connection, "BEGIN IMMEDIATE", cancellationToken);
                begun.Add(connection);
            }

            var result = await work();
            if (result.IsFailure)
            {
                await EndAsync(begun, "ROLLBACK");
                return result;
            }

            // Late connections (created while the work ran) took part too; they are part of what must commit.
            foreach (var connection in _connections.Values.Where(c => !begun.Contains(c))) begun.Add(connection);
            await EndAsync(begun, "COMMIT");
            return result;
        }
        catch
        {
            await EndAsync(begun.Concat(_connections.Values).Distinct().ToList(), "ROLLBACK", swallow: true);
            throw;
        }
        finally
        {
            IsActive = false;
            foreach (var connection in _connections.Values)
                if (connection.State == System.Data.ConnectionState.Open) await connection.CloseAsync();
        }
    }

    private static async Task EndAsync(IEnumerable<SqliteConnection> connections, string statement, bool swallow = false)
    {
        foreach (var connection in connections)
        {
            if (connection.State != System.Data.ConnectionState.Open) continue;
            try { await ExecuteAsync(connection, statement, CancellationToken.None); }
            catch when (swallow) { /* the original failure is what matters; SQLite rolls back when the connection closes */ }
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Values) connection.Dispose();   // an unfinished transaction is rolled back by SQLite
        _connections.Clear();
    }
}

/// <summary>
/// While an atomic operation runs, SaveChanges must not start its own transaction (SQLite has no nested transactions): the
/// operation's transaction already covers every save. Outside an operation Entity Framework's normal per-save transaction applies.
/// </summary>
public sealed class AtomicSaveChangesInterceptor : SaveChangesInterceptor
{
    public static readonly AtomicSaveChangesInterceptor Instance = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Adjust(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Adjust(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Adjust(DbContext? context)
    {
        if (context is null) return;
        var services = context.GetService<IDbContextOptions>().FindExtension<Microsoft.EntityFrameworkCore.Infrastructure.CoreOptionsExtension>()?.ApplicationServiceProvider;
        var active = services?.GetService<SharedDatabaseScope>()?.IsActive == true;
        context.Database.AutoTransactionBehavior = active ? AutoTransactionBehavior.Never : AutoTransactionBehavior.WhenNeeded;
    }
}

public static class SharedDatabaseExtensions
{
    /// <summary>Registers the per-scope shared connection and <see cref="IAtomicOperation"/>. Safe to call from every module.</summary>
    public static IServiceCollection AddAtomicOperations(this IServiceCollection services)
    {
        services.TryAddScoped<SharedDatabaseScope>();
        services.TryAddScoped<IAtomicOperation>(sp => sp.GetRequiredService<SharedDatabaseScope>());
        return services;
    }

    /// <summary>
    /// Like UseSqlite, but the context uses the connection shared by its scope, so it can take part in an <see cref="IAtomicOperation"/>.
    /// Without the shared scope registered it behaves exactly like UseSqlite.
    /// </summary>
    public static DbContextOptionsBuilder UseSharedSqlite(
        this DbContextOptionsBuilder options, IServiceProvider services, string connectionString, Action<Microsoft.EntityFrameworkCore.Infrastructure.SqliteDbContextOptionsBuilder>? configure = null)
    {
        var scope = services.GetService<SharedDatabaseScope>();
        if (scope is null) return options.UseSqlite(connectionString, configure);

        return options.UseSqlite(scope.GetConnection(connectionString), configure).AddInterceptors(AtomicSaveChangesInterceptor.Instance);
    }
}
