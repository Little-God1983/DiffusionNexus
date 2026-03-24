using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Reads image dimensions from file headers without decoding pixel data.
/// Implementations should be lightweight and avoid loading full images into memory.
/// </summary>
public interface IImageDimensionReader
{
    /// <summary>
    /// Reads the width and height from the file header.
    /// Returns an invalid <see cref="ImageDimensions"/> (both zero) when the
    /// format is unsupported or the file cannot be read.
    /// </summary>
    /// <param name="filePath">Absolute path to the image file.</param>
    /// <returns>Parsed dimensions, or <c>(0, 0)</c> on failure.</returns>
    ImageDimensions ReadDimensions(string filePath);
}
