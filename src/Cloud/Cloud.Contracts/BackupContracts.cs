namespace Cloud.Contracts.Backup;

/// <summary>Metadata of a stored backup. The server never looks inside the backup content.</summary>
public sealed record BackupDto(
    Guid BackupId, Guid LicenseId, string CustomerId, Guid InstallationId,
    DateTimeOffset CreatedAt, long SizeBytes, string Sha256, string? Label, string? ClientVersion);

/// <summary>Header/query names of the client backup API.</summary>
public static class BackupHeaders
{
    /// <summary>Optional client-computed SHA-256 (hex) of the uploaded bytes; the upload is rejected if it does not match.</summary>
    public const string Sha256 = "X-Backup-Sha256";

    /// <summary>Optional application version of the uploading client (informational).</summary>
    public const string ClientVersion = "X-Client-Version";
}
