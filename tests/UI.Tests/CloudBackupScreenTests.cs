using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Client.Backup.Application;
using Client.Backup.Domain;
using Client.Backup.Infrastructure;
using Client.Desktop.Resources;
using Client.Desktop.Screens;
using Client.Desktop.Screens.CloudBackup;
using Client.Desktop.Shell;
using CloudBackup.Application;
using CloudBackup.Contracts;
using CloudBackup.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Authorization;
using Platform.Application.Abstractions.Security;
using Platform.Core.Results;
using Platform.Presentation.Actions;
using Platform.Presentation.Screens;
using Tests.Common.Security;

namespace UI.Tests;

/// <summary>
/// MISS-04e3: the cloud backup screen on the real local backup and CloudBackup services (real files, a protected key ring) with an in-memory
/// backup server: the recovery code shown once and confirmed, the access key, a backup that goes up, and a restore from the cloud - on this
/// PC with its key, and on a new PC with the typed recovery code - through the local restore's question, confirmation and restart.
/// </summary>
public sealed class CloudBackupScreenTests : IDisposable
{
    private static readonly string[] Everything =
        [CloudBackupCapabilities.Manage, CloudBackupCapabilities.Restore, BackupCapabilities.Create, BackupCapabilities.Restore, BackupCapabilities.Configure];

