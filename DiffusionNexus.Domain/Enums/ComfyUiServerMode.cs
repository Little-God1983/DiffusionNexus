namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Which ComfyUI runs the Image Editor's Inpaint and Outpaint (and, from #608, Batch Upscale).
/// Chosen once, app-wide, in Settings → ComfyUI Server.
/// </summary>
public enum ComfyUiServerMode
{
    /// <summary>The app-owned embedded ComfyUI (Diffusion Nexus Engine), started on demand.</summary>
    Engine,

    /// <summary>A ComfyUI the user runs themselves, at <c>AppSettings.ComfyUiServerUrl</c>.</summary>
    CustomUrl
}
