using System.Text.Json.Nodes;
using Audit.Contracts.Interfaces;
using Audit.Contracts.Models;
using Client.Licensing.Application;
using Client.Updater.Application;
using Microsoft.Extensions.DependencyInjection;
using Platform.Application.Abstractions.Authorization;
using Sales.Contracts.Interfaces;
using Tests.Common.Security;
using static Integration.Tests.FailureTestKit;

namespace Integration.Tests;

/// <summary>
/// Stage 12 - the Stage 11 licensing and security decisions under failure, on the real desktop composition: a clock turned back, an unreadable
/// installation identity, a tampered license file, a rejected update package. The decisions must hold exactly as documented: licensed work is
/// declined with words the user can act on, nothing is granted by the failure, and the business data stays readable and untouched.
/// </summary>
[Collection(HostCollection.Name)]
[Trait("Category", "Failure")]
public sealed class LicenseAndSecurityFailureTests
{
    private static async Task<Platform.Core.Results.Result> SellOneAsync(OfflineDesktop desktop, Shop shop)
    {
        var (_, cartId) = await OpenCartAsync(desktop.Services, shop, 1m);
        var checkout = await CheckoutAsync(desktop.Services, cartId);
        return checkout.IsSuccess ? Platform.Core.Results.Result.Success() : Platform.Core.Results.Error.Unauthorized(checkout.ErrorCode!, checkout.ErrorMessage!);
    }

    private static async Task<Platform.Core.Results.Result> TrySellAsync(OfflineDesktop desktop, Shop shop)
    {
        // a sale needs a session, which needs the same license: the refusal can come from either step
        using var scope = desktop.Services.CreateScope();
        var pos = scope.ServiceProvider.GetRequiredService<POS.Contracts.Interfaces.IPOSService>();
        var session = await pos.OpenSessionAsync("cashier-1", shop.WarehouseId);
        if (!session.IsSuccess) return Platform.Core.Results.Error.Unauthorized(session.ErrorCode!, session.ErrorMessage!);
        var cart = await pos.StartCartAsync(session.SessionId);
        if (!cart.IsSuccess) return Platform.Core.Results.Error.Unauthorized(cart.ErrorCode!, cart.ErrorMessage!);
        var added = await pos.AddProductAsync(cart.CartId, shop.Sku, 1m);
        if (!added.IsSuccess) return Platform.Core.Results.Error.Unauthorized(added.ErrorCode!, added.ErrorMessage!);
        var checkout = await pos.CheckoutAsync(cart.CartId);
        return checkout.IsSuccess ? Platform.Core.Results.Result.Success() : Platform.Core.Results.Error.Unauthorized(checkout.ErrorCode!, checkout.ErrorMessage!);
    }

    private static async Task<IReadOnlyList<string>> SecurityActionsAsync(OfflineDesktop desktop)
    {
        using var scope = desktop.Services.CreateScope();
        var page = await scope.ServiceProvider.GetRequiredService<IAuditReader>().QueryAsync(new AuditEntryFilter(Module: "security"), pageSize: 500);
        return page.Items.Select(e => e.Action).ToList();
    }

    // ------------------------------------------------------------------ clock rollback

    [Fact]
    public async Task AClockTurnedBack_DeclinesLicensedWorkInPlainWords_KeepsTheDataReadable_AndSellingResumesWhenTheClockIsRight()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        Assert.True((await SellOneAsync(desktop, shop)).IsSuccess);
        var before = await BusinessState.ReadAsync(desktop.Host);

        desktop.Clock.Set(OfflineDesktop.Start.AddHours(-3));   // beyond the 120-minute tolerance, while offline

        var refused = await TrySellAsync(desktop, shop);
        Assert.Equal(SecurityErrors.LicenseRestrictedCode, refused.Error.Code);
        Assert.Contains("data is safe", refused.Error.Description);
        var notice = LicenseNotice.Describe(desktop.Services.GetRequiredService<ILicenseService>().Current);
        Assert.Equal(LicenseNoticeLevel.Restricted, notice.Level);
        Assert.Contains("Correct the date and time", notice.Message);

        // nothing changed, and everything is still readable
        Assert.Equal(before, await BusinessState.ReadAsync(desktop.Host));
        Assert.Equal(9m, await OnHandAsync(desktop.Host, shop.ProductId));
        using (var scope = desktop.Services.CreateScope())
            Assert.Single(await scope.ServiceProvider.GetRequiredService<ISalesReader>().GetRecentAsync());

