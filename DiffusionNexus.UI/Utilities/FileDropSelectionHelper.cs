using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Pure helpers behind the file-drop dialog's selection handling, kept outside the Avalonia
/// code-behind so they can be unit tested.
/// </summary>
public static class FileDropSelectionHelper
{
    /// <summary>
    /// Appends <paramref name="incoming"/> to <paramref name="alreadySelected"/>, preserving order
    /// and dropping blank entries and case-insensitive duplicates.
    /// </summary>
    public static List<string> MergeDistinct(IEnumerable<string> alreadySelected, IEnumerable<string> incoming)
    {
        ArgumentNullException.ThrowIfNull(alreadySelected);
        ArgumentNullException.ThrowIfNull(incoming);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();

        foreach (var path in alreadySelected.Concat(incoming))
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (seen.Add(path)) merged.Add(path);
        }

        return merged;
    }

    /// <summary>
    /// Wording for the drop dialog's notice about ZIP entries that could not be extracted.
    /// Returns <see langword="null"/> when nothing was skipped.
    /// </summary>
    public static string? BuildSkippedEntriesNotice(IReadOnlyList<(string ArchiveName, int Count)> skippedByArchive)
    {
        ArgumentNullException.ThrowIfNull(skippedByArchive);

        var affected = skippedByArchive.Where(s => s.Count > 0).ToList();
        var total = affected.Sum(s => s.Count);
        if (total == 0) return null;

        if (affected.Count == 1)
        {
            var (name, count) = affected[0];
            return count == 1
                ? $"1 entry in {name} could not be extracted and was skipped."
                : $"{count} entries in {name} could not be extracted and were skipped.";
        }

        var breakdown = string.Join(", ", affected.Select(s => $"{s.ArchiveName}: {s.Count}"));
        return $"{total} entries could not be extracted and were skipped ({breakdown}).";
    }

    /// <summary>
    /// Builds the final list of files to import from a confirmed conflict resolution: every
    /// non-conflicting file plus the conflicting files the user chose to override or rename.
    /// </summary>
    public static List<string> BuildFinalFileList(
        FileConflictResolutionResult resolution,
        IEnumerable<string> nonConflictingFiles)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(nonConflictingFiles);

        var files = new List<string>(nonConflictingFiles);

        foreach (var conflict in resolution.Conflicts)
        {
            if (conflict.Resolution is FileConflictResolution.Override or FileConflictResolution.Rename)
                files.Add(conflict.NewFilePath);
        }

        return files;
    }
}
