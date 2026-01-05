namespace DiffusionNexus.Domain.Utilities;

/// <summary>
/// Provides display mappings for base model names.
/// Converts long Civitai base model strings to short display labels.
/// </summary>
public static class BaseModelDisplayMapper
{
    /// <summary>
    /// Display info for a base model (short name + optional icon).
    /// </summary>
    /// <param name="ShortName">Short display name (e.g., "XL", "1.5").</param>
    /// <param name="Icon">Optional icon/emoji for the base model.</param>
    /// <param name="ToolTip">Full name for tooltip display.</param>
    public readonly record struct DisplayInfo(string ShortName, string? Icon, string ToolTip);

    private static readonly Dictionary<string, DisplayInfo> Mappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // Stable Diffusion versions
        ["SD 1.4"] = new("1.4", null, "Stable Diffusion 1.4"),
        ["SD 1.5"] = new("1.5", null, "Stable Diffusion 1.5"),
        ["SD 1.5 LCM"] = new("1.5 LCM", null, "Stable Diffusion 1.5 LCM"),
        ["SD 1.5 Hyper"] = new("1.5 Hyper", null, "Stable Diffusion 1.5 Hyper"),
        ["SD 2.0"] = new("2.0", null, "Stable Diffusion 2.0"),
        ["SD 2.0 768"] = new("2.0 768", null, "Stable Diffusion 2.0 768"),
        ["SD 2.1"] = new("2.1", null, "Stable Diffusion 2.1"),
        ["SD 2.1 768"] = new("2.1 768", null, "Stable Diffusion 2.1 768"),
        ["SD 2.1 Unclip"] = new("2.1 Unclip", null, "Stable Diffusion 2.1 Unclip"),

        // SDXL
        ["SDXL 0.9"] = new("XL 0.9", null, "Stable Diffusion XL 0.9"),
        ["SDXL 1.0"] = new("XL", null, "Stable Diffusion XL 1.0"),
        ["SDXL 1.0 LCM"] = new("XL LCM", null, "Stable Diffusion XL 1.0 LCM"),
        ["SDXL Distilled"] = new("XL Dist", null, "Stable Diffusion XL Distilled"),
        ["SDXL Hyper"] = new("XL Hyper", null, "Stable Diffusion XL Hyper"),
        ["SDXL Lightning"] = new("XL ?", null, "Stable Diffusion XL Lightning"),
        ["SDXL Turbo"] = new("XL Turbo", null, "Stable Diffusion XL Turbo"),

        // Pony
        ["Pony"] = new("Pony", "??", "Pony Diffusion"),

        // Illustrious
        ["Illustrious"] = new("IL", null, "Illustrious"),

        // Flux
        ["Flux.1 S"] = new("Flux S", null, "Flux.1 Schnell"),
        ["Flux.1 D"] = new("Flux D", null, "Flux.1 Dev"),
        ["Flux.1 D Hyper"] = new("Flux D Hyper", null, "Flux.1 Dev Hyper"),

        // Hunyuan
        ["Hunyuan 1"] = new("HY1", null, "Hunyuan 1"),
        ["HunyuanDiT"] = new("HY DiT", null, "Hunyuan DiT"),
        ["HunyuanDiT 1.1"] = new("HY DiT 1.1", null, "Hunyuan DiT 1.1"),
        ["HunyuanDiT 1.2"] = new("HY DiT 1.2", null, "Hunyuan DiT 1.2"),

        // Wan Video
        ["Wan Video 1.3B t2v"] = new("Wan 1.3B", "??", "Wan Video 1.3B Text-to-Video"),
        ["Wan Video 14B t2v"] = new("Wan 14B", "??", "Wan Video 14B Text-to-Video"),
        ["Wan Video 14B i2v 480p"] = new("Wan i2v 480", "??", "Wan Video 14B Image-to-Video 480p"),
        ["Wan Video 14B i2v 720p"] = new("Wan i2v 720", "??", "Wan Video 14B Image-to-Video 720p"),

