using System.Security.Cryptography;
using Catalog.Application.Commands;
using Client.Host.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Data;
using Platform.Core.Results;
using Platform.Infrastructure.Persistence;
using POS.Contracts.Interfaces;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 12 - the SQLite database failing: locked, unavailable, corrupt, a migration that cannot apply, a constraint, a failure at commit time.
/// The real host and the real file are used; the database is inspected directly afterwards. A failed operation changes nothing, the application
/// says so in plain words (no database text), and a corrupt or half-migrated file is never overwritten.
/// </summary>
[Collection(HostCollection.Name)]
[Trait("Category", "Failure")]
public sealed class DatabaseFailureTests
{
    private const string BusyKey = "GENERICPOS_Database__BusyTimeoutSeconds";

    /// <summary>Makes a locked database give up after 1 second instead of waiting 30, for the duration of a test.</summary>
    private sealed class NoLockWait : IDisposable
    {
        public NoLockWait() => Environment.SetEnvironmentVariable(BusyKey, "1");
        public void Dispose() => Environment.SetEnvironmentVariable(BusyKey, null);
    }

    private static async Task<SqliteConnection> LockAsync(IntegrationHost host, string beginStatement)
    {
        var connection = await host.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = beginStatement;
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    // ------------------------------------------------------------------ locked

    [Fact]
    public async Task ASaleFailsSafely_WhileAnotherConnectionHoldsTheWriteLock_AndSucceedsOnceItIsReleased()
    {
        using var _ = new NoLockWait();
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 1m);
        var before = await BusinessState.ReadAsync(host);

        await using (var locker = await LockAsync(host, "BEGIN IMMEDIATE"))
        {
            var failed = await CheckoutAsync(host.Services, cartId);

            Assert.False(failed.IsSuccess);
            Assert.Equal("POS.Checkout.NotSaved", failed.ErrorCode);
            Assert.DoesNotContain("locked", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);   // no database wording for the cashier
            Assert.DoesNotContain("SQLite", failed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            // a reader is still served while only the write lock is held, and sees nothing changed
            using var scope = host.Services.CreateScope();
            var level = await scope.ServiceProvider.GetRequiredService<Inventory.Contracts.Interfaces.IInventoryReader>().GetStockLevelByProductAsync(shop.ProductId, shop.WarehouseId);
            Assert.Equal(10m, level!.OnHand);
        }   // the lock is released here

        Assert.Equal(before, await BusinessState.ReadAsync(host));
        var retried = await CheckoutAsync(host.Services, cartId);
        Assert.True(retried.IsSuccess, retried.ErrorMessage);
        Assert.Equal(9m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(before.Sales + 1, (await BusinessState.ReadAsync(host)).Sales);
    }

    [Fact]
    public async Task EveryPosAction_ReportsAPlainFailure_NotAnException_WhileTheDatabaseIsExclusivelyLocked()
    {
        using var _ = new NoLockWait();
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var (sessionId, cartId) = await OpenCartAsync(host.Services, shop, 1m);

        await using (await LockAsync(host, "BEGIN EXCLUSIVE"))
        {
            using var scope = host.Services.CreateScope();
            var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();

            // actions that WRITE cannot happen while the database is locked: each says so plainly and changes nothing
            var writes = new (string Code, string? Message)[]
            {
                Describe(await pos.OpenSessionAsync("cashier-2", shop.WarehouseId)),
                Describe(await pos.AddProductAsync(cartId, Sku, 1m)),
                Describe(await pos.ChangeQuantityAsync(cartId, shop.ProductId, 2m)),
                Describe(await pos.RemoveProductAsync(cartId, shop.ProductId)),
                Describe(await pos.ClearCartAsync(cartId)),
                Describe(await pos.CheckoutAsync(cartId))
            };

            Assert.All(writes, r =>
            {
                Assert.StartsWith("POS.", r.Code);
                Assert.DoesNotContain("SQLite", r.Message ?? "", StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("   at ", r.Message ?? "");
                Assert.Contains("nothing was changed", r.Message ?? "", StringComparison.OrdinalIgnoreCase);
            });

            // a failed save leaves the scope consistent with the database: the cart still holds its item (it was not half-edited in memory),
            // so a later refusal is the honest business one, never one based on a change that did not happen
            var close = await pos.CloseSessionAsync(sessionId);
            Assert.Equal("POS.CloseSession.OpenCartHasItems", close.ErrorCode);
        }

        // the lock is gone: the cart is exactly as it was and the shop carries on
        Assert.Equal(1, await CountAsync(host, "pos_Carts", "Status = 1"));
        Assert.True((await CheckoutAsync(host.Services, cartId)).IsSuccess);
        Assert.Equal(9m, await OnHandAsync(host, shop.ProductId));
    }

    private static (string Code, string? Message) Describe(POS.Contracts.Models.POSOperationResult r) => (r.ErrorCode ?? "", r.ErrorMessage);
    private static (string Code, string? Message) Describe(POS.Contracts.Models.POSOpenSessionResult r) => (r.ErrorCode ?? "", r.ErrorMessage);
    private static (string Code, string? Message) Describe(POS.Contracts.Models.POSStartCartResult r) => (r.ErrorCode ?? "", r.ErrorMessage);
    private static (string Code, string? Message) Describe(POS.Contracts.Models.POSAddItemResult r) => (r.ErrorCode ?? "", r.ErrorMessage);
    private static (string Code, string? Message) Describe(POS.Contracts.Models.POSCheckoutResult r) => (r.ErrorCode ?? "", r.ErrorMessage);

    // ------------------------------------------------------------------ failure at commit time

    /// <summary>Test-only: lets the work finish inside the transaction and then fails before COMMIT, like a disk error at commit.</summary>
    private sealed class FailBeforeCommitModule(FailBeforeCommitModule.Switch toggle) : IHostingModule
    {
        public sealed class Switch { public bool FailNextCommit { get; set; } }

        public void RegisterServices(HostBuilderContext context, IServiceCollection services)
            => services.AddScoped<IAtomicOperation>(sp => new Decorator(sp.GetRequiredService<SharedDatabaseScope>(), toggle));

        private sealed class Decorator(SharedDatabaseScope inner, Switch toggle) : IAtomicOperation
        {
            public bool IsActive => inner.IsActive;

            public Task<Result<T>> ExecuteAsync<T>(Func<Task<Result<T>>> work, CancellationToken cancellationToken = default)
                => inner.ExecuteAsync(async () =>
                {
                    var result = await work();
                    if (toggle.FailNextCommit && result.IsSuccess) throw new IOException("injected: the commit could not be written");
                    return result;
                }, cancellationToken);
        }
    }

    [Fact]
    public async Task SaleDoesNotModifyInventory_WhenTheTransactionCommitFails_AndTheRetrySucceeds()
    {
        var toggle = new FailBeforeCommitModule.Switch();
        await using var host = await IntegrationHost.StartAllAsync(extra: [new FailBeforeCommitModule(toggle)]);
        var shop = await CreateShopAsync(host.Services);
        var (_, cartId) = await OpenCartAsync(host.Services, shop, 2m);
        var before = await BusinessState.ReadAsync(host);

        toggle.FailNextCommit = true;
        var failed = await CheckoutAsync(host.Services, cartId);   // every write was made; the commit never happened

        Assert.Equal("POS.Checkout.NotSaved", failed.ErrorCode);
        Assert.Equal(before, await BusinessState.ReadAsync(host));
        Assert.Equal(10m, await OnHandAsync(host, shop.ProductId));

        toggle.FailNextCommit = false;
        Assert.True((await CheckoutAsync(host.Services, cartId)).IsSuccess);
        Assert.Equal(8m, await OnHandAsync(host, shop.ProductId));
        Assert.Equal(before.Sales + 1, (await BusinessState.ReadAsync(host)).Sales);
    }

    // ------------------------------------------------------------------ constraint violation

    [Fact]
    public async Task AConstraintViolation_IsAClearFailure_AndLeavesNoPartialRecord()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        var shop = await CreateShopAsync(host.Services);
        var products = await CountAsync(host, "cat_Products");

        using var scope = host.Services.CreateScope();
        var p = scope.ServiceProvider;
        var category = await p.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Other"));
        var unit = await p.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Box", "bx"));
        var duplicate = await p.GetRequiredService<CreateProductCommandHandler>()
            .HandleAsync(new CreateProductCommand(shop.Sku, "Another cola", category.Value.Value, unit.Value.Value, 1m, 1m));

        Assert.True(duplicate.IsFailure);   // a failed Result, not an exception
        Assert.Equal(products, await CountAsync(host, "cat_Products"));
        Assert.Equal(1, await CountAsync(host, "cat_Products", $"Sku = '{shop.Sku}'"));
    }

    // ------------------------------------------------------------------ unavailable / corrupt / migration failure at start-up

    private static string NewFolder() => Path.Combine(Path.GetTempPath(), "genericpos-dbfail-" + Guid.NewGuid().ToString("N"));

    private static string DatabaseFile(string folder) => Path.Combine(folder, "GenericPOS", "integration.db");

    private static string Hash(string path)
    {
        SqliteConnection.ClearAllPools();   // a failed start-up may leave a pooled connection holding the file
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    [Fact]
    public async Task ACorruptDatabaseFile_StopsStartUp_IsNeverOverwritten_AndAGoodRestoreStartsNormally()
    {
        var folder = NewFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "GenericPOS"));
            var path = DatabaseFile(folder);
            await File.WriteAllBytesAsync(path, System.Text.Encoding.ASCII.GetBytes(new string('x', 8192)));   // not a SQLite file
            var before = Hash(path);

            await Assert.ThrowsAnyAsync<Exception>(() => IntegrationHost.StartAllAsync(folder));

            Assert.True(File.Exists(path));
            Assert.Equal(before, Hash(path));   // the evidence is untouched: nothing was deleted, recreated or "repaired"

            // recovery: the bad file is moved aside (as an operator or a restore would) and the application starts on a fresh database
            File.Move(path, path + ".corrupt");
            await using var host = await IntegrationHost.StartAllAsync(folder);
            host.KeepFiles = true;
            Assert.Contains("pos_Carts", await host.GetTablesAsync());
            Assert.Equal(before, Hash(path + ".corrupt"));
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    [Fact]
    public async Task ATruncatedDatabase_IsNotSilentlyReplacedByAnEmptyOne()
    {
        var folder = NewFolder();
        try
        {
            string path;
            await using (var host = await IntegrationHost.StartAllAsync(folder))
            {
                host.KeepFiles = true;
                await CreateShopAsync(host.Services);
                path = host.DatabasePath;
            }

            SqliteConnection.ClearAllPools();
            var bytes = await File.ReadAllBytesAsync(path);
            await File.WriteAllBytesAsync(path, bytes[..(bytes.Length / 2)]);   // a copy that stopped half way
            var before = Hash(path);

            var started = false;
            try
            {
                await using var host = await IntegrationHost.StartAllAsync(folder);
                started = true;
                host.KeepFiles = true;
                // if SQLite accepted the file, the data it could still read must not have been replaced by an empty database
                Assert.True(File.Exists(path));
            }
            catch (Exception)
            {
                Assert.False(started);
            }

            Assert.True(new FileInfo(path).Length >= bytes.Length / 2 - 1, "the damaged file must not be shortened or recreated");
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    [Fact]
    public async Task AMigrationThatCannotApply_StopsStartUp_LeavesNoHalfAppliedSchema_AndWorksOnceTheConflictIsGone()
    {
        var folder = NewFolder();
        try
        {
            // an unrelated object already occupies a name the Inventory migration needs
            Directory.CreateDirectory(Path.Combine(folder, "GenericPOS"));
            await using (var setup = new SqliteConnection($"Data Source={DatabaseFile(folder)};Pooling=False"))
            {
                await setup.OpenAsync();
                await using var command = setup.CreateCommand();
                command.CommandText = "CREATE TABLE inv_Warehouses (Unrelated TEXT); INSERT INTO inv_Warehouses VALUES ('keep me');";
                await command.ExecuteNonQueryAsync();
            }

            await Assert.ThrowsAnyAsync<Exception>(() => IntegrationHost.StartAllAsync(folder));

            // the conflicting data is untouched and the failed migration was rolled back as a whole
            await using (var inspect = new SqliteConnection($"Data Source={DatabaseFile(folder)};Pooling=False"))
            {
                await inspect.OpenAsync();
                Assert.Equal(["keep me"], await IntegrationHost.QueryAsync(inspect, "SELECT Unrelated FROM inv_Warehouses"));
                Assert.Empty(await IntegrationHost.QueryAsync(inspect, "SELECT name FROM sqlite_master WHERE name = 'inv_StockItems'"));
            }

            // recovery: with the conflict removed the application starts and builds the schema
            await using (var fix = new SqliteConnection($"Data Source={DatabaseFile(folder)};Pooling=False"))
            {
                await fix.OpenAsync();
                await using var command = fix.CreateCommand();
                command.CommandText = "DROP TABLE inv_Warehouses;";
                await command.ExecuteNonQueryAsync();
            }

            SqliteConnection.ClearAllPools();
            await using var host = await IntegrationHost.StartAllAsync(folder);
            host.KeepFiles = true;
            Assert.Contains("inv_StockItems", await host.GetTablesAsync());
            Assert.True((await CreateShopAsync(host.Services)).ProductId != Guid.Empty);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            IntegrationHost.DeleteFolder(folder);
        }
    }

    [Fact]
    public async Task ADatabasePathThatIsADirectory_StopsStartUpWithoutCreatingAnything()
    {
        var folder = NewFolder();
        try
        {
            Directory.CreateDirectory(DatabaseFile(folder));   // "integration.db" is a folder: the database cannot be opened

            await Assert.ThrowsAnyAsync<Exception>(() => IntegrationHost.StartAllAsync(folder));

            Assert.True(Directory.Exists(DatabaseFile(folder)));
            Assert.Empty(Directory.GetFileSystemEntries(DatabaseFile(folder)));
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }
}
