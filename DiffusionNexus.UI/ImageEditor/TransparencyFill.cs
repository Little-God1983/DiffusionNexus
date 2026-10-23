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

/// <summary>
/// The question asked before a JPEG save loses transparency, for
/// <c>IDialogService.ShowOptionsAsync</c>. That dialog styles buttons by position: the first is
/// the neutral dismiss button, the last the green primary. So Cancel comes first and white, the
/// usual choice, last.
/// </summary>
public static class TransparencyFillPrompt
{
    public const string Title = "JPEG can't store transparency";

    public const string Message =
        "This image has transparent areas, and a JPEG has no transparency, so something has to "
        + "fill them in the saved file. To keep the transparency, cancel and export as PNG instead.";

    public static IReadOnlyList<string> Options { get; } = ["Cancel", "Fill with black", "Fill with white"];

    /// <summary>
    /// Maps the dialog's answer to a fill; null for Cancel, a closed dialog (-1) or anything else.
    /// </summary>
    public static TransparencyFill? FromChoice(int index) => index switch
    {
        1 => TransparencyFill.Black,
        2 => TransparencyFill.White,
        _ => null
    };
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
