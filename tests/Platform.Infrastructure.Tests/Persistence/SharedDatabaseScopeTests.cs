using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Data;
using Platform.Core.Results;
using Platform.Infrastructure.Persistence;

namespace Platform.Infrastructure.Tests.Persistence;

/// <summary>
/// The shared per-scope SQLite transaction in isolation: two unrelated contexts (like two modules) on one file, one operation across both.
/// Real SQLite, a throw-away file per test.
/// </summary>
[Trait("Category", "Failure")]
public sealed class SharedDatabaseScopeTests : IDisposable
{
    private sealed class Apple { public int Id { get; set; } public string Name { get; set; } = ""; }
    private sealed class Pear { public int Id { get; set; } public string Name { get; set; } = ""; }

    private sealed class AppleContext(DbContextOptions<AppleContext> options) : DbContext(options)
    {
        public DbSet<Apple> Apples => Set<Apple>();
        protected override void OnModelCreating(ModelBuilder b) => b.Entity<Apple>().ToTable("apples").HasKey(a => a.Id);
    }

    private sealed class PearContext(DbContextOptions<PearContext> options) : DbContext(options)
    {
        public DbSet<Pear> Pears => Set<Pear>();
        protected override void OnModelCreating(ModelBuilder b) => b.Entity<Pear>().ToTable("pears").HasKey(p => p.Id);
    }

