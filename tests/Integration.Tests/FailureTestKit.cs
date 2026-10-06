using Catalog.Application.Commands;
using Inventory.Application.Commands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace Integration.Tests;

/// <summary>A product on the shelf of a warehouse, created through the real handlers of a real host.</summary>
public sealed record Shop(Guid ProductId, string Sku, Guid WarehouseId);

/// <summary>
/// Test-only helpers for the Stage 12 failure campaign: deterministic data, direct database inspection and DATABASE-LEVEL failure injection.
/// Failures are injected as SQLite triggers on a chosen table, so the real EF Core code, the real shared transaction and the real
/// SQLite engine fail exactly where a full disk, a constraint or a corrupt page would - nothing in production code is aware of it.
/// </summary>
public static class FailureTestKit
{
    public const string Sku = "COLA-1";

    public static async Task<Shop> CreateShopAsync(IServiceProvider services, string sku = Sku, string name = "Cola", decimal salePrice = 2.5m, decimal stock = 10m, Guid? warehouseId = null)
    {
        using var scope = services.CreateScope();
        var p = scope.ServiceProvider;

        var category = await p.GetRequiredService<CreateCategoryCommandHandler>().HandleAsync(new CreateCategoryCommand("Cat-" + sku));
        var unit = await p.GetRequiredService<CreateUnitCommandHandler>().HandleAsync(new CreateUnitCommand("Piece-" + sku, "p" + sku.ToLowerInvariant()));
        Assert.True(category.IsSuccess && unit.IsSuccess, "category/unit: " + (category.IsFailure ? category.Error : unit.IsFailure ? unit.Error : null));
        var product = await p.GetRequiredService<CreateProductCommandHandler>()
            .HandleAsync(new CreateProductCommand(sku, name, category.Value.Value, unit.Value.Value, salePrice, 1m));
        Assert.True(product.IsSuccess, product.IsFailure ? product.Error.ToString() : null);

        var warehouse = warehouseId ?? (await p.GetRequiredService<CreateWarehouseCommandHandler>().HandleAsync(new CreateWarehouseCommand("Main", "MAIN" + Guid.NewGuid().ToString("N")[..6]))).Value;
        var stocked = await p.GetRequiredService<AddStockCommandHandler>().HandleAsync(new AddStockCommand(product.Value.Value, warehouse, stock));
        Assert.True(stocked.IsSuccess, stocked.IsFailure ? stocked.Error.ToString() : null);

        return new Shop(product.Value.Value, sku, warehouse);
    }

    /// <summary>Opens a till session and a cart holding <paramref name="quantity"/> of the shop's product (each in its own scope, like separate UI actions).</summary>
    public static async Task<(Guid SessionId, Guid CartId)> OpenCartAsync(IServiceProvider services, Shop shop, decimal quantity, string sku = Sku)
    {
        using var scope = services.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<IPOSService>();
        var session = await pos.OpenSessionAsync("cashier-1", shop.WarehouseId);
        Assert.True(session.IsSuccess, session.ErrorMessage);
        var cart = await pos.StartCartAsync(session.SessionId);
        Assert.True(cart.IsSuccess, cart.ErrorMessage);
        var added = await pos.AddProductAsync(cart.CartId, sku, quantity);
        Assert.True(added.IsSuccess, added.ErrorMessage);
        return (session.SessionId, cart.CartId);
    }