    private readonly List<string> _roots = [];
    private readonly List<ServiceProvider> _providers = [];
    private readonly InMemoryBackupServer _server = new();
    private readonly CountingRestarter _restarter = new();
    private readonly ECDiffieHellman _vendor = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        foreach (var provider in _providers) provider.Dispose();
        _vendor.Dispose();
        SqliteConnection.ClearAllPools();
        foreach (var root in _roots)
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>One installation: its own folder, database and key ring, sharing the in-memory server.</summary>
    private (CloudBackupViewModel Screen, ServiceProvider Services) Pc(params string[] allowed)
    {
        var root = Path.Combine(Path.GetTempPath(), "genericpos-ui-cloud-" + Guid.NewGuid().ToString("N"));
        _roots.Add(root);
        var database = Path.Combine(root, "GenericPOS", "genericpos.db");
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE \"__EFMigrationsHistory\" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL); INSERT INTO \"__EFMigrationsHistory\" VALUES ('20261005_Init', '10.0.11'); CREATE TABLE sal_Sales (Total TEXT); INSERT INTO sal_Sales VALUES ('2.50');";
            command.ExecuteNonQuery();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:DatabaseFolder"] = "Custom",
            ["Database:CustomFolderPath"] = root,
            ["Backup:LocalFolder"] = Path.Combine(root, "usb"),
            ["CloudBackup:EscrowKeys:0:KeyId"] = "escrow-2026",
            ["CloudBackup:EscrowKeys:0:PublicKey"] = Convert.ToBase64String(_vendor.ExportSubjectPublicKeyInfo()),
        }).Build();

        var services = new ServiceCollection().AddLogging();
        services.AddSingleton<IAuthorizationService>(new ScriptedAuthorizationService(allowed));
        services.AddSingleton<ICurrentUser, FakeCurrentUser>();
        services.AddSingleton<ISecretProtector>(new TestProtector());
        services.AddSingleton<IUnattendedWorkGate>(new LicensedGate());
        var context = new HostBuilderContext(new Dictionary<object, object>()) { Configuration = configuration };
        new ClientBackupHostingModule().RegisterServices(context, services);
        new CloudBackupHostingModule().RegisterServices(context, services);
        services.AddSingleton<IBackupClient>(_server);   // registered last: replaces the null transport
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _providers.Add(provider);

        var runner = new UiActionRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UiActionRunner>.Instance);
        return (new CloudBackupViewModel(runner, _restarter), provider);
    }

    private static async Task Run(CloudBackupViewModel vm, System.Windows.Input.ICommand command, object? parameter = null)
    {
        Assert.True(command.CanExecute(parameter), "the command should be available");
        command.Execute(parameter);
        for (var i = 0; i < 500 && vm.IsBusy; i++) await Task.Delay(10);
        Assert.False(vm.IsBusy);
    }

    /// <summary>Makes the recovery code on the screen and types back the two groups asked for; returns the code as written down.</summary>
    private static async Task<string> SetUpCodeAsync(CloudBackupViewModel vm)
    {
        await Run(vm, vm.MakeCodeCommand);
        Assert.True(vm.IsCodeShown);
        var code = vm.CodeText;
        var groups = code.Split('-');
        var asked = new[] { vm.FirstGroupLabel, vm.SecondGroupLabel }.Select(l => int.Parse(new string(l.Where(char.IsDigit).ToArray()), CultureInfo.InvariantCulture)).ToArray();
        vm.FirstGroup = groups[asked[0] - 1];
        vm.SecondGroup = groups[asked[1] - 1].ToLowerInvariant();
        await Run(vm, vm.ConfirmCodeCommand);
        Assert.False(vm.IsCodeShown);
        Assert.Equal(string.Empty, vm.CodeText);   // never shown again
        return code;
    }

    private static async Task BackUpAndUploadAsync(ServiceProvider services)
    {
        Assert.True((await services.GetRequiredService<BackupService>().CreateAsync(BackupOrigin.Manual)).IsSuccess);
        Assert.Equal(1, await services.GetRequiredService<CloudBackupService>().UploadDueAsync("1.2.3"));
    }

    [Fact]
    public void The_screen_opens_with_the_cloud_restore_permission_which_no_license_state_locks()
    {
        var screen = Assert.Single(new DesktopScreens().GetScreens(), s => s.Id == "backup.cloud");

        Assert.Equal(CloudBackupCapabilities.Restore, screen.RequiredCapability);
        Assert.Equal(ScreenGroups.Administration, screen.Group);
        Assert.Equal(LicenseRequirement.None, CloudBackupCapabilities.All.Single(c => c.Code == CloudBackupCapabilities.Restore).License);
        Assert.Equal(CloudBackupText.ScreenTitle, screen.Title());
    }

    [Fact]
    public async Task The_owner_sets_cloud_backup_up_a_backup_goes_up_and_comes_back_through_the_local_restore()
    {
        var (vm, services) = Pc(Everything);
        await vm.OnNavigatedToAsync();
        Assert.True(vm.CanManage && vm.CanRestore);
        Assert.Equal(CloudBackupText.StateNoCode, vm.StateText);
        Assert.Equal(CloudBackupText.MakeCode, vm.MakeCodeText);

        await SetUpCodeAsync(vm);
        Assert.Equal(CloudBackupText.CodeConfirmed, vm.StatusMessage);
        Assert.Equal(CloudBackupText.StateNoAccess, vm.StateText);
        Assert.Equal(CloudBackupText.MakeNewCode, vm.MakeCodeText);

        vm.AccessKeyInput = "gpb_ui-test-access-key-0123456789";
        await Run(vm, vm.SaveAccessCommand);
        Assert.Equal(string.Empty, vm.AccessKeyInput);
        Assert.Equal(CloudBackupText.StateReady, vm.StateText);
        Assert.Equal(CloudBackupText.AccessEntered, vm.AccessStateText);
        Assert.Equal(CloudBackupText.WaitingNone, vm.WaitingText);

        await BackUpAndUploadAsync(services);
        await Run(vm, vm.RefreshCloudCommand);
        vm.Selected = Assert.Single(vm.CloudBackups);
        Assert.Contains("genericpos-", vm.Selected.NameText);

        await Run(vm, vm.RestoreCommand);
        Assert.True(vm.IsRestorePrepared, vm.ErrorMessage);
        Assert.Contains(vm.Selected!.Item.Label!, vm.RestoreQuestion);
        Assert.Empty(Directory.GetFiles(services.GetRequiredService<CloudBackupWorkspace>().DownloadDirectory));   // the plain copy is gone at once

        await Run(vm, vm.ConfirmRestoreCommand);
        Assert.True(vm.IsRestorePending);
        Assert.Contains(vm.CloudBackups[0].Item.Label!, vm.PendingText);   // the backup's own name, not a temporary file name
        await Run(vm, vm.RestartNowCommand);
        Assert.Equal(1, _restarter.Restarts);
        Assert.NotNull(await services.GetRequiredService<RestoreService>().GetPendingAsync());
    }

    [Fact]
    public async Task On_a_new_pc_the_screen_asks_for_the_recovery_code_and_then_restores()
    {
        var (owner, ownerServices) = Pc(Everything);
        await owner.OnNavigatedToAsync();
        var code = await SetUpCodeAsync(owner);
        owner.AccessKeyInput = "gpb_ui-test-access-key-0123456789";
        await Run(owner, owner.SaveAccessCommand);
        await BackUpAndUploadAsync(ownerServices);

        var (newPc, _) = Pc(Everything);
        await newPc.OnNavigatedToAsync();
        newPc.AccessKeyInput = "gpb_ui-test-access-key-0123456789";
        await Run(newPc, newPc.SaveAccessCommand);
        newPc.Selected = Assert.Single(newPc.CloudBackups);

        await Run(newPc, newPc.RestoreCommand);
        Assert.True(newPc.IsCodeNeeded);
        Assert.False(newPc.IsRestorePrepared);
        Assert.Equal("Type the recovery code this backup was made with.", newPc.ErrorMessage);

        newPc.RecoveryCodeInput = code;
        await Run(newPc, newPc.RestoreCommand);
        Assert.True(newPc.IsRestorePrepared, newPc.ErrorMessage);
        Assert.False(newPc.IsCodeNeeded);
        Assert.Equal(string.Empty, newPc.RecoveryCodeInput);
    }

    /// <summary>What AdminPortal (approval after the identity check) and the vendor's offline tool do with the owner's request.</summary>
    private string VendorRestoreKey(string requestText)
    {
        Assert.True(Security.BackupEnvelope.EscrowRecovery.TryReadRequest(requestText, out var request, out _));
        var approved = new Security.BackupEnvelope.ApprovedRecovery(1, request!, Guid.NewGuid(), "C-1", DateTimeOffset.UtcNow, "vendor-admin", "callback");
        using var escrow = Security.BackupEnvelope.Recovery.EscrowPrivateKey.FromPem("escrow-2026", new string(PemEncoding.Write("PRIVATE KEY", _vendor.ExportPkcs8PrivateKey())));
        return Security.BackupEnvelope.EscrowRecovery.WriteRestoreKey(Security.BackupEnvelope.Recovery.RestoreKeyIssuer.Issue(approved, [escrow], DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Without_the_code_a_new_pc_asks_the_vendor_restores_with_the_restore_key_and_is_then_asked_for_a_new_code()
    {
        var (owner, ownerServices) = Pc(Everything);
        await owner.OnNavigatedToAsync();
        await SetUpCodeAsync(owner);
        owner.AccessKeyInput = "gpb_ui-test-access-key-0123456789";
        await Run(owner, owner.SaveAccessCommand);
        await BackUpAndUploadAsync(ownerServices);

        // the PC died and the code is lost: a new PC, the access key typed in again, no recovery code
        var (newPc, services) = Pc(Everything);
        await newPc.OnNavigatedToAsync();
        newPc.AccessKeyInput = "gpb_ui-test-access-key-0123456789";
        await Run(newPc, newPc.SaveAccessCommand);
        newPc.Selected = Assert.Single(newPc.CloudBackups);
        Assert.False(newPc.AskVendorCommand.CanExecute(null));   // offered only once the code turns out to be needed
        await Run(newPc, newPc.RestoreCommand);
        Assert.True(newPc.IsCodeNeeded);

        await Run(newPc, newPc.AskVendorCommand);
        Assert.True(newPc.IsRecoveryRequestShown, newPc.ErrorMessage);
        Assert.Contains("-----BEGIN GENERICPOS RECOVERY REQUEST-----", newPc.RecoveryRequestText);
        Assert.Equal(CloudBackupText.RequestMade, newPc.StatusMessage);
        newPc.RequestSaved(@"C:\Users\owner\Desktop\recovery-request.txt");
        Assert.Contains("recovery-request.txt", newPc.StatusMessage);

        var restoreKey = VendorRestoreKey(newPc.RecoveryRequestText);
        Assert.False(newPc.UseRestoreKeyCommand.CanExecute(null));
        newPc.RestoreKeyInput = "> " + restoreKey.Replace("\r\n", "\n> ");   // as quoted in a mail reply
        await Run(newPc, newPc.UseRestoreKeyCommand);
        Assert.True(newPc.IsRestorePrepared, newPc.ErrorMessage);
        Assert.Contains(newPc.Selected!.Item.Label!, newPc.RestoreQuestion);
        Assert.Empty(Directory.GetFiles(services.GetRequiredService<CloudBackupWorkspace>().DownloadDirectory));

        // called off before the restart: nothing is asked for, the key still works
        await Run(newPc, newPc.ConfirmRestoreCommand);
        Assert.True(newPc.IsRestorePending);
        Assert.Equal(string.Empty, newPc.RestoreKeyInput);
        Assert.True(await services.GetRequiredService<CloudBackupService>().NeedsNewRecoveryCodeAsync());
        await Run(newPc, newPc.CancelRestoreCommand);
        Assert.False(await services.GetRequiredService<CloudBackupService>().NeedsNewRecoveryCodeAsync());

        newPc.RestoreKeyInput = restoreKey;
        await Run(newPc, newPc.UseRestoreKeyCommand);
        await Run(newPc, newPc.ConfirmRestoreCommand);
        await Run(newPc, newPc.RestartNowCommand);
        Assert.NotNull(await services.GetRequiredService<RestoreService>().GetPendingAsync());

        // after the restart the screen asks for a new recovery code until one is made
        await newPc.OnNavigatedToAsync();
        Assert.True(newPc.NeedsNewCode);
        await SetUpCodeAsync(newPc);
        Assert.False(newPc.NeedsNewCode);
    }

    [Fact]
    public async Task A_restore_key_that_is_not_one_or_was_not_asked_for_here_is_refused_in_plain_words()
    {
        var (vm, _) = Pc(Everything);
        await vm.OnNavigatedToAsync();
        vm.AccessKeyInput = "gpb_ui-test-access-key-0123456789";
        await Run(vm, vm.SaveAccessCommand);

        vm.RestoreKeyInput = "thanks, here it is";
        await Run(vm, vm.UseRestoreKeyCommand);
        Assert.False(vm.IsRestorePrepared);
        Assert.Contains("No restore key was found", vm.ErrorMessage);
    }

    [Fact]
    public async Task Who_sees_what()
    {
        var (restoreOnly, _) = Pc(CloudBackupCapabilities.Restore, BackupCapabilities.Restore);
        await restoreOnly.OnNavigatedToAsync();
        Assert.False(restoreOnly.CanManage);
        Assert.True(restoreOnly.CanRestore);
        Assert.False(restoreOnly.MakeCodeCommand.CanExecute(null));
        Assert.False(restoreOnly.SaveAccessCommand.CanExecute(null));

        var (noLocalRestore, _) = Pc(CloudBackupCapabilities.Restore);
        await noLocalRestore.OnNavigatedToAsync();
        Assert.False(noLocalRestore.CanRestore);   // a cloud restore is a local restore too: it needs backup.restore as well
        Assert.Empty(noLocalRestore.CloudBackups);
    }

    // ------------------------------------------------------------------ fakes

    private sealed class LicensedGate : IUnattendedWorkGate
    {
        public bool IsLicensed(string capability) => true;
    }

    private sealed class CountingRestarter : IApplicationRestarter
    {
        public int Restarts { get; private set; }

        public void Restart() => Restarts++;
    }

    /// <summary>Reversible and purpose-bound (like DPAPI for one Windows user); enough for a key ring in a test folder.</summary>
    private sealed class TestProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext, string purpose)
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
            return [.. key[..8], .. plaintext.ToArray().Select((b, i) => (byte)(b ^ key[i % key.Length]))];
        }

        public bool TryUnprotect(ReadOnlySpan<byte> protectedData, string purpose, out byte[] plaintext)
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(purpose));
            plaintext = [];
            if (protectedData.Length < 8 || !protectedData[..8].SequenceEqual(key.AsSpan(0, 8))) return false;
            plaintext = protectedData[8..].ToArray().Select((b, i) => (byte)(b ^ key[i % key.Length])).ToArray();
            return true;
        }
    }

    /// <summary>The vendor's backup server in memory, shared by both "PCs" (same license, same access key).</summary>
    private sealed class InMemoryBackupServer : IBackupClient
    {
        private readonly Dictionary<Guid, (byte[] Bytes, CloudBackupItem Item)> _stored = [];

        public async Task<Result<CloudBackupItem>> UploadAsync(string access, Stream content, string sha256, string label, string clientVersion, CancellationToken cancellationToken = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var item = new CloudBackupItem(Guid.NewGuid(), DateTimeOffset.UtcNow, buffer.Length, sha256, label);
            _stored[item.Id] = (buffer.ToArray(), item);
            return item;
        }

        public Task<Result<IReadOnlyList<CloudBackupItem>>> ListAsync(string access, CancellationToken cancellationToken = default)
            => Task.FromResult(Result.Success<IReadOnlyList<CloudBackupItem>>(_stored.Values.Select(s => s.Item).ToList()));

        public Task<Result<Stream>> DownloadAsync(string access, Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_stored.TryGetValue(id, out var s) ? Result.Success<Stream>(new MemoryStream(s.Bytes)) : Result.Failure<Stream>(Error.NotFound(BackupClientErrorCodes.NotFound, "none")));

        public Task<Result> DeleteAsync(string access, Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(_stored.Remove(id) ? Result.Success() : Result.Failure(Error.NotFound(BackupClientErrorCodes.NotFound, "none")));
    }
}
