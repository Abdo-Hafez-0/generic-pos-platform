using System.Text.RegularExpressions;
using Security.BackupEnvelope;
using Security.BackupEnvelope.Recovery;

namespace Tools.BackupEscrowRecovery;

/// <summary>
/// The vendor's offline escrow tool (MISS-04f, design 5B). Runs on the offline machine that holds the escrow private keys; never on a server.
///
///   new-key  &lt;key-id&gt; &lt;key-folder&gt;                          make an escrow key pair; the private key stays in the folder,
///                                                            the public key is printed for the desktop's configuration
///   show     &lt;file&gt;                                        what a request, approved recovery or restore key is about
///   recover  &lt;approved-file&gt; &lt;key-folder&gt; [&lt;out-file&gt;]  approved recovery -> restore key for that ONE backup
///
/// Only an APPROVED recovery (AdminPortal: identity check recorded, slot compared with the stored backup) is accepted by recover.
/// Exit codes: 0 done, 1 refused (the reason is printed), 2 wrong usage.
/// </summary>
public static partial class EscrowRecoveryTool
{
    public const string KeyFileSuffix = ".escrow-key.pem";

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex KeyIdShape();

    public static int Run(string[] args, TextWriter output, TextWriter error, TimeProvider clock)
    {
        try
        {
            return args switch
            {
                ["new-key", var keyId, var folder] => NewKey(keyId, folder, output, error),
                ["show", var file] => Show(file, output, error),
                ["recover", var approved, var folder] => Recover(approved, folder, null, output, error, clock),
                ["recover", var approved, var folder, var outFile] => Recover(approved, folder, outFile, output, error, clock),
                _ => Usage(error),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine($"Refused: {ex.Message}");
            return 1;
        }
    }

    private static int Usage(TextWriter error)
    {
        error.WriteLine("Usage:");
        error.WriteLine("  BackupEscrowRecovery new-key <key-id> <key-folder>");
        error.WriteLine("  BackupEscrowRecovery show <file>");
        error.WriteLine("  BackupEscrowRecovery recover <approved-recovery-file> <key-folder> [<restore-key-file>]");
        return 2;
    }

    // ------------------------------------------------------------------ new-key

    private static int NewKey(string keyId, string folder, TextWriter output, TextWriter error)
    {
        if (!KeyIdShape().IsMatch(keyId))
        {
            error.WriteLine("Refused: a key id is 1 to 64 letters, digits, dots, dashes or underscores (for example escrow-2026).");
            return 1;
        }

        var path = Path.Combine(folder, keyId + KeyFileSuffix);
        if (File.Exists(path))
        {
            error.WriteLine($"Refused: {path} exists already. An escrow key is never replaced: backups sealed to it need it for as long as they exist.");
            return 1;
        }

        Directory.CreateDirectory(folder);
        using var key = EscrowPrivateKey.Generate(keyId);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(file))
            writer.Write(key.ExportPem());

        output.WriteLine($"Escrow key '{keyId}' made. The PRIVATE key is in {path}: keep it offline, add it to the key register, never copy it to a server.");
        output.WriteLine("Configure the desktop with the PUBLIC key:");
        output.WriteLine($"  CloudBackup:EscrowKeys:<n>:KeyId     = {keyId}");
        output.WriteLine($"  CloudBackup:EscrowKeys:<n>:PublicKey = {key.PublicKey.PublicKey}");
        return 0;
    }

    // ------------------------------------------------------------------ show

    private static int Show(string file, TextWriter output, TextWriter error)
    {
        var text = File.ReadAllText(file);
        if (EscrowRecovery.TryReadApproved(text, out var approved, out _))
        {
            output.WriteLine("Approved recovery");
            WriteRequest(approved!.Request, output);
            output.WriteLine($"  License:      {approved.LicenseId} (customer {approved.CustomerId})");
            output.WriteLine($"  Approved by:  {approved.ApprovedBy} at {approved.ApprovedAt:u} - identity checked by {approved.CheckMethod}");
            return 0;
        }

        if (EscrowRecovery.TryReadRequest(text, out var request, out _))
        {
            output.WriteLine("Recovery request (NOT approved yet: check the identity and approve it in AdminPortal first)");
            WriteRequest(request!, output);
            return 0;
        }

        if (EscrowRecovery.TryReadRestoreKey(text, out var key, out _))
        {
            output.WriteLine("Restore key");
            output.WriteLine($"  Backup id:  {key!.BackupId}");
            output.WriteLine($"  Request id: {key.RequestId}");
            output.WriteLine($"  Issued at:  {key.IssuedAt:u}");
            return 0;
        }

        error.WriteLine("Refused: the file holds no complete recovery request, approved recovery or restore key (copied only partly, or changed).");
        return 1;
    }

