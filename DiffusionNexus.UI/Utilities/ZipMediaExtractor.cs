using System.IO.Compression;

namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Outcome of extracting the media entries of a ZIP archive into a temporary directory.
/// </summary>
public sealed class ZipExtractionResult
{
    public static readonly ZipExtractionResult Empty = new(null, []);

    public ZipExtractionResult(string? tempDirectory, IReadOnlyList<string> extractedFiles)
    {
        TempDirectory = tempDirectory;
        ExtractedFiles = extractedFiles;
    }

    /// <summary>
    /// The temporary directory holding the extracted files, or <see langword="null"/> when
    /// nothing was extracted. The caller owns this directory and must delete it once the
    /// extracted files have been consumed.
    /// </summary>
    public string? TempDirectory { get; }

    /// <summary>
    /// Full paths of the extracted files, in archive order.
    /// </summary>
    public IReadOnlyList<string> ExtractedFiles { get; }
}

/// <summary>
/// Extracts the media entries of a ZIP archive into a flat temporary directory so they can be
/// treated exactly like loose files dropped onto a dialog (extension filter, conflict detection,
/// import).
/// </summary>
public static class ZipMediaExtractor
{
    private const string TempDirectoryPrefix = "DiffusionNexus_ZipExtract_";
    private static readonly string[] ArchiveExtensions = [".zip"];

    /// <summary>
    /// Extracts every entry whose extension is in <paramref name="allowedExtensions"/> (archives are
    /// never extracted, even when listed) into a fresh temporary directory. Nested paths are
    /// flattened to the entry file name; colliding names get a numeric suffix.
    /// An unreadable archive yields <see cref="ZipExtractionResult.Empty"/> rather than throwing.
    /// </summary>
    /// <param name="zipPath">Path of the archive to read.</param>
    /// <param name="allowedExtensions">Lower-case extensions including the dot. Empty means "everything but archives".</param>
    /// <param name="tempRoot">Directory under which the temporary extraction directory is created; defaults to the system temp path.</param>
    public static ZipExtractionResult Extract(
        string zipPath,
        IReadOnlyCollection<string> allowedExtensions,
        string? tempRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(allowedExtensions);

        var allowed = allowedExtensions
            .Where(e => !ArchiveExtensions.Contains(e, StringComparer.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tempDir = Path.Combine(
            tempRoot ?? Path.GetTempPath(),
            TempDirectoryPrefix + Guid.NewGuid().ToString("N")[..8]);

        var extracted = new List<string>();

        try
        {
            Directory.CreateDirectory(tempDir);

            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var entry in archive.Entries)
            {
                // Directory entries have an empty Name.
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (ArchiveExtensions.Contains(ext))
                    continue;
                if (allowed.Count > 0 && !allowed.Contains(ext))
                    continue;

                var destPath = UniquePath(tempDir, entry.Name);
                entry.ExtractToFile(destPath);
                extracted.Add(destPath);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable archive is treated as "contains nothing usable". The dialog's
            // drop-zone analysis already flags such archives as invalid.
            extracted.Clear();
        }

        if (extracted.Count == 0)
        {
            TryDelete(tempDir);
            return ZipExtractionResult.Empty;
        }

        return new ZipExtractionResult(tempDir, extracted);
    }

    private static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var counter = 1; ; counter++)
        {
            candidate = Path.Combine(directory, $"{stem}_{counter}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best effort; a leftover empty temp directory is harmless.
        }
    }
}
