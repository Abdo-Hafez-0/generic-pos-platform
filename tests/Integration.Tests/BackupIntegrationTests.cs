using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Client.Backup.Application;
using Client.Backup.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// MISS-04a on the production-like offline desktop (real SQLite file, real Users/Audit, a network that refuses every request): a signed-in
/// administrator chooses a folder, backs up after a sale and checks the backup. The backup holds the sale, the live database keeps
/// working, nothing goes over the network, and the actions are in the audit log under the administrator's name.
/// </summary>
[Collection(HostCollection.Name)]
public sealed class BackupIntegrationTests
{
    private static async Task<T> InScopeAsync<T>(IServiceProvider services, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = services.CreateScope();
        return await work(scope.ServiceProvider);
    }

    private static long Count(string path, string table)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static async Task<AuditEntryResult> AuditEntryAsync(IServiceProvider services, string action)
    {
        for (var i = 0; i < 200; i++)
        {
            var found = await InScopeAsync(services, async p => (await p.GetRequiredService<IAuditReader>().QueryAsync(new AuditEntryFilter(Module: "backup", Action: action), pageSize: 10)).Items);
            if (found.Count > 0) return Assert.Single(found);
            await Task.Delay(25);
        }

        throw new Xunit.Sdk.XunitException($"No audit entry backup/{action}.");
    }

    [Fact]
    public async Task An_administrator_backs_up_the_shop_offline_and_the_backup_holds_the_sales()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var services = desktop.Services;
        var user = services.GetRequiredService<ICurrentUser>().UserName;
        var shop = await CreateShopAsync(services);
        var (_, cartId) = await OpenCartAsync(services, shop, 2m);
        Assert.True((await CheckoutAsync(services, cartId)).IsSuccess);

        // the administrator created at first run holds the new capabilities
        foreach (var capability in BackupCapabilities.All)
            Assert.True(await services.GetRequiredService<IAuthorizationService>().IsAllowedAsync(capability.Code), capability.Code);

        var folder = Path.Combine(desktop.Host.Folder, "usb", "Backups");
        var settings = await InScopeAsync(services, p => p.GetRequiredService<UpdateBackupSettingsCommandHandler>().HandleAsync(new UpdateBackupSettingsCommand(folder, 14)));
        Assert.True(settings.IsSuccess, settings.IsFailure ? settings.Error.Description : null);

        var made = await InScopeAsync(services, p => p.GetRequiredService<CreateBackupCommandHandler>().HandleAsync(new CreateBackupCommand()));
        Assert.True(made.IsSuccess, made.IsFailure ? made.Error.Description : null);

        // the backup is a complete copy: the sale, the stock movement, the users, the migration history of every module
        var backup = made.Value.Location;
        Assert.StartsWith(folder, backup);
        Assert.Equal(Count(desktop.Host.DatabasePath, "sal_Sales"), Count(backup, "sal_Sales"));
        Assert.Equal(1L, Count(backup, "sal_Sales"));
        Assert.Equal(Count(desktop.Host.DatabasePath, "usr_Users"), Count(backup, "usr_Users"));
        Assert.NotEmpty(made.Value.Migrations);

        var verified = await InScopeAsync(services, p => p.GetRequiredService<VerifyBackupCommandHandler>().HandleAsync(new VerifyBackupCommand(made.Value.Id)));
        Assert.True(verified.IsSuccess, verified.IsFailure ? verified.Error.Description : null);
        Assert.True(verified.Value.LastVerifyPassed);

        // the shop keeps selling after the backup
        var (_, nextCart) = await OpenCartAsync(services, shop, 1m);
        Assert.True((await CheckoutAsync(services, nextCart)).IsSuccess);
        Assert.Equal(2L, Count(desktop.Host.DatabasePath, "sal_Sales"));
        Assert.Equal(1L, Count(backup, "sal_Sales"));

        // offline, and audited under the administrator's name
        Assert.Equal(0, desktop.Network.Requests);
        Assert.Equal(user, (await AuditEntryAsync(services, "backup.created")).ActorName);
        Assert.Equal(user, (await AuditEntryAsync(services, "backup.verified")).ActorName);
        Assert.Equal(user, (await AuditEntryAsync(services, "backup.settings-changed")).ActorName);

        // the history is kept next to the database, not in it
        var history = Path.Combine(Path.GetDirectoryName(desktop.Host.DatabasePath)!, "Backup", "history.json");
        Assert.True(File.Exists(history));
        Assert.Contains(made.Value.Id.ToString(), await File.ReadAllTextAsync(history));
    }

    [Fact]
    public async Task Before_a_folder_is_chosen_the_desktop_starts_and_backing_up_explains_what_is_missing()
    {
        await using var desktop = await OfflineDesktop.StartAsync();

        var made = await InScopeAsync(desktop.Services, p => p.GetRequiredService<CreateBackupCommandHandler>().HandleAsync(new CreateBackupCommand()));

        Assert.Equal(BackupErrorCodes.NotConfigured, made.Error.Code);
    }
}
