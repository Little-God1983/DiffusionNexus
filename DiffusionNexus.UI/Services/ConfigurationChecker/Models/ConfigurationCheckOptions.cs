namespace DiffusionNexus.UI.Services.ConfigurationChecker.Models;

/// <summary>
/// Options for the configuration check.
/// </summary>
public sealed record ConfigurationCheckOptions
{
    /// <summary>
    /// The user's selected VRAM in GB. When greater than 0, only the best matching
    /// quant variant is expected per model (rather than all variants).
    /// Set to 0 to skip VRAM-based filtering.
    /// </summary>
    public int SelectedVramGb { get; init; }

    /// <summary>
    /// Optional custom model base folder (from user settings).
    /// When set, models are also searched here.
    /// </summary>
    public string? ModelBaseFolder { get; init; }

    /// <summary>
    /// Optional folder path overrides from user settings.
    /// </summary>
    public Dictionary<string, string>? FolderPathOverrides { get; init; }
}
