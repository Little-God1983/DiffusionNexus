namespace DiffusionNexus.UI.Helpers;

/// <summary>
/// Formats a byte count into a human-readable size string (B/KB/MB/GB).
/// </summary>
/// <remarks>
/// Extracted from three byte-identical private copies (two in
/// <c>FileConflictDialogViewModel.cs</c>, one in <c>BackupCompareDialog.axaml.cs</c>)
/// found during the #442 housekeeping pass. There are other file-size formatters
/// elsewhere in the codebase (e.g. <c>CivitaiDownloadQueue.FormatBytes</c>) with
/// different rounding/precision rules; those are intentionally left alone since
/// unifying them would change displayed text.
/// </remarks>
internal static class FileSizeFormatter
{
    /// <summary>
    /// Formats <paramref name="bytes"/> as a size string.
    /// Uses one decimal place for KB/MB and two for GB.
    /// </summary>
    public static string Format(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024.0):F1} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
    }
}
