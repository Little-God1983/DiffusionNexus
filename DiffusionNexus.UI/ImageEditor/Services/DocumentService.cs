using DiffusionNexus.Service.Services.Imaging;
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

            // Encode before the file is opened, so a refused format (GIF) leaves an existing
            // file untouched. BMP goes through the app's own writer: Skia has no BMP encoder.
            byte[] bytes;
            try
            {
                bytes = ImageFileWriter.Encode(bitmap, format, quality);
            }
            catch (NotSupportedException ex)
            {
                FileLogger.LogWarning($"DocumentService.Save: {ex.Message} ({filePath})");
                return false;
            }

            using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            stream.Write(bytes);

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
        => TryGetFormatFromExtension(filePath, out var format) ? format : SKEncodedImageFormat.Png;

    /// <inheritdoc />
    public bool TryGetFormatFromExtension(string filePath, out SKEncodedImageFormat format)
    {
        SKEncodedImageFormat? known = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            ".bmp" => SKEncodedImageFormat.Bmp,
            ".gif" => SKEncodedImageFormat.Gif,
            _ => null
        };
        format = known ?? SKEncodedImageFormat.Png;
        return known is not null;
    }

    /// <inheritdoc />
    public string GenerateUniqueFilePath(string directory, string baseName, string extension)
    {
        for (var counter = 1; counter < 1000; counter++)
        {
            var candidate = Path.Combine(directory, $"{baseName}_edited_{counter:D3}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        throw new IOException(
            $"Could not find an unused file name for '{baseName}' in '{directory}' after 999 attempts.");
    }
}
