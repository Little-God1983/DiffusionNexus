using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Handles save, load, and export operations for the image editor.
/// Abstracts file I/O away from the ViewModel and editor core.
/// </summary>
public interface IDocumentService
{
    /// <summary>
    /// Saves a bitmap to the specified path.
    /// </summary>
    /// <param name="bitmap">The bitmap to save.</param>
    /// <param name="filePath">Destination file path.</param>
    /// <param name="format">Image format.</param>
    /// <param name="quality">Quality for lossy formats (0–100).</param>
    /// <returns>True if saved successfully.</returns>
    bool Save(SKBitmap bitmap, string filePath, SKEncodedImageFormat format = SKEncodedImageFormat.Png, int quality = 95);

    /// <summary>
    /// Determines the image format from a file extension.
    /// </summary>
    /// <param name="filePath">Path or filename with extension.</param>
    /// <returns>The corresponding <see cref="SKEncodedImageFormat"/>.</returns>
    SKEncodedImageFormat GetFormatFromExtension(string filePath);

    /// <summary>
    /// Generates a unique file path by appending a suffix to the base name.
    /// </summary>
    /// <param name="directory">Target directory.</param>
    /// <param name="baseName">Base filename without extension.</param>
    /// <param name="extension">File extension including the dot.</param>
    /// <returns>A unique file path.</returns>
    string GenerateUniqueFilePath(string directory, string baseName, string extension);
}
