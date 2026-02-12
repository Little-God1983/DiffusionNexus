namespace DiffusionNexus.UI.ImageEditor;

/// <summary>
/// Result of preparing an inpaint masked image for AI inpainting.
/// </summary>
/// <param name="Success">Whether the preparation completed successfully.</param>
/// <param name="MaskedImagePng">PNG bytes of the masked image (transparent = inpaint region).</param>
/// <param name="BaseWasCaptured">Whether the inpaint base was auto-captured because none existed.</param>
/// <param name="ErrorMessage">Error description if preparation failed.</param>
public record InpaintPrepareResult(
    bool Success,
    byte[]? MaskedImagePng,
    bool BaseWasCaptured,
    string? ErrorMessage = null)
{
    /// <summary>Creates a successful result.</summary>
    public static InpaintPrepareResult Succeeded(byte[] png, bool baseCaptured) =>
        new(true, png, baseCaptured);

    /// <summary>Creates a failed result.</summary>
    public static InpaintPrepareResult Failed(string error, bool baseCaptured = false) =>
        new(false, null, baseCaptured, error);
}
