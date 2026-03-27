using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Loads caption files from a dataset folder and pairs them with images by name.
/// </summary>
public class CaptionLoader
{
    /// <summary>
    /// Loads all caption files from the given folder and pairs each with its
    /// matching image file (same base name, image extension).
    /// </summary>
    /// <param name="folderPath">Absolute path to the dataset folder.</param>
    /// <returns>
    /// A tuple of loaded caption files and the total count of image files found.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="folderPath"/> is null or whitespace.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the folder does not exist.</exception>
    public (List<CaptionFile> Captions, int ImageFileCount) Load(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            throw new ArgumentException("Folder path must not be empty.", nameof(folderPath));

        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"Dataset folder not found: {folderPath}");

        var allFiles = Directory.GetFiles(folderPath);

        // Build a lookup of image files: baseName → full path (case-insensitive)
        var imagesByBaseName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var imageCount = 0;

        foreach (var file in allFiles)
        {
            if (SupportedMediaTypes.IsImageFile(file))
            {
                imageCount++;
                var baseName = Path.GetFileNameWithoutExtension(file);
                // If multiple images share the same base name, keep the first
                imagesByBaseName.TryAdd(baseName, file);
            }
        }

        // Load caption files and pair with images
        var captions = new List<CaptionFile>();

        foreach (var file in allFiles)
        {
            if (!SupportedMediaTypes.IsCaptionFile(file))
                continue;

            var rawText = File.ReadAllText(file);
            var baseName = Path.GetFileNameWithoutExtension(file);
            imagesByBaseName.TryGetValue(baseName, out var pairedImage);

            var detectedStyle = TextHelpers.DetectCaptionStyle(rawText);

            captions.Add(new CaptionFile
            {
                FilePath = file,
                RawText = rawText,
                DetectedStyle = detectedStyle,
                PairedImagePath = pairedImage
            });
        }

        return (captions, imageCount);
    }

    /// <summary>
    /// Heuristic to detect whether the caption text is natural-language
    /// prose or booru-style comma-separated tags.
    /// </summary>
    [Obsolete("Use TextHelpers.DetectCaptionStyle instead. To be removed in a future version.")]
    internal static CaptionStyle DetectCaptionStyle(string text) =>
        TextHelpers.DetectCaptionStyle(text);
}
