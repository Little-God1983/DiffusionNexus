using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Result of applying a single <see cref="FileEdit"/>.
/// </summary>
public record FixApplyResult
{
    /// <summary>
    /// Whether the edit was applied successfully.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// Path to the file that was (or should have been) modified.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Error message when <see cref="Success"/> is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Path to the backup file created before editing, if any.
    /// </summary>
    public string? BackupPath { get; init; }
}

/// <summary>
/// Applies <see cref="FixSuggestion"/> edits to files on disk.
/// Supports creating backups before modifying files and verifies
/// that file contents haven't changed since the analysis was run.
/// </summary>
public static class FixApplier
{
    /// <summary>
    /// Applies all <see cref="FileEdit"/>s in a <see cref="FixSuggestion"/>.
    /// Each file is backed up before modification (when <paramref name="createBackup"/> is true).
    /// If the original text is no longer found in the file, the edit is skipped with an error.
    /// </summary>
    /// <param name="suggestion">The fix suggestion containing edits to apply.</param>
    /// <param name="createBackup">Whether to create .bak files before editing.</param>
    /// <returns>A result for each edit in the suggestion.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="suggestion"/> is null.</exception>
    public static List<FixApplyResult> Apply(FixSuggestion suggestion, bool createBackup = true)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        var results = new List<FixApplyResult>(suggestion.Edits.Count);

        foreach (var edit in suggestion.Edits)
        {
            results.Add(ApplyEdit(edit, createBackup));
        }

        return results;
    }

    /// <summary>
    /// Applies a single <see cref="FileEdit"/> to a file.
    /// </summary>
    private static FixApplyResult ApplyEdit(FileEdit edit, bool createBackup)
    {
        if (!File.Exists(edit.FilePath))
        {
            return new FixApplyResult
            {
                Success = false,
                FilePath = edit.FilePath,
                ErrorMessage = $"File not found: {edit.FilePath}"
            };
        }

        string currentContent;
        try
        {
            currentContent = File.ReadAllText(edit.FilePath);
        }
        catch (IOException ex)
        {
            return new FixApplyResult
            {
                Success = false,
                FilePath = edit.FilePath,
                ErrorMessage = $"Failed to read file: {ex.Message}"
            };
        }

        // Verify the original text still exists in the file
        if (!currentContent.Contains(edit.OriginalText, StringComparison.Ordinal))
        {
            return new FixApplyResult
            {
                Success = false,
                FilePath = edit.FilePath,
                ErrorMessage = "File contents have changed since analysis — original text not found."
            };
        }

        // Create backup before modifying
        string? backupPath = null;
        if (createBackup)
        {
            backupPath = edit.FilePath + ".bak";
            try
            {
                File.Copy(edit.FilePath, backupPath, overwrite: true);
            }
            catch (IOException ex)
            {
                return new FixApplyResult
                {
                    Success = false,
                    FilePath = edit.FilePath,
                    ErrorMessage = $"Failed to create backup: {ex.Message}"
                };
            }
        }

        // Apply the edit (replace first occurrence only)
        var newContent = ReplaceFirst(currentContent, edit.OriginalText, edit.NewText);

        try
        {
            File.WriteAllText(edit.FilePath, newContent);
        }
        catch (IOException ex)
        {
            return new FixApplyResult
            {
                Success = false,
                FilePath = edit.FilePath,
                ErrorMessage = $"Failed to write file: {ex.Message}",
                BackupPath = backupPath
            };
        }

        return new FixApplyResult
        {
            Success = true,
            FilePath = edit.FilePath,
            BackupPath = backupPath
        };
    }

    /// <summary>
    /// Replaces the first occurrence of <paramref name="oldValue"/> with
    /// <paramref name="newValue"/> using ordinal comparison.
    /// </summary>
    internal static string ReplaceFirst(string source, string oldValue, string newValue)
    {
        var index = source.IndexOf(oldValue, StringComparison.Ordinal);
        if (index < 0)
            return source;

        return string.Concat(
            source.AsSpan(0, index),
            newValue,
            source.AsSpan(index + oldValue.Length));
    }
}
