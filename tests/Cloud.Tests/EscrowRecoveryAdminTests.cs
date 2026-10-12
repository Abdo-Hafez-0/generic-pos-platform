using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using AdminPortal.Application;
using BackupServer.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Microsoft.AspNetCore.Mvc.Testing;
using Security.BackupEnvelope;

namespace Cloud.Tests;

/// <summary>
/// MISS-04f (design 5B step 2): the vendor approves an owner's recovery request in AdminPortal after the written identity check. The slot
/// must be the STORED backup's, the license must own it, the check is audited, and the answer is a text for the offline tool - no key.
/// </summary>
public sealed class EscrowRecoveryAdminTests : IDisposable
{
    private readonly CloudWorld _w = new();
    private readonly ECDiffieHellman _escrow = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDiffieHellman _pc = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _escrow.Dispose();
        _pc.Dispose();
        _w.Dispose();
    }

    private EscrowRecoveryAdminService Service => _w.Get<EscrowRecoveryAdminService>();

    private static readonly EscrowRecoveryApprovalRequest Checked = new(null, "callback to the phone number on the customer record", "Sara (owner)", "asked for the shop address");

    /// <summary>A customer with cloud backup whose encrypted backup is stored; returns the license and the stored backup's id and header.</summary>
    private async Task<(Guid LicenseId, Guid BackupId, EnvelopeHeader Header)> StoredAsync(string name = "Acme")
    {
        var license = await _w.NewLicenseAsync(["cloud-backup"], null, name);
        await _w.ActivateAsync(license);
        var token = ResultAssert.Ok(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, license.License.LicenseId)).Token;
        var principal = ResultAssert.Ok(await _w.BackupAccess.AuthenticateAsync(token));

        var database = new byte[50_000];
        RandomNumberGenerator.Fill(database);
        using var file = new MemoryStream();
        var header = await BackupEnvelope.WriteAsync(new MemoryStream(database), file, BackupKey.FromRecoveryCode(RecoveryCode.Generate().Secret),
            new EscrowPublicKey("escrow-2026", Convert.ToBase64String(_escrow.ExportSubjectPublicKeyInfo())),
            new EnvelopeMetadata("1.2.3", [], database.Length, Convert.ToHexStringLower(SHA256.HashData(database))), CloudWorld.Start);
        file.Position = 0;
        var record = ResultAssert.Ok(await _w.Backups.UploadAsync(principal, file, new BackupUploadInfo(null, "genericpos-20260601-120000.db", "1.2.3")));
        return (license.License.LicenseId, record.BackupId, header);
    }

    private string RequestText(EnvelopeHeader header, Guid backupId, Guid? licenseId)
        => EscrowRecovery.WriteRequest(EscrowRecovery.CreateRequest(header, backupId, "genericpos-20260601-120000.db", licenseId?.ToString(), _pc.ExportSubjectPublicKeyInfo(), CloudWorld.Start));

    private async Task<IReadOnlyList<AuditEntryDto>> AuditAsync(Guid backupId)
        => (await _w.Operations.QueryAuditAsync(new AuditFilter(null, null, "backup", backupId.ToString(), null, null), null, null)).Items;

    [Fact]
    public async Task A_checked_request_for_the_stored_backup_is_approved_audited_and_answered_with_a_text_for_the_offline_tool()
    {
        var (licenseId, backupId, header) = await StoredAsync();
        var request = RequestText(header, backupId, licenseId);

        var approved = ResultAssert.Ok(await Service.ApproveAsync(_w.Actor, backupId, Checked with { Request = request }));

        Assert.Equal(licenseId, approved.LicenseId);
        Assert.True(EscrowRecovery.TryReadApproved(approved.ApprovedRecovery, out var text, out _));
        Assert.Equal(backupId, text!.Request.BackupId);
        Assert.Equal(header.Escrow, text.Request.Escrow);
        Assert.Equal(_w.Actor.Name, text.ApprovedBy);
        Assert.Equal(Checked.CheckMethod, text.CheckMethod);

        var entry = Assert.Single(await AuditAsync(backupId));
        Assert.Equal(EscrowRecoveryAdminService.ApprovedAction, entry.Action);
        Assert.Contains("callback to the phone number", entry.Summary);
        Assert.Contains("Sara (owner)", entry.Summary);
        Assert.Contains(licenseId.ToString(), entry.Summary);

        // a request from a PC that is not activated (no license id) is approved for the backup's owner
        var anonymous = ResultAssert.Ok(await Service.ApproveAsync(_w.Actor, backupId, Checked with { Request = RequestText(header, backupId, null) }));
        Assert.Equal(licenseId, anonymous.LicenseId);
    }

    [Fact]
    public async Task Without_the_written_identity_check_nothing_is_approved()
    {
        var (licenseId, backupId, header) = await StoredAsync();
        var request = RequestText(header, backupId, licenseId);

        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, backupId, new EscrowRecoveryApprovalRequest(request, null, "Sara", null)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, backupId, new EscrowRecoveryApprovalRequest(request, "callback", " ", null)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, backupId, new EscrowRecoveryApprovalRequest(request, new string('x', 401), "Sara", null)), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, backupId, Checked with { Request = "please help" }), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, backupId, Checked with { Request = request.Replace("A", "B") }), CloudErrorCodes.Validation);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, Guid.NewGuid(), Checked with { Request = request }), CloudErrorCodes.Validation);
        Assert.Empty(await AuditAsync(backupId));
    }

    [Fact]
    public async Task A_slot_from_another_shops_backup_or_another_license_is_refused_and_the_attempt_is_audited()
    {
        var victim = await StoredAsync("Victim");
        var asker = await StoredAsync("Asker");

        // the asker sends the victim's slot (from a stolen .gpbak) under the asker's own backup id and license
        var smuggled = RequestText(victim.Header, asker.BackupId, asker.LicenseId);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, asker.BackupId, Checked with { Request = smuggled }), CloudErrorCodes.Conflict);

        // the asker names the victim's backup but its own license
        var foreign = RequestText(victim.Header, victim.BackupId, asker.LicenseId);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, victim.BackupId, Checked with { Request = foreign }), CloudErrorCodes.Conflict);

        Assert.Equal(EscrowRecoveryAdminService.RefusedAction, Assert.Single(await AuditAsync(asker.BackupId)).Action);
        Assert.Equal(EscrowRecoveryAdminService.RefusedAction, Assert.Single(await AuditAsync(victim.BackupId)).Action);
    }

    [Fact]
    public async Task A_stored_backup_that_is_not_an_encrypted_cloud_backup_cannot_be_recovered()
    {
        var license = await _w.NewLicenseAsync(["cloud-backup"], null, "Plain");
        await _w.ActivateAsync(license);
        var token = ResultAssert.Ok(await _w.Licenses.IssueBackupTokenAsync(_w.Actor, license.License.LicenseId)).Token;
        var principal = ResultAssert.Ok(await _w.BackupAccess.AuthenticateAsync(token));
        var record = ResultAssert.Ok(await _w.Backups.UploadAsync(principal, new MemoryStream("SQLite format 3\0 plain"u8.ToArray()), new BackupUploadInfo(null, null, null)));
        var (_, _, header) = await StoredAsync();

        var request = RequestText(header, record.BackupId, license.License.LicenseId);
        ResultAssert.Fails(await Service.ApproveAsync(_w.Actor, record.BackupId, Checked with { Request = request }), CloudErrorCodes.Conflict);
    }

    [Fact]
    public async Task The_endpoint_needs_the_admin_key_and_answers_the_approved_text()
    {
        var (licenseId, backupId, header) = await StoredAsync();
        using var host = ApiHosting.Host<AdminPortalApiMarker>(_w.Settings);
        using var admin = host.CreateClient().WithBearer(CloudWorld.AdminKey);
        using var anonymous = host.CreateClient();
        var body = Checked with { Request = RequestText(header, backupId, licenseId) };

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/admin/backups/{backupId}/escrow-recovery", body)).StatusCode);

        var approved = await (await admin.PostAsJsonAsync($"/api/admin/backups/{backupId}/escrow-recovery", body)).Json<EscrowRecoveryApprovalResponse>(HttpStatusCode.OK);
        Assert.Contains("-----BEGIN GENERICPOS APPROVED RECOVERY-----", approved.ApprovedRecovery);

        var refused = await admin.PostAsJsonAsync($"/api/admin/backups/{backupId}/escrow-recovery", body with { Contact = null });
        Assert.Equal(CloudErrorCodes.Validation, (await refused.Error(HttpStatusCode.BadRequest)).Code);
    }
}
