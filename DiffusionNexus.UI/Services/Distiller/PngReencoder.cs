using System;
using System.IO;
using SkiaSharp;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>A re-encoded PNG image: the encoded bytes plus the final pixel dimensions.</summary>
internal sealed record ReencodedPng(byte[] Bytes, int Width, int Height);

/// <summary>
/// Re-encodes a PNG with an optional downscale (longest side capped, aspect preserved) and a
/// configurable zlib compression level. Used by the Batch Metadata Distiller's optional
/// resize/recompress output step and its file-size estimate. Re-encoding is pixel-lossless
/// (PNG); only resizing changes pixels.
/// </summary>
internal static class PngReencoder
{
    /// <summary>zlib level used when a resize forces a re-encode but max compression wasn't asked for.</summary>
    internal const int DefaultZlibLevel = 6;

    /// <summary>zlib level for the "maximum compression" option.</summary>
    internal const int MaxZlibLevel = 9;

    /// <summary>
    /// Computes the output dimensions for a longest-side cap. Returns the input unchanged when
    /// <paramref name="maxDimension"/> is null or the image already fits.
    /// </summary>
    internal static (int Width, int Height) TargetSize(int width, int height, int? maxDimension)
    {
        if (maxDimension is not int max || max <= 0) return (width, height);
        var longest = Math.Max(width, height);
        if (longest <= max) return (width, height);

        var scale = (double)max / longest;
        return (Math.Max(1, (int)Math.Round(width * scale)),
                Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Decodes <paramref name="sourcePath"/>, downscales to <paramref name="maxDimension"/> on the
    /// longest side when needed, and encodes a PNG at <paramref name="zlibLevel"/>. Returns null when
    /// the file cannot be decoded or encoded.
    /// </summary>
    internal static ReencodedPng? Reencode(string sourcePath, int? maxDimension, int zlibLevel)
    {
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath)) return null;

        using var decoded = SKBitmap.Decode(sourcePath);
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0) return null;

        var (tw, th) = TargetSize(decoded.Width, decoded.Height, maxDimension);

        SKBitmap? resized = null;
        try
        {
            var source = decoded;
            if (tw != decoded.Width || th != decoded.Height)
            {
#pragma warning disable CS0618 // SKFilterQuality is obsolete but matches EfficientImageDecoder's resize path
                resized = decoded.Resize(new SKImageInfo(tw, th, decoded.ColorType, decoded.AlphaType), SKFilterQuality.High);
#pragma warning restore CS0618
                if (resized is null) return null;
                source = resized;
            }

            using var pixmap = source.PeekPixels();
            if (pixmap is null) return null;

            var options = new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters,
                Math.Clamp(zlibLevel, 0, MaxZlibLevel));
            using var data = pixmap.Encode(options);
            if (data is null || data.Size == 0) return null;

            return new ReencodedPng(data.ToArray(), source.Width, source.Height);
        }
        finally
        {
            resized?.Dispose();
        }
    }
}