        // CogVideoX
        ["CogVideoX"] = new("CogVX", "??", "CogVideoX"),

        // LTX Video
        ["LTXV"] = new("LTXV", "??", "LTX Video"),

        // Mochi
        ["Mochi"] = new("Mochi", "??", "Mochi"),

        // PixArt
        ["PixArt a"] = new("PixArt ?", null, "PixArt Alpha"),
        ["PixArt E"] = new("PixArt ?", null, "PixArt Sigma"),

        // Kolors
        ["Kolors"] = new("Kolors", null, "Kolors"),

        // Cascade
        ["Stable Cascade"] = new("Cascade", null, "Stable Cascade"),

        // AuraFlow
        ["AuraFlow"] = new("Aura", null, "AuraFlow"),

        // Lumina
        ["Lumina"] = new("Lumina", null, "Lumina"),

        // NoobAI
        ["NoobAI"] = new("Noob", null, "NoobAI"),

        // Z-Image-Turbo (your custom)
        ["Z-Image-Turbo"] = new("ZIT", "?", "Z-Image-Turbo"),

        // SVD
        ["SVD"] = new("SVD", "??", "Stable Video Diffusion"),
        ["SVD XT"] = new("SVD XT", "??", "Stable Video Diffusion XT"),
        ["SVD XT 1.1"] = new("SVD XT 1.1", "??", "Stable Video Diffusion XT 1.1"),

        // Stable Audio
        ["Stable Audio"] = new("Audio", "??", "Stable Audio"),

        // Other
        ["Other"] = new("Other", null, "Other"),
        ["UNKNOWN"] = new("?", null, "Unknown Base Model"),
    };

    /// <summary>
    /// Gets display info for a base model string.
    /// </summary>
    /// <param name="baseModel">The base model name from Civitai API.</param>
    /// <returns>Display info with short name and optional icon.</returns>
    public static DisplayInfo GetDisplayInfo(string? baseModel)
    {
        if (string.IsNullOrWhiteSpace(baseModel))
        {
            return new DisplayInfo("?", null, "Unknown");
        }

        if (Mappings.TryGetValue(baseModel, out var info))
        {
            return info;
        }

        // Return the original name if no mapping found
        return new DisplayInfo(
            TruncateForDisplay(baseModel, 12),
            null,
            baseModel);
    }

    /// <summary>
    /// Gets just the short display name for a base model.
    /// </summary>
    /// <param name="baseModel">The base model name.</param>
    /// <returns>Short display name.</returns>
    public static string GetShortName(string? baseModel)
    {
        return GetDisplayInfo(baseModel).ShortName;
    }

    /// <summary>
    /// Gets the icon for a base model if one exists.
    /// </summary>
    /// <param name="baseModel">The base model name.</param>
    /// <returns>Icon string or null.</returns>
    public static string? GetIcon(string? baseModel)
    {
        return GetDisplayInfo(baseModel).Icon;
    }

    /// <summary>
    /// Checks if the base model has a video-related icon.
    /// </summary>
    /// <param name="baseModel">The base model name.</param>
    /// <returns>True if this is a video model.</returns>
    public static bool IsVideoModel(string? baseModel)
    {
        var info = GetDisplayInfo(baseModel);
        return info.Icon == "??";
    }

    /// <summary>
    /// Formats multiple base models for display.
    /// </summary>
    /// <param name="baseModels">Collection of base model names.</param>
    /// <param name="separator">Separator between models (default: ", ").</param>
    /// <returns>Formatted string with all base models.</returns>
    public static string FormatMultiple(IEnumerable<string?>? baseModels, string separator = ", ")
    {
        if (baseModels is null)
        {
            return "?";
        }

        var displays = baseModels
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b =>
            {
                var info = GetDisplayInfo(b);
                return info.Icon is not null
                    ? $"{info.Icon} {info.ShortName}"
                    : info.ShortName;
            })
            .Distinct();

        return string.Join(separator, displays);
    }

    private static string TruncateForDisplay(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 1)] + "…";
    }
}