    private readonly string _file = Path.Combine(Path.GetTempPath(), "shared-scope-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly ServiceProvider _provider;

    public SharedDatabaseScopeTests()
    {
        var connectionString = $"Data Source={_file}";
        var services = new ServiceCollection();
        services.AddAtomicOperations();
        services.AddDbContext<AppleContext>((sp, o) => o.UseSharedSqlite(sp, connectionString));
        services.AddDbContext<PearContext>((sp, o) => o.UseSharedSqlite(sp, connectionString));
        _provider = services.BuildServiceProvider();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE apples (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL); CREATE TABLE pears (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL);";
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_file); } catch (IOException) { }
    }

    private long Count(string table)
    {
        using var connection = new SqliteConnection($"Data Source={_file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void SeedPear(int id)
    {
        using var connection = new SqliteConnection($"Data Source={_file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO pears (Id, Name) VALUES ({id}, 'seeded')";
        command.ExecuteNonQuery();
    }

    private static async Task<Result<int>> WriteBothAsync(AppleContext apples, PearContext pears)
    {
        apples.Apples.Add(new Apple { Id = 1, Name = "a" });
        await apples.SaveChangesAsync();
        pears.Pears.Add(new Pear { Id = 1, Name = "p" });
        await pears.SaveChangesAsync();
        return Result.Success(2);
    }

    [Fact]
    public async Task ASuccessfulOperation_CommitsEveryContextTogether()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var result = await sp.GetRequiredService<IAtomicOperation>().ExecuteAsync(
            () => WriteBothAsync(sp.GetRequiredService<AppleContext>(), sp.GetRequiredService<PearContext>()));

        Assert.True(result.IsSuccess);
        Assert.Equal((1L, 1L), (Count("apples"), Count("pears")));
    }

    [Fact]
    public async Task AFailedResult_RollsBackEveryContext_AndTheContextsMatchTheDatabaseAgain()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var apples = sp.GetRequiredService<AppleContext>();
        var pears = sp.GetRequiredService<PearContext>();

        var result = await sp.GetRequiredService<IAtomicOperation>().ExecuteAsync(async () =>
        {
            await WriteBothAsync(apples, pears);
            return Result.Failure<int>(Error.Conflict("Test.Refused", "refused after both writes"));
        });

        Assert.True(result.IsFailure);
        Assert.Equal((0L, 0L), (Count("apples"), Count("pears")));
        Assert.Empty(apples.ChangeTracker.Entries());   // what the contexts "saved" no longer exists: they do not pretend otherwise
        Assert.Empty(pears.ChangeTracker.Entries());
        Assert.Equal(0, await apples.Apples.CountAsync());
    }

    [Fact]
    public async Task AnException_RollsBackEveryContext_AndIsRethrown()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var apples = sp.GetRequiredService<AppleContext>();
        var pears = sp.GetRequiredService<PearContext>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sp.GetRequiredService<IAtomicOperation>().ExecuteAsync<int>(async () =>
        {
            await WriteBothAsync(apples, pears);
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal((0L, 0L), (Count("apples"), Count("pears")));
        Assert.False(sp.GetRequiredService<IAtomicOperation>().IsActive);
    }

    [Fact]
    public async Task ADatabaseErrorInTheLastWrite_UndoesTheEarlierWritesOfOtherContexts()
    {
        SeedPear(7);
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var apples = sp.GetRequiredService<AppleContext>();
        var pears = sp.GetRequiredService<PearContext>();

        await Assert.ThrowsAsync<DbUpdateException>(() => sp.GetRequiredService<IAtomicOperation>().ExecuteAsync(async () =>
        {
            apples.Apples.Add(new Apple { Id = 1, Name = "a" });
            await apples.SaveChangesAsync();
            pears.Pears.Add(new Pear { Id = 7, Name = "duplicate key" });   // the database refuses this write
            await pears.SaveChangesAsync();
            return Result.Success(1);
        }));

        Assert.Equal((0L, 1L), (Count("apples"), Count("pears")));   // only the seeded pear remains
    }

    [Fact]
    public async Task ANestedOperation_JoinsTheRunningOne_AndTheOuterFailureUndoesIt()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var atomic = sp.GetRequiredService<IAtomicOperation>();
        var apples = sp.GetRequiredService<AppleContext>();
        var pears = sp.GetRequiredService<PearContext>();

        var result = await atomic.ExecuteAsync(async () =>
        {
            var inner = await atomic.ExecuteAsync(() => WriteBothAsync(apples, pears));   // would commit on its own if it did not join
            Assert.True(inner.IsSuccess);
            Assert.True(atomic.IsActive);
            return Result.Failure<int>(Error.Failure("Test.Late", "the outer operation fails afterwards"));
        });

        Assert.True(result.IsFailure);
        Assert.Equal((0L, 0L), (Count("apples"), Count("pears")));
    }

    [Fact]
    public async Task AConnectionLostBeforeCommit_LeavesNothing_LikeAKilledProcess()
    {
        var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var shared = sp.GetRequiredService<SharedDatabaseScope>();
        var apples = sp.GetRequiredService<AppleContext>();
        var pears = sp.GetRequiredService<PearContext>();

        await Assert.ThrowsAnyAsync<Exception>(() => shared.ExecuteAsync<int>(async () =>
        {
            await WriteBothAsync(apples, pears);
            shared.Dispose();                                   // the connection vanishes with the transaction open: no COMMIT, no ROLLBACK
            throw new IOException("process killed");
        }));
        scope.Dispose();

        Assert.Equal((0L, 0L), (Count("apples"), Count("pears")));
    }

    [Fact]
    public async Task OutsideAnOperation_ASaveIsStillAllOrNothing()
    {
        SeedPear(7);
        using var scope = _provider.CreateScope();
        var pears = scope.ServiceProvider.GetRequiredService<PearContext>();
        pears.Pears.AddRange(new Pear { Id = 1, Name = "ok" }, new Pear { Id = 7, Name = "duplicate key" });

        await Assert.ThrowsAsync<DbUpdateException>(() => pears.SaveChangesAsync());

        Assert.Equal(1L, Count("pears"));   // the first row was undone too: EF's own per-save transaction applies when no operation runs
        Assert.Empty(pears.ChangeTracker.Entries());   // and the failed entities are forgotten
    }

    [Fact]
    public async Task AnOperationThatLosesTheWriteLockRace_FailsWithoutChangingAnything()
    {
        using var other = new SqliteConnection($"Data Source={_file};Pooling=False;Default Timeout=1");
        other.Open();
        using (var lockCommand = other.CreateCommand())
        {
            lockCommand.CommandText = "BEGIN IMMEDIATE";
            lockCommand.ExecuteNonQuery();
        }

        // the scope's own connection uses the default timeout; a short one keeps the test quick
        var fast = new ServiceCollection();
        fast.AddAtomicOperations();
        fast.AddDbContext<AppleContext>((p, o) => o.UseSharedSqlite(p, $"Data Source={_file};Default Timeout=1"));
        await using var provider = fast.BuildServiceProvider();
        using var fastScope = provider.CreateScope();
        var apples = fastScope.ServiceProvider.GetRequiredService<AppleContext>();

        await Assert.ThrowsAnyAsync<SqliteException>(() => fastScope.ServiceProvider.GetRequiredService<IAtomicOperation>().ExecuteAsync(async () =>
        {
            apples.Apples.Add(new Apple { Id = 1, Name = "a" });
            await apples.SaveChangesAsync();
            return Result.Success(1);
        }));

        using (var release = other.CreateCommand())
        {
            release.CommandText = "ROLLBACK";
            release.ExecuteNonQuery();
        }

        Assert.Equal(0L, Count("apples"));
    }
}
