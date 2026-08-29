namespace DiffusionNexus.UI.Models;

/// <summary>
/// Serialized form of the Civitai Browser's saved filter, stored as JSON in
/// <c>AppSettings.CivitaiBrowserFilterJson</c>. Owned and (de)serialized by
/// <c>CivitaiBrowserViewModel</c>. Single slot — saving overwrites the previous filter.
/// </summary>
public sealed class CivitaiBrowserFilterData
{
    /// <summary>Raw base-model names that were selected (case-insensitive match on restore).</summary>
    public List<string> SelectedBaseModels { get; set; } = [];

    /// <summary>
    /// Whether to show models already installed. Null on filters saved before this flag
    /// existed (or any older payload) — the default is ticked/shown, so null means "show",
    /// same as <see cref="ShowEarlyAccess"/>, <see cref="ShowPaywalled"/> and <see cref="ShowNsfw"/>.
    /// </summary>
    public bool? ShowInstalled { get; set; }

    /// <summary>Whether to show Early Access models. Null means "show" (see <see cref="ShowInstalled"/>).</summary>
    public bool? ShowEarlyAccess { get; set; }

    /// <summary>Whether to show paywalled models. Null means "show" (see <see cref="ShowInstalled"/>).</summary>
    public bool? ShowPaywalled { get; set; }

    /// <summary>Whether to show NSFW models. Null means "show" (see <see cref="ShowInstalled"/>).</summary>
    public bool? ShowNsfw { get; set; }
}
