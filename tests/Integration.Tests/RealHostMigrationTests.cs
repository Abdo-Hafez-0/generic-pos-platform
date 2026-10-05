namespace Integration.Tests;

/// <summary>The real host, with every module, applying every module's real migrations to one real SQLite file.</summary>
[Collection(HostCollection.Name)]
public sealed class RealHostMigrationTests
{
    /// <summary>Table prefix owned by each module (Reporting owns no tables).</summary>
    private static readonly Dictionary<string, string> Prefixes = new()
    {
        ["Catalog"] = "cat_",
        ["Inventory"] = "inv_",
        ["Sales"] = "sal_",
        ["POS"] = "pos_",
        ["Customers"] = "cus_",
        ["Suppliers"] = "sup_",
        ["Purchasing"] = "pur_",
        ["Pricing"] = "pri_",
        ["Payments"] = "pay_",
        ["Users"] = "usr_",
        ["Audit"] = "aud_",
        ["CashManagement"] = "cash_"
    };

    [Fact]
    public async Task EveryModuleCreatesItsOwnTablesUnderItsOwnPrefix()
    {
        await using var host = await IntegrationHost.StartAllAsync();

        var tables = await host.GetTablesAsync();

        foreach (var (module, prefix) in Prefixes)
            Assert.True(tables.Any(t => t.StartsWith(prefix, StringComparison.Ordinal)), $"{module} created no {prefix}* table");

        Assert.Contains("cus_Customers", tables);
        Assert.Contains("sup_Suppliers", tables);
        Assert.Contains("pur_PurchaseOrders", tables);
        Assert.Contains("pri_Prices", tables);
        Assert.Contains("pay_Payments", tables);
        Assert.Contains("usr_Users", tables);
        Assert.Contains("aud_AuditEntries", tables);
        Assert.Contains("cash_Sessions", tables);
    }

    [Fact]
    public async Task NoTableBelongsToNoModule_AndReportingOwnsNone()
    {
        await using var host = await IntegrationHost.StartAllAsync();

        var tables = await host.GetTablesAsync();

        var strays = tables.Where(t => !t.StartsWith("__", StringComparison.Ordinal) && !Prefixes.Values.Any(p => t.StartsWith(p, StringComparison.Ordinal))).ToList();
        Assert.Empty(strays);
        Assert.DoesNotContain(tables, t => t.StartsWith("rep_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoForeignKeyCrossesAModuleBoundary()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        await using var connection = await host.OpenDatabaseAsync();

        var crossing = new List<string>();
        foreach (var table in await host.GetTablesAsync())
        {
            foreach (var referenced in await IntegrationHost.QueryAsync(connection, $"SELECT \"table\" FROM pragma_foreign_key_list('{table}')"))
            {
                if (PrefixOf(table) != PrefixOf(referenced)) crossing.Add($"{table} -> {referenced}");
            }
        }

        Assert.Empty(crossing);
    }

    [Fact]
    public async Task EveryStage8MigrationIsRecordedInTheMigrationHistory()
    {
        await using var host = await IntegrationHost.StartAllAsync();
        await using var connection = await host.OpenDatabaseAsync();

        var applied = await IntegrationHost.QueryAsync(connection, "SELECT MigrationId FROM \"__EFMigrationsHistory\"");

        foreach (var name in new[]
                 {
                     "InitialCustomersSchema", "InitialSuppliersSchema", "InitialPurchasingSchema", "InitialPricingSchema",
                     "InitialPaymentsSchema", "InitialUsersSchema", "InitialAuditSchema", "InitialCashManagementSchema"
                 })
        {
            Assert.Contains(applied, id => id.EndsWith(name, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task RestartingTheHostOnTheSameDatabaseAppliesNothingNewAndKeepsTheData()
    {
        string folder;
        int before;
        await using (var first = await IntegrationHost.StartAllAsync())
        {
            first.KeepFiles = true;
            folder = first.Folder;
            before = await CountMigrationsAsync(first);
            await using var connection = await first.OpenDatabaseAsync();
            await using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO cus_Customers (Id, Code, Name, Status, CreatedAt, UpdatedAt) VALUES ('11111111-1111-1111-1111-111111111111', 'KEEP-1', 'Kept customer', 1, '2026-01-01', '2026-01-01')";
            await insert.ExecuteNonQueryAsync();
        }

        try
        {
            await using var second = await IntegrationHost.StartAsync([.. IntegrationHost.CoreModules, .. IntegrationHost.Stage8Modules], reuseFolder: folder);

            Assert.Equal(before, await CountMigrationsAsync(second));
            await using var connection = await second.OpenDatabaseAsync();
            Assert.Equal(["Kept customer"], await IntegrationHost.QueryAsync(connection, "SELECT Name FROM cus_Customers WHERE Code = 'KEEP-1'"));
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    private static string PrefixOf(string table) => table.StartsWith("__", StringComparison.Ordinal) ? "__" : table[..(table.IndexOf('_') + 1)];

    private static async Task<int> CountMigrationsAsync(IntegrationHost host)
    {
        await using var connection = await host.OpenDatabaseAsync();
        return int.Parse((await IntegrationHost.QueryAsync(connection, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\""))[0]);
    }
}
