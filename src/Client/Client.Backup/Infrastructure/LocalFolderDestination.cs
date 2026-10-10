using Client.Backup.Application;
using Client.Backup.Domain;
using Microsoft.Extensions.Logging;
using Platform.Core.Results;

namespace Client.Backup.Infrastructure;

/// <summary>
/// A folder on this PC, a USB drive or a network share. A backup is first written as "name.part" and renamed when complete, so a
/// half-written file never carries a backup's name (a pulled USB stick leaves a ".part" file, never a broken "backup"). Existing files
/// are never overwritten: a second backup in the same second gets a "-2" suffix.
/// </summary>
public sealed class LocalFolderDestination(string folder, ILogger logger) : IBackupDestination
{
    private const string PartSuffix = ".part";

    public string Kind => BackupDestinations.Local;

    public string Folder { get; } = folder;

    public async Task<Result<string>> StoreAsync(string sourcePath, string fileName, CancellationToken cancellationToken = default)
    {
        string? part = null;
        try
        {
            Directory.CreateDirectory(Folder);
            var target = UniqueName(fileName);
            part = target + PartSuffix;

            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (var destination = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await source.CopyToAsync(destination, cancellationToken);
                await destination.FlushAsync(cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            File.Move(part, target, overwrite: false);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            logger.LogError(ex, "Writing a backup to {Folder} failed.", Folder);
            TryDelete(part);
            return Error.Failure(BackupErrorCodes.DestinationUnavailable,
                $"The backup could not be written to {Folder}. Check that the drive is connected and has free space. Your data was not changed.");
        }
    }

    public Task<Result<Stream>> OpenReadAsync(string location, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(location))
            return Task.FromResult(Result.Failure<Stream>(Missing(location)));

        try
        {
            Stream stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            return Task.FromResult(Result.Success(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Backup {Location} could not be opened.", location);
            return Task.FromResult(Result.Failure<Stream>(Error.Failure(BackupErrorCodes.DestinationUnavailable,
                $"The backup file {Path.GetFileName(location)} could not be opened.")));
        }
    }

    public Task<Result> DeleteAsync(string location, CancellationToken cancellationToken = default)
    {
        try
        {
            if (File.Exists(location))
            {
                File.Delete(location);
                return Task.FromResult(Result.Success());
            }

            // Gone already - unless the whole drive (a USB stick, a share) is missing: then the file may well still exist.
            var root = Path.GetPathRoot(location);
            return Task.FromResult(string.IsNullOrEmpty(root) || Directory.Exists(root)
                ? Result.Success()
                : Result.Failure(Missing(location)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Backup {Location} could not be deleted.", location);
            return Task.FromResult(Result.Failure(Error.Failure(BackupErrorCodes.DestinationUnavailable,
                $"The backup file {Path.GetFileName(location)} could not be deleted. It may be open in another program.")));
        }
    }

    private string UniqueName(string fileName)
    {
        var target = Path.Combine(Folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(target) || File.Exists(target + PartSuffix); n++)
            target = Path.Combine(Folder, $"{stem}-{n}{extension}");
        return target;
    }

    private static Error Missing(string location)
        => Error.NotFound(BackupErrorCodes.NotFound,
            $"The backup file {Path.GetFileName(location)} is no longer in {Path.GetDirectoryName(location)}. If it is on a USB drive, connect the drive and try again.");

    private void TryDelete(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the unfinished backup {Path}.", path);
        }
    }
}

public sealed class LocalDestinationFactory(ILogger<LocalFolderDestination> logger) : ILocalDestinationFactory
{
    public IBackupDestination Create(string folder) => new LocalFolderDestination(folder, logger);
}