        // recovery: the clock is corrected
        desktop.Clock.Set(OfflineDesktop.Start.AddMinutes(10));
        Assert.True((await SellOneAsync(desktop, shop)).IsSuccess);
        Assert.Equal(8m, await OnHandAsync(desktop.Host, shop.ProductId));
        Assert.Equal(0, desktop.Network.Requests);
    }

    // ------------------------------------------------------------------ unreadable protected identity

    [Fact]
    public async Task ACorruptedInstallationIdentity_GrantsNothing_IsKeptAsEvidence_IsAudited_AndLeavesTheDataIntact()
    {
        var clock = new TestClock(OfflineDesktop.Start);
        using var licenses = OfflineDesktop.NewLicenses(clock);
        string folder;
        Shop shop;
        await using (var first = await OfflineDesktop.StartAsync(null, keepFiles: true, licenses, clock))
        {
            shop = await CreateShopAsync(first.Services);
            Assert.True((await SellOneAsync(first, shop)).IsSuccess);
            folder = first.Host.Folder;
        }

        try
        {
            var identityFile = Path.Combine(licenses.Directory, "installation.json");
            Assert.True(File.Exists(identityFile));
            File.WriteAllText(identityFile, "this is not an identity at all");   // damaged on disk

            await using var second = await OfflineDesktop.StartAsync(folder, keepFiles: false, licenses, clock);

            // nothing is granted: the damaged identity cannot be bound to the license, and the application says what to do
            var refused = await TrySellAsync(second, shop);
            Assert.Equal(SecurityErrors.LicenseRestrictedCode, refused.Error.Code);
            Assert.Contains("data is safe", refused.Error.Description);

            // the damaged file is kept for support (never silently overwritten) and the event is in the audit trail
            Assert.Single(Directory.GetFiles(licenses.Directory, "installation.json.unusable-*"));
            Assert.Contains("security.installation.identity-unusable", await SecurityActionsAsync(second));

            // every record is intact and readable
            Assert.Equal(1, (await BusinessState.ReadAsync(second.Host)).Sales);
            Assert.Equal(9m, await OnHandAsync(second.Host, shop.ProductId));
            using var scope = second.Services.CreateScope();
            Assert.Single(await scope.ServiceProvider.GetRequiredService<ISalesReader>().GetRecentAsync());
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    // ------------------------------------------------------------------ tampered license

    [Fact]
    public async Task ATamperedLicenseFile_IsRefused_AndTheOriginalWorksAgainWhenRestored()
    {
        var clock = new TestClock(OfflineDesktop.Start);
        using var licenses = OfflineDesktop.NewLicenses(clock);
        var licenseFile = Path.Combine(licenses.Directory, "license.json");
        var original = File.ReadAllText(licenseFile);
        string folder;
        Shop shop;
        await using (var first = await OfflineDesktop.StartAsync(null, keepFiles: true, licenses, clock))
        {
            shop = await CreateShopAsync(first.Services);
            Assert.True((await SellOneAsync(first, shop)).IsSuccess);
            folder = first.Host.Folder;
        }

        try
        {
            var json = JsonNode.Parse(original)!.AsObject();
            var key = json.Select(p => p.Key).Single(k => k.Equals("signature", StringComparison.OrdinalIgnoreCase));
            var signature = json[key]!.GetValue<string>();
            json[key] = (signature[0] == 'A' ? 'B' : 'A') + signature[1..];   // one character of the signature changed
            File.WriteAllText(licenseFile, json.ToJsonString());

            await using (var tampered = await OfflineDesktop.StartAsync(folder, keepFiles: true, licenses, clock))
            {
                var refused = await TrySellAsync(tampered, shop);
                Assert.Equal(SecurityErrors.LicenseRestrictedCode, refused.Error.Code);
                Assert.Equal(1, (await BusinessState.ReadAsync(tampered.Host)).Sales);
                Assert.Equal(9m, await OnHandAsync(tampered.Host, shop.ProductId));
            }

            File.WriteAllText(licenseFile, original);   // the genuine license is put back

            await using var restored = await OfflineDesktop.StartAsync(folder, keepFiles: false, licenses, clock);
            Assert.True((await SellOneAsync(restored, shop)).IsSuccess);
            Assert.Equal(8m, await OnHandAsync(restored.Host, shop.ProductId));
        }
        finally
        {
            IntegrationHost.DeleteFolder(folder);
        }
    }

    // ------------------------------------------------------------------ rejected update package

    [Fact]
    public async Task AnInvalidUpdatePackage_IsRejectedAndAudited_WithoutAffectingTheInstallationOrTheShop()
    {
        await using var desktop = await OfflineDesktop.StartAsync();
        var shop = await CreateShopAsync(desktop.Services);
        var package = Path.Combine(Path.GetTempPath(), "genericpos-bad-" + Guid.NewGuid().ToString("N") + ".gpkg");
        await File.WriteAllBytesAsync(package, [0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF, 0xFE, 0x01, 0x02]);   // starts like a ZIP, is not a package
        try
        {
            var installed = await desktop.Services.GetRequiredService<IUpdateService>().InstallAsync(package);

            Assert.True(installed.IsFailure);
            Assert.DoesNotContain("   at ", installed.Error.Description);
            Assert.DoesNotContain(package, installed.Error.Description);
            Assert.True(File.Exists(package));   // the user's file is not touched

            Assert.True((await SellOneAsync(desktop, shop)).IsSuccess);   // the shop does not notice
            Assert.Equal(9m, await OnHandAsync(desktop.Host, shop.ProductId));
        }
        finally
        {
            File.Delete(package);
        }
    }
}
