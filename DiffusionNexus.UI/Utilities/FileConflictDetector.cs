using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

public class FileConflictDetectionResult
{
    public List<FileConflictItem> Conflicts { get; } = [];
    public List<string> NonConflictingFiles { get; } = [];
}

/// <summary>
/// Detects file conflicts when adding files to a destination, respecting file pairs (e.g. image + caption).
/// </summary>
public static class FileConflictDetector
{
    /// <summary>
    /// Analyzes dropped files against existing file names to detect conflicts.
    /// Groups files by base name ensuring that if one file in a pair conflicts, 
    /// the others are also treated as potential conflicts to allow synchronized renaming.
    /// </summary>
    /// <param name="droppedFiles">List of full paths of files being added.</param>
    /// <param name="existingFileNames">Set of filenames already in the destination folder.</param>
    /// <param name="destinationFolder">The full path to the destination folder (used to construct paths).</param>
    /// <returns>Result containing conflicts and non-conflicting files.</returns>
    public static FileConflictDetectionResult DetectConflicts(
        IEnumerable<string> droppedFiles,
        HashSet<string> existingFileNames,
        string destinationFolder)
    {
        var result = new FileConflictDetectionResult();

        // 1. Filter and group dropped files by base name (without extension)
        var filesByBaseName = droppedFiles
            .GroupBy(f => Path.GetFileNameWithoutExtension(f))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in filesByBaseName)
        {
            var pairFiles = group.Value;
            var pairConflicts = false;

            // 2. Check if ANY file in the group conflicts with existing files
            foreach (var filePath in pairFiles)
            {
                var fileName = Path.GetFileName(filePath);
                if (existingFileNames.Contains(fileName))
                {
                    pairConflicts = true;
                    break;
                }
            }

            if (pairConflicts)
            {
                // 3. If any conflict exists in the group, treat ALL files in the group as conflicts.
                // This allows the user to rename the base name and have it apply to all files in the group.
                foreach (var filePath in pairFiles)
                {
                    var fileName = Path.GetFileName(filePath);
                    var existingPath = Path.Combine(destinationFolder, fileName);
                    
                    // We need to create a conflict item even if the specific file doesn't exist 
                    // (so it can follow the rename of its partner).
                    var newItem = CreateConflictItem(filePath, existingPath);
                    result.Conflicts.Add(newItem);
                }
            }
            else
            {
                // 4. No conflicts in this group -> all files are safe
                result.NonConflictingFiles.AddRange(pairFiles);
            }
        }

        return result;
    }

    private static FileConflictItem CreateConflictItem(string sourcePath, string existingPath)
    {
        long newSize = 0;
        DateTime newCreationTime = DateTime.MinValue;
        
        try 
        {
            var newInfo = new FileInfo(sourcePath);
            if (newInfo.Exists)
            {
                newSize = newInfo.Length;
                newCreationTime = newInfo.CreationTime;
            }
        }
        catch { /* Ignore file access errors for robustness/testing */ }

        long existingSize = 0;
        DateTime existingDate = DateTime.MinValue;
        
        // Check if existing file actually exists on disk (it might not if this is a "dragged along" pair member)
        try
        {
            if (File.Exists(existingPath))
            {
                var existingInfo = new FileInfo(existingPath);
                existingSize = existingInfo.Length;
                existingDate = existingInfo.CreationTime;
            }
        }
        catch { /* Ignore file access errors */ }

        return new FileConflictItem
        {
            ConflictingName = Path.GetFileName(sourcePath),
            ExistingFilePath = existingPath, // Stored even if it doesn't exist yet (target path)
            NewFilePath = sourcePath,
            ExistingFileSize = existingSize,
            NewFileSize = newSize,
            ExistingCreationDate = existingDate,
            NewCreationDate = newCreationTime,
            IsImage = MediaFileExtensions.IsImageFile(sourcePath)
            // Note: Does not set PairedCaptionPath logic here as we are treating them as individual items 
            // that will be synced by the ViewModel logic.
        };
    }
}