    public static async Task<POSCheckoutResult> CheckoutAsync(IServiceProvider services, Guid cartId, decimal? tendered = 20m)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPOSService>().CheckoutAsync(cartId,
            payment: tendered is null ? null : new POSPaymentRequest(POSPaymentMethod.Cash, TenderedAmount: tendered));
    }

    // ------------------------------------------------------------------ direct database inspection (never trusts a returned Result)

    public static async Task<long> CountAsync(IntegrationHost host, string table, string? where = null)
        => Convert.ToInt64(await ScalarAsync(host, $"SELECT COUNT(*) FROM {table}" + (where is null ? "" : " WHERE " + where)));

    public static async Task<object?> ScalarAsync(IntegrationHost host, string sql)
    {
        await using var connection = await host.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    /// <summary>The stock on hand of a product, read straight from the database (decimals are stored as text).</summary>
    public static async Task<decimal> OnHandAsync(IntegrationHost host, Guid productId)
    {
        await using var connection = await host.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SUM(CAST(b.OnHand AS REAL)) FROM inv_InventoryBalances b JOIN inv_StockItems i ON i.Id = b.StockItemId WHERE i.CatalogProductId = $p";
        command.Parameters.AddWithValue("$p", productId.ToString().ToUpperInvariant());
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull) throw new InvalidOperationException("No balance row for the product.");
        return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Counts of every table a sale touches, to compare before/after a failure.</summary>
    public sealed record BusinessState(long Sales, long SaleItems, long SalesTransactions, long Payments, long StockMovements, long OpenCarts, long CheckedOutCarts, long Sessions)
    {
        public static async Task<BusinessState> ReadAsync(IntegrationHost host) => new(
            await CountAsync(host, "sal_Sales"),
            await CountAsync(host, "sal_SaleItems"),
            await CountAsync(host, "sal_SalesTransactions"),
            await CountAsync(host, "pay_Payments"),
            await CountAsync(host, "inv_StockMovements"),
            await CountAsync(host, "pos_Carts", "Status = 1"),
            await CountAsync(host, "pos_Carts", "Status <> 1"),
            await CountAsync(host, "pos_Sessions"));
    }

    // ------------------------------------------------------------------ database-level failure injection

    /// <summary>Makes every INSERT into <paramref name="table"/> (optionally only when the SQL condition <paramref name="when"/> holds) fail inside SQLite until the returned handle is disposed.</summary>
    public static Task<IAsyncDisposable> FailInsertsAsync(IntegrationHost host, string table, string? when = null) => InjectAsync(host, table, "INSERT", when);

    /// <summary>Makes every UPDATE of <paramref name="table"/> fail inside SQLite until the returned handle is disposed.</summary>
    public static Task<IAsyncDisposable> FailUpdatesAsync(IntegrationHost host, string table, string? when = null) => InjectAsync(host, table, "UPDATE", when);

    private static async Task<IAsyncDisposable> InjectAsync(IntegrationHost host, string table, string operation, string? when)
    {
        var trigger = $"inject_fail_{operation.ToLowerInvariant()}_{table}";
        await ExecuteAsync(host, $"CREATE TRIGGER {trigger} BEFORE {operation} ON {table}{(when is null ? "" : " WHEN " + when)} BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        return new Injection(host, trigger);
    }

    public static async Task ExecuteAsync(IntegrationHost host, string sql)
    {
        await using var connection = await host.OpenDatabaseAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Injection(IntegrationHost host, string trigger) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await ExecuteAsync(host, $"DROP TRIGGER IF EXISTS {trigger}");
    }

    // ------------------------------------------------------------------ interrupting the commit

    /// <summary>
    /// Test-only: finishes the operation's work inside its transaction and then makes the commit fail (<see cref="Mode.Fail"/>) or the database connection
    /// vanish without COMMIT or ROLLBACK (<see cref="Mode.Crash"/>, what the database sees when the process is killed at that instant).
    /// </summary>
    public sealed class InterruptCommit : Client.Host.Hosting.IHostingModule
    {
        public enum Mode { None, Fail, Crash }

        public Mode Next { get; set; }

        public void RegisterServices(Microsoft.Extensions.Hosting.HostBuilderContext context, IServiceCollection services)
            => services.AddScoped<Platform.Application.Abstractions.Data.IAtomicOperation>(sp =>
                new Decorator(sp.GetRequiredService<Platform.Infrastructure.Persistence.SharedDatabaseScope>(), this));

        private sealed class Decorator(Platform.Infrastructure.Persistence.SharedDatabaseScope inner, InterruptCommit owner) : Platform.Application.Abstractions.Data.IAtomicOperation
        {
            public bool IsActive => inner.IsActive;

            public Task<Platform.Core.Results.Result<T>> ExecuteAsync<T>(Func<Task<Platform.Core.Results.Result<T>>> work, CancellationToken cancellationToken = default)
                => inner.ExecuteAsync(async () =>
                {
                    var result = await work();
                    if (!result.IsSuccess || owner.Next == Mode.None) return result;

                    var mode = owner.Next;
                    if (mode == Mode.Crash) inner.Dispose();   // the connection is gone with the transaction still open
                    throw new IOException(mode == Mode.Crash ? "injected: the process was killed before the commit" : "injected: the commit could not be written");
                }, cancellationToken);
        }
    }
}
