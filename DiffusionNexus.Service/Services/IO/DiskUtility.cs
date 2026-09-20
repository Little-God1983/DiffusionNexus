using System;
using DiffusionNexus.Domain.Utilities;

namespace DiffusionNexus.Service.Services.IO;

/// <summary>
/// Provides helpers for working with file system paths and disk space.
/// </summary>
public class DiskUtility
{
    /// <summary>
    /// Determines whether enough free space exists on the volume holding <paramref name="targetPath"/>
    /// to copy all files from <paramref name="sourcePath"/>.
    /// </summary>
    /// <remarks>
    /// A target whose free space cannot be read is not a "no": it answers <see langword="true"/>,
    /// because refusing a copy to a share that simply will not report its size is the worse error.
    /// A target with no volume behind it is a "no". This used to let
    /// <see cref="DriveNotFoundException"/> out of a yes/no method, so the caller had to catch it
    /// to find out (issue #581).
    /// </remarks>
    public bool EnoughFreeSpace(string sourcePath, string targetPath)
    {
        long folderSize = GetDirectorySize(sourcePath);
        var space = DiskSpace.TryGetAvailableSpace(targetPath);

        return space.Kind switch
        {
            FreeSpaceKind.Unreachable => false,
            FreeSpaceKind.Unknown => true,
            _ => folderSize <= space.FreeBytes,
        };
    }

    /// <summary>
    /// Recursively calculates the size of a directory.
    /// </summary>
    public static long GetDirectorySize(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"The directory '{folderPath}' does not exist.");

        long size = 0;
        foreach (string file in Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories))
        {
            size += new FileInfo(file).Length;
        }
        return size;
    }

    /// <summary>
    /// Removes empty directories beneath the provided <paramref name="path"/>.
    /// </summary>
    public Task DeleteEmptyDirectoriesAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DeleteEmptyDirectories(path, cancellationToken), cancellationToken);
    }

    private static void DeleteEmptyDirectories(string path, CancellationToken token)
    {
        foreach (var directory in Directory.GetDirectories(path))
        {
            token.ThrowIfCancellationRequested();
            DeleteEmptyDirectories(directory, token);
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    /// <summary>
    /// Validates that a path is non-null and well formed.
    /// </summary>
    public bool IsValidPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        try
        {
            Path.GetFullPath(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
