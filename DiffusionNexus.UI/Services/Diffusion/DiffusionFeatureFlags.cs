namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Compile-time feature flags for the local diffusion backend.
/// Centralized so a single edit toggles the new path without hunting through call sites.
/// </summary>
public static class DiffusionFeatureFlags
{
    /// <summary>
    /// Master switch for the local <c>StableDiffusion.NET</c>-backed Diffusion Canvas.
    /// When <c>true</c>, the canvas module is registered and the backend is wired up.
    /// When <c>false</c>, ComfyUI remains the sole generation path.
    ///
    /// TODO(v2-backend-dropdown): replace this static flag with an <c>AppSettings</c>
    /// option once the user-facing backend dropdown ships.
    /// </summary>
    public const bool UseLocalDiffusionBackend = true;
}
