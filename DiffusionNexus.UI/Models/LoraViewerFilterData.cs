namespace DiffusionNexus.UI.Models;

/// <summary>
/// Serialized form of the LoRA Viewer's saved base-model filter, stored as JSON in
/// <c>AppSettings.LoraViewerFilterJson</c>. Owned and (de)serialized by
/// <c>LoraViewerViewModel</c>. Single slot — saving overwrites the previous filter.
/// </summary>
public sealed class LoraViewerFilterData
{
    /// <summary>Raw base-model names that were selected (case-insensitive match on restore).</summary>
    public List<string> SelectedBaseModels { get; set; } = [];

    /// <summary>Whether the "Unknown" pseudo entry was selected.</summary>
    public bool IncludeUnknown { get; set; }

    /// <summary>Whether the "only models I have installed" narrowing was on.</summary>
    public bool OnlyInstalled { get; set; }

    /// <summary>
    /// Sort field of the Installed tab (a <c>LoraSortField</c> enum name).
    /// Null on filters saved before sort became part of the filter — restoring
    /// those leaves the current sort untouched.
    /// </summary>
    public string? SortField { get; set; }

    /// <summary>Sort direction; null on pre-sort saved filters (see <see cref="SortField"/>).</summary>
    public bool? SortDescending { get; set; }
}
