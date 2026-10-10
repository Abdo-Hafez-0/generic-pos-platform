using Platform.Core.Results;

namespace CloudBackup.Contracts;

/// <summary>A backup the cloud keeps for this shop (the server never sees inside it).</summary>
public sealed record CloudBackupItem(Guid Id, DateTimeOffset CreatedAt, long SizeBytes, string Sha256, string? Label);

/// <summary>
/// The transport to the vendor's backup server (BackupServer.Api). Implemented OUTSIDE the module (Client.CloudBackup.Http: business
/// modules never reach HTTP, rule 7); without it, every call answers <see cref="BackupClientErrorCodes.Unavailable"/>. Every failure is a
/// plain result, never an exception. The access value is the per-license secret the vendor issued; it is passed per call and never kept by
/// the transport.
/// </summary>
public interface IBackupClient
{
    Task<Result<CloudBackupItem>> UploadAsync(string access, Stream content, string sha256, string label, string clientVersion, CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<CloudBackupItem>>> ListAsync(string access, CancellationToken cancellationToken = default);

    /// <summary>The encrypted bytes; the caller disposes the stream.</summary>
    Task<Result<Stream>> DownloadAsync(string access, Guid id, CancellationToken cancellationToken = default);

    Task<Result> DeleteAsync(string access, Guid id, CancellationToken cancellationToken = default);
}

public static class BackupClientErrorCodes
{
    /// <summary>No server configured, no network, or the server did not answer: try again later.</summary>
    public const string Unavailable = "CloudBackup.Server.Unavailable";

    /// <summary>The server refused the access value or the license (revoked, not entitled to cloud backup).</summary>
    public const string Refused = "CloudBackup.Server.Refused";

    /// <summary>The backup is larger than the server accepts.</summary>
    public const string TooLarge = "CloudBackup.Server.TooLarge";

    /// <summary>The server has no such backup.</summary>
    public const string NotFound = "CloudBackup.Server.NotFound";

    /// <summary>Any other answer.</summary>
    public const string Failed = "CloudBackup.Server.Failed";
}
