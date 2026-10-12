using BackupServer.Application;
using Cloud.Contracts;
using Cloud.Contracts.Admin;
using Security.BackupEnvelope;

namespace AdminPortal.Application;

/// <summary>
/// MISS-04f, design 5B step 2: the vendor approves an owner's recovery request after the written identity check.
///
///   - the identity check (how, with whom, notes) is required and goes into the admin audit log with the outcome;
///   - the request's escrow slot and key id must be those of the STORED backup's authenticated header, and the license the request names
///     must own that backup, so a slot copied from another shop's backup cannot be smuggled in;
///   - the answer is an approved-recovery TEXT for the offline tool. The server holds no escrow key and opens nothing.
/// Refusals after the identity fields were given are audited too.
/// </summary>
public sealed class EscrowRecoveryAdminService(BackupService backups, AdminAuditRecorder audit, TimeProvider clock)
{
    public const int MaxFieldLength = 400;
    public const string ApprovedAction = "backup.escrow-recovery-approved";
    public const string RefusedAction = "backup.escrow-recovery-refused";

    public async Task<ServiceResult<EscrowRecoveryApprovalResponse>> ApproveAsync(
        AdminActor actor, Guid backupId, EscrowRecoveryApprovalRequest body, CancellationToken cancellationToken = default)
    {
        var method = body.CheckMethod?.Trim();
        var contact = body.Contact?.Trim();
        var notes = string.IsNullOrWhiteSpace(body.Notes) ? null : body.Notes.Trim();
        if (string.IsNullOrEmpty(method) || string.IsNullOrEmpty(contact))
            return Fail(CloudErrorCodes.Validation, "Record how the identity was checked and who was spoken to: a recovery is never approved without a written identity check.");
        if (method.Length > MaxFieldLength || contact.Length > MaxFieldLength || notes is { Length: > MaxFieldLength })
            return Fail(CloudErrorCodes.Validation, $"The identity check fields may not exceed {MaxFieldLength} characters each.");

        if (!EscrowRecovery.TryReadRequest(body.Request, out var request, out var problem))
            return Fail(CloudErrorCodes.Validation, problem switch
            {
                RecoveryTextProblem.UnsupportedVersion => "The recovery request was made by a newer version of the desktop; update the administration tools.",
                RecoveryTextProblem.Damaged => "The recovery request is damaged (a character changed or lost). Ask the owner to send it again.",
                _ => "No recovery request was found in the text. Paste the owner's whole text, including the BEGIN and END lines.",
            });

        if (request!.BackupId != backupId)
            return Fail(CloudErrorCodes.Validation, $"The recovery request is for backup {request.BackupId}, not {backupId}.");

        var record = await backups.FindAnyAsync(backupId, cancellationToken);
        if (record is null)
            return Fail(CloudErrorCodes.NotFound, "Unknown backup.");

        var check = $"identity checked by {method} with {contact}{(notes is null ? "" : $" ({notes})")}";
        if (request.LicenseId is not null && (!Guid.TryParse(request.LicenseId, out var named) || named != record.LicenseId))
            return await RefuseAsync(actor, backupId, check, $"the request names license {request.LicenseId}, but the backup belongs to license {record.LicenseId}", cancellationToken);

        EnvelopeHeader header;
        var content = backups.OpenAnyContent(backupId);
        if (content is null)
            return Fail(CloudErrorCodes.NotFound, "The backup content is no longer available.");
        try
        {
            await using (content)
                header = BackupEnvelope.ReadHeader(content);
        }
        catch (BackupEnvelopeException)
        {
            return await RefuseAsync(actor, backupId, check, "the stored backup is not an encrypted cloud backup", cancellationToken);
        }

        if (!EscrowRecovery.Matches(request, header))
            return await RefuseAsync(actor, backupId, check, "the request's key id or escrow slot is not the stored backup's", cancellationToken);

        var approved = new ApprovedRecovery(EscrowRecovery.FormatVersion, request, record.LicenseId, record.CustomerId, clock.GetUtcNow(), actor.Name, method);
        await audit.RecordAsync(actor, ApprovedAction, "backup", backupId.ToString(), Limit(
            $"Escrow recovery approved for license {record.LicenseId} (customer {record.CustomerId}), shop key {request.KeyId}, escrow key {request.Escrow.KeyId}, request {request.RequestId:N}; {check}."),
            cancellationToken);

        return ServiceResult<EscrowRecoveryApprovalResponse>.Ok(
            new EscrowRecoveryApprovalResponse(backupId, record.LicenseId, record.CustomerId, EscrowRecovery.WriteApproved(approved)));
    }

    private async Task<ServiceResult<EscrowRecoveryApprovalResponse>> RefuseAsync(AdminActor actor, Guid backupId, string check, string reason, CancellationToken cancellationToken)
    {
        await audit.RecordAsync(actor, RefusedAction, "backup", backupId.ToString(), Limit($"Escrow recovery refused: {reason}; {check}."), cancellationToken);
        return Fail(CloudErrorCodes.Conflict, $"The recovery request does not match the stored backup: {reason}. Nothing was approved.");
    }

    private static string Limit(string summary) => summary.Length <= 2000 ? summary : summary[..1997] + "...";

    private static ServiceResult<EscrowRecoveryApprovalResponse> Fail(string code, string message) => ServiceResult<EscrowRecoveryApprovalResponse>.Fail(code, message);
}
