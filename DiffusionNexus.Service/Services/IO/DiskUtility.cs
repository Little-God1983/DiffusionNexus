using System;

namespace DiffusionNexus.Service.Services.IO;

/// <summary>
/// Provides helpers for working with file system paths and disk space.
/// </summary>
public class DiskUtility
{
    /// <summary>
    /// Determines whether enough free space exists on the drive containing <paramref name="targetPath"/> to copy all files from <paramref name="sourcePath"/>.
    /// </summary>
    public bool EnoughFreeSpace(string sourcePath, string targetPath)
    {
        long folderSize = GetDirectorySize(sourcePath);
        long availableSpace = GetAvailableSpace(targetPath);
        return folderSize <= availableSpace;
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
    /// Gets the available free space for the drive containing the supplied path.
    /// </summary>
    public static long GetAvailableSpace(string folderPath)
    {
        var root = Path.GetPathRoot(folderPath);
        if (string.IsNullOrEmpty(root))
            throw new ArgumentException("Invalid path", nameof(folderPath));
        DriveInfo drive = new DriveInfo(root);
        return drive.AvailableFreeSpace;
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
