using DiffusionNexus.UI.Services;
using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Handles save/load/export operations.
/// </summary>
internal sealed class DocumentService : IDocumentService
{
    /// <inheritdoc />
    public bool Save(SKBitmap bitmap, string filePath, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 95)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var image = SKImage.FromBitmap(bitmap);
            if (image is null) return false;

            using var data = image.Encode(format, quality);
            if (data is null) return false;

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            data.SaveTo(stream);

            return true;
        }
        catch (Exception ex)
        {
            FileLogger.LogError($"DocumentService.Save failed for {filePath}", ex);
            return false;
        }
    }

    /// <inheritdoc />
    public SKEncodedImageFormat GetFormatFromExtension(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            ".bmp" => SKEncodedImageFormat.Bmp,
            ".gif" => SKEncodedImageFormat.Gif,
            _ => SKEncodedImageFormat.Png
        };
    }

    /// <inheritdoc />
    public string GenerateUniqueFilePath(string directory, string baseName, string extension)
    {
        var counter = 1;
        string newPath;

        do
        {
            var suffix = $"_edited_{counter:D3}";
            newPath = Path.Combine(directory, $"{baseName}{suffix}{extension}");
            counter++;
        }
        while (File.Exists(newPath) && counter < 1000);

        return newPath;
    }
}
