using System.IO.Compression;

namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Where a file that is about to be imported originally came from when it was pulled out of an
/// archive: the archive on disk and the entry's full name inside it (folders included, forward
/// slashes as stored in the ZIP).
/// </summary>
public sealed record ArchiveOrigin(string ArchivePath, string EntryName);

/// <summary>
/// Outcome of extracting the media entries of a ZIP archive into a temporary directory.
/// </summary>
public sealed class ZipExtractionResult
{
    public static readonly ZipExtractionResult Empty = new(null, null, [], new Dictionary<string, string>(), 0);

    public ZipExtractionResult(
        string? tempDirectory,
        string? archivePath,
        IReadOnlyList<string> extractedFiles,
        IReadOnlyDictionary<string, string> entryNames,
        int skippedEntryCount)
    {
        TempDirectory = tempDirectory;
        ArchivePath = archivePath;
        ExtractedFiles = extractedFiles;
        EntryNames = entryNames;
        SkippedEntryCount = skippedEntryCount;
    }

    /// <summary>
    /// The temporary directory holding the extracted files, or <see langword="null"/> when
    /// nothing was extracted. The caller owns this directory and must delete it once the
    /// extracted files have been consumed.
    /// </summary>
    public string? TempDirectory { get; }

    /// <summary>
    /// The archive the files were extracted from, or <see langword="null"/> when nothing was extracted.
    /// </summary>
    public string? ArchivePath { get; }

    /// <summary>
    /// Full paths of the extracted files, in archive order.
    /// </summary>
    public IReadOnlyList<string> ExtractedFiles { get; }

    /// <summary>
    /// Extracted file path → the entry's full name inside the archive (e.g. <c>day2/grok.jpg</c>).
    /// The extraction is flat, so this is the only place the folder inside the ZIP survives.
    /// </summary>
    public IReadOnlyDictionary<string, string> EntryNames { get; }

    /// <summary>
    /// Entries that matched the filter but could not be written (unusable name, disk error).
    /// The rest of the archive is still extracted.
    /// </summary>
    public int SkippedEntryCount { get; }

    /// <summary>
    /// The <see cref="ArchiveOrigin"/> of one extracted file, for conflict rows to display.
    /// </summary>
    public IEnumerable<KeyValuePair<string, ArchiveOrigin>> Origins =>
        ArchivePath is null
            ? []
            : EntryNames.Select(kv => new KeyValuePair<string, ArchiveOrigin>(kv.Key, new ArchiveOrigin(ArchivePath, kv.Value)));
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
    /// Extraction folders older than this are orphans (a dialog does not live for days) and are
    /// swept on the next extraction. Every caller deletes its folders deterministically once the
    /// import is done; this is the safety net for a crash mid-import.
    /// </summary>
    internal static readonly TimeSpan StaleRetention = TimeSpan.FromDays(2);

    /// <summary>
    /// Deletes extraction folders handed back by the file-drop dialog once their files have been
    /// copied to the destination. Only folders carrying our prefix are touched, so a caller that
    /// accidentally passes an unrelated path loses nothing. Missing folders and failures are
    /// ignored; a folder that resists deletion is picked up by the stale sweep later.
    /// </summary>
    /// <param name="directories">Folders reported via a dialog result's <c>TemporaryDirectories</c>.</param>
    public static void DeleteExtractionDirectories(IEnumerable<string> directories)
    {
        foreach (var dir in directories)
        {
            var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
            if (!leaf.StartsWith(TempDirectoryPrefix, StringComparison.Ordinal))
                continue;

            TryDelete(dir);
        }
    }

    /// <summary>
    /// Extracts every entry whose extension is in <paramref name="allowedExtensions"/> (archives are
    /// never extracted, even when listed) into a fresh temporary directory. Nested paths are
    /// flattened to the entry file name; colliding names get a numeric suffix. An entry that cannot
    /// be written is skipped and counted; an archive that cannot be opened at all yields
    /// <see cref="ZipExtractionResult.Empty"/> rather than throwing.
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

        var root = tempRoot ?? Path.GetTempPath();
        SweepStaleDirectories(root);

        var tempDir = Path.GetFullPath(Path.Combine(root, TempDirectoryPrefix + Guid.NewGuid().ToString("N")[..8]));
        var containment = tempDir + Path.DirectorySeparatorChar;

        var extracted = new List<string>();
        var entryNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;

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

                // entry.Name is only leaf-safe for Windows-made archives (.NET picks the splitter
                // from the archive's "made by" platform); a Unix-made archive may legally carry
                // backslashes or ".." segments, which Path.Combine would honour.
                var leaf = SafeLeafName(entry.Name);
                if (leaf is null)
                {
                    skipped++;
                    continue;
                }

                var destPath = UniquePath(tempDir, leaf);
                if (!Path.GetFullPath(destPath).StartsWith(containment, StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    entry.ExtractToFile(destPath);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                {
                    // One unwritable entry must not cost the user the rest of the archive.
                    skipped++;
                    continue;
                }

                extracted.Add(destPath);
                entryNames[destPath] = entry.FullName;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // The archive itself could not be opened or read. Whatever came out before the
            // failure is still usable; the drop-zone analysis already flags corrupt archives.
        }

        if (extracted.Count == 0)
        {
            TryDelete(tempDir);
            return new ZipExtractionResult(null, null, [], new Dictionary<string, string>(), skipped);
        }

        return new ZipExtractionResult(tempDir, zipPath, extracted, entryNames, skipped);
    }

    /// <summary>
    /// Reduces an archive entry name to a bare file name, treating both separators as directory
    /// boundaries. Returns <see langword="null"/> when nothing usable remains (directory entries,
    /// <c>.</c>, <c>..</c>).
    /// </summary>
    public static string? SafeLeafName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return null;

        var normalized = entryName.Replace('\\', '/');
        var slash = normalized.LastIndexOf('/');
        var leaf = slash >= 0 ? normalized[(slash + 1)..] : normalized;

        // Windows drive-relative spellings like "C:evil.png" survive the slash split.
        var colon = leaf.LastIndexOf(':');
        if (colon >= 0) leaf = leaf[(colon + 1)..];

        leaf = leaf.Trim();
        if (leaf.Length == 0 || leaf == "." || leaf == "..") return null;
        return leaf;
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

    /// <summary>
    /// Deletes extraction folders under <paramref name="root"/> that are older than
    /// <see cref="StaleRetention"/>. Only folders carrying our prefix are ever touched.
    /// </summary>
    private static void SweepStaleDirectories(string root)
    {
        try
        {
            if (!Directory.Exists(root)) return;

            var cutoff = DateTime.UtcNow - StaleRetention;
            foreach (var dir in Directory.EnumerateDirectories(root, TempDirectoryPrefix + "*"))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(dir) < cutoff)
                        Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // In use or already gone; try again on the next extraction.
                }
            }
        }
        catch
        {
            // Sweeping is a courtesy; never let it block an extraction.
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
