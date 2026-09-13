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
