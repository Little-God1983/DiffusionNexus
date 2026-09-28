using SkiaSharp;

namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// What a JPEG save puts where the image is transparent. JPEG has no alpha channel, so those
/// areas must become some colour; left to the encoder they turn black.
/// </summary>
public enum TransparencyFill
{
    White,
    Black
}

/// <summary>Maps <see cref="TransparencyFill"/> to the colour drawn behind the image.</summary>
public static class TransparencyFillExtensions
{
    public static SKColor ToSKColor(this TransparencyFill fill) => fill switch
    {
        TransparencyFill.White => SKColors.White,
        TransparencyFill.Black => SKColors.Black,
        _ => throw new ArgumentOutOfRangeException(nameof(fill), fill, null)
    };
}
