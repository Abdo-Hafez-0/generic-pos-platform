using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Backup.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Application.Abstractions.Authorization;
using Platform.Core.Results;
using Tests.Common.Security;

namespace Backup.Tests;

/// <summary>MISS-04a: the backup capabilities, the handlers that check them, and the composition.</summary>
public sealed class BackupSecurityTests : IDisposable
{
    private readonly BackupWorld _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void The_four_capabilities_need_no_license_and_the_dangerous_ones_are_sensitive()
    {
        var all = new BackupCapabilityProvider().GetCapabilities().ToDictionary(c => c.Code);

        Assert.Equal(["backup.configure", "backup.create", "backup.delete", "backup.restore"], all.Keys.Order());
        Assert.All(all.Values, c => Assert.Equal(LicenseRequirement.None, c.License));
        Assert.All(all.Values, c => Assert.Equal("backup", c.Module));
        Assert.False(all["backup.create"].IsSensitive);
        Assert.True(all["backup.restore"].IsSensitive);
        Assert.True(all["backup.delete"].IsSensitive);
        Assert.True(all["backup.configure"].IsSensitive);
        _ = new CapabilityCatalog([new BackupCapabilityProvider()]);   // valid codes, an owner
    }

    public static TheoryData<string, string> Handlers => new()
    {
        { "create", BackupCapabilities.Create },
        { "verify", BackupCapabilities.Create },
        { "history", BackupCapabilities.Create },
        { "settings", BackupCapabilities.Create },
        { "delete", BackupCapabilities.Delete },
        { "configure", BackupCapabilities.Configure },
    };

    private static Task<Result> Run(string handler, BackupService service, IAuthorizationService auth, Guid id) => handler switch
    {
        "create" => Plain(new CreateBackupCommandHandler(service, auth).HandleAsync(new CreateBackupCommand())),
        "verify" => Plain(new VerifyBackupCommandHandler(service, auth).HandleAsync(new VerifyBackupCommand(id))),
        "history" => Plain(new GetBackupHistoryQueryHandler(service, auth).HandleAsync(new GetBackupHistoryQuery())),
        "settings" => Plain(new GetBackupSettingsQueryHandler(service, auth).HandleAsync(new GetBackupSettingsQuery())),
        "delete" => new DeleteBackupCommandHandler(service, auth).HandleAsync(new DeleteBackupCommand(id)),
        "configure" => Plain(new UpdateBackupSettingsCommandHandler(service, auth).HandleAsync(new UpdateBackupSettingsCommand(null, 5))),
        _ => throw new ArgumentOutOfRangeException(nameof(handler))
    };

    private static async Task<Result> Plain<T>(Task<Result<T>> task) => await task;

    [Theory]
    [MemberData(nameof(Handlers))]
    public async Task Each_handler_asks_for_its_capability_and_does_nothing_without_it(string handler, string capability)
    {
        var existing = (await _world.Service.CreateAsync(BackupOrigin.Manual)).Value;
        var filesBefore = _world.BackupFiles();
        var settingsBefore = await _world.Service.GetSettingsAsync();

        var refused = await Run(handler, _world.Service, new ScriptedAuthorizationService(), existing.Id);

        Assert.True(refused.IsFailure);
        Assert.Equal(SecurityErrors.Forbidden(capability).Code, refused.Error.Code);
        Assert.Equal(filesBefore, _world.BackupFiles());
        Assert.Equal(settingsBefore, await _world.Service.GetSettingsAsync());
        Assert.Null(Assert.Single(await _world.Service.GetHistoryAsync()).LastVerifiedAt);

        var allowed = new ScriptedAuthorizationService(capability);
        Assert.True((await Run(handler, _world.Service, allowed, existing.Id)).IsSuccess);
        Assert.Equal([capability], allowed.Asked);
    }

    [Fact]
    public async Task The_composition_resolves_every_handler_with_scope_validation_and_keeps_its_files_next_to_the_database()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = _world.Root,
            ["Backup:LocalFolder"] = _world.BackupFolder,
            ["Backup:KeepLocal"] = "7",
        }).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAuthorizationService, AllowAllAuthorizationService>();
        services.AddSingleton<ICurrentUser, NobodySignedIn>();
        new ClientBackupHostingModule().RegisterServices(new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration }, services);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Assert.Equal(Path.Combine(_world.Root, "GenericPOS", "Backup"), provider.GetRequiredService<BackupWorkspace>().Root);
        Assert.Equal(new BackupSettings(_world.BackupFolder, 7), await provider.GetRequiredService<BackupService>().GetSettingsAsync());
        foreach (var type in new[] { typeof(CreateBackupCommandHandler), typeof(VerifyBackupCommandHandler), typeof(GetBackupHistoryQueryHandler),
                     typeof(DeleteBackupCommandHandler), typeof(GetBackupSettingsQueryHandler), typeof(UpdateBackupSettingsCommandHandler),
                     typeof(PrepareRestoreCommandHandler), typeof(PrepareRestoreFromFileCommandHandler), typeof(ConfirmRestoreCommandHandler),
                     typeof(CancelRestoreCommandHandler), typeof(GetRestoreStatusQueryHandler) })
            Assert.NotNull(provider.GetRequiredService(type));
        Assert.Contains(provider.GetServices<ICapabilityProvider>(), p => p is BackupCapabilityProvider);
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is BackupInitializer);
        Assert.IsType<PendingRestoreStep>(Assert.Single(provider.GetServices<Client.Host.Hosting.IStartupPreparation>()));
    }

    private sealed class NobodySignedIn : ICurrentUser
    {
        public bool IsAuthenticated => false;
        public Guid UserId => Guid.Empty;
        public string UserName => string.Empty;
        public string DisplayName => string.Empty;
    }
}