    private static void WriteRequest(RecoveryRequest request, TextWriter output)
    {
        output.WriteLine($"  Backup id:    {request.BackupId}");
        output.WriteLine($"  Backup made:  {request.BackupCreatedAt:u}{(request.BackupLabel is null ? "" : "  (" + request.BackupLabel + ")")}");
        output.WriteLine($"  Shop key id:  {request.KeyId}");
        output.WriteLine($"  Escrow key:   {request.Escrow.KeyId}");
        output.WriteLine($"  Asked by:     license {request.LicenseId ?? "(not activated)"} at {request.RequestedAt:u}");
    }

    // ------------------------------------------------------------------ recover

    private static int Recover(string approvedFile, string folder, string? outFile, TextWriter output, TextWriter error, TimeProvider clock)
    {
        var text = File.ReadAllText(approvedFile);
        if (!EscrowRecovery.TryReadApproved(text, out var approved, out var problem))
        {
            error.WriteLine(EscrowRecovery.TryReadRequest(text, out _, out _)
                ? "Refused: this is the owner's recovery request, not an approved recovery. Check who is asking and approve it in AdminPortal first; use the text AdminPortal returns."
                : problem == RecoveryTextProblem.UnsupportedVersion
                    ? "Refused: the approved recovery was made by a newer version of the system; use a newer tool."
                    : "Refused: the file holds no complete approved recovery (copied only partly, or changed).");
            return 1;
        }

        var keys = LoadKeys(folder, error);
        try
        {
            if (keys.Count == 0)
            {
                error.WriteLine($"Refused: no escrow key (*{KeyFileSuffix}) in {folder}.");
                return 1;
            }

            RestoreKey key;
            try
            {
                key = RestoreKeyIssuer.Issue(approved!, keys, clock.GetUtcNow());
            }
            catch (BackupEnvelopeException ex)
            {
                error.WriteLine($"Refused: {ex.Message}");
                return 1;
            }

            var target = outFile ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(approvedFile))!, $"restore-key-{approved!.Request.BackupId:N}.txt");
            File.WriteAllText(target, EscrowRecovery.WriteRestoreKey(key));

            output.WriteLine($"Restore key written to {target}");
            output.WriteLine($"  Backup id:   {key.BackupId} (made {approved!.Request.BackupCreatedAt:u})");
            output.WriteLine($"  License:     {approved.LicenseId} (customer {approved.CustomerId})");
            output.WriteLine($"  Approved by: {approved.ApprovedBy} at {approved.ApprovedAt:u} ({approved.CheckMethod})");
            output.WriteLine("Send it to the owner. It opens this one backup only, and only on the computer that made the request.");
            return 0;
        }
        finally
        {
            foreach (var k in keys) k.Dispose();
        }
    }

    /// <summary>Every &lt;key-id&gt;.escrow-key.pem in the folder (old keys stay: backups sealed to them may still exist).</summary>
    private static List<EscrowPrivateKey> LoadKeys(string folder, TextWriter error)
    {
        var keys = new List<EscrowPrivateKey>();
        if (!Directory.Exists(folder)) return keys;
        foreach (var path in Directory.EnumerateFiles(folder, "*" + KeyFileSuffix))
        {
            var keyId = Path.GetFileName(path)[..^KeyFileSuffix.Length];
            try
            {
                keys.Add(EscrowPrivateKey.FromPem(keyId, File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                error.WriteLine($"Warning: {path} is not a readable P-256 private key; skipped.");
            }
        }

        return keys;
    }
}
