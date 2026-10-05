using System.Security.Cryptography;
using BackupServer.Application;
using UpdateServer.Application;
using Updates.Package;

namespace Cloud.Infrastructure.Storage;

/// <summary>Streams a request body into a staging file, enforcing a size limit and hashing as it goes.</summary>
internal static class StagingWriter
{
    public static async Task<(string Path, long Size, string Sha256)?> WriteAsync(
        string stagingDirectory, Stream input, long maxBytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(stagingDirectory);
        var path = Path.Combine(stagingDirectory, Guid.NewGuid().ToString("N") + ".part");

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;

            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        output.Close();
                        TryDelete(path);
                        return null;
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            return (path, total, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Best-effort delete. A file can be briefly locked (virus scanner, indexer), so a few short retries are made; a leftover is harmless and is swept by <see cref="CleanStale"/>.</summary>
    public static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
        }
    }

    /// <summary>Removes staging files left behind by an interrupted upload (older than <paramref name="age"/>).</summary>
    public static void CleanStale(string stagingDirectory, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(stagingDirectory)) return;

            foreach (var file in Directory.EnumerateFiles(stagingDirectory, "*.part"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > age)
                    TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // housekeeping only
        }
    }
}

/// <summary>Package bytes in a directory: <c>&lt;id&gt;.gpkg</c>, with uploads staged in <c>.staging</c> until they pass inspection.</summary>
public sealed class FilePackageStore : IPackageFileStore
{
    private readonly string directory;

    public FilePackageStore(string directory)
    {
        this.directory = directory;
        StagingWriter.CleanStale(StagingDirectory, TimeSpan.FromDays(1));
    }

    private string StagingDirectory => Path.Combine(directory, ".staging");

    private string FinalPath(Guid packageId) => Path.Combine(directory, packageId.ToString("N") + PackageFormat.Extension);

    public async Task<StagedFile?> StageAsync(Stream input, long maxBytes, CancellationToken cancellationToken = default)
    {
        var written = await StagingWriter.WriteAsync(StagingDirectory, input, maxBytes, cancellationToken);
        return written is null ? null : new StagedFile(written.Value.Path, written.Value.Size);
    }

    public void Commit(StagedFile staged, Guid packageId)
    {
        Directory.CreateDirectory(directory);
        File.Move(staged.Path, FinalPath(packageId), overwrite: false);
    }

    public void Discard(StagedFile staged) => StagingWriter.TryDelete(staged.Path);

    public void Delete(Guid packageId) => StagingWriter.TryDelete(FinalPath(packageId));

    public Stream? OpenRead(Guid packageId)
    {
        var path = FinalPath(packageId);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }
}

/// <summary>Backup bytes in a directory: <c>&lt;id&gt;.bak</c>, uploads staged in <c>.staging</c>. The content is opaque to the server.</summary>
public sealed class FileBackupBlobStore : IBackupBlobStore
{
    private readonly string directory;

    public FileBackupBlobStore(string directory)
    {
        this.directory = directory;
        StagingWriter.CleanStale(StagingDirectory, TimeSpan.FromDays(1));
    }

    private string StagingDirectory => Path.Combine(directory, ".staging");

    private string FinalPath(Guid backupId) => Path.Combine(directory, backupId.ToString("N") + ".bak");

    public async Task<StagedBlob?> StageAsync(Stream input, long maxBytes, CancellationToken cancellationToken = default)
    {
        var written = await StagingWriter.WriteAsync(StagingDirectory, input, maxBytes, cancellationToken);
        return written is null ? null : new StagedBlob(written.Value.Path, written.Value.Size, written.Value.Sha256);
    }

    public void Commit(StagedBlob staged, Guid backupId)
    {
        Directory.CreateDirectory(directory);
        File.Move(staged.Path, FinalPath(backupId), overwrite: false);
    }

    public void Discard(StagedBlob staged) => StagingWriter.TryDelete(staged.Path);

    public void Delete(Guid backupId) => StagingWriter.TryDelete(FinalPath(backupId));

    public Stream? OpenRead(Guid backupId)
    {
        var path = FinalPath(backupId);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }
}
