using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a selectable base model filter item (e.g., "SD 1.5", "SDXL 1.0").
/// Used in the LoRA Viewer to filter tiles by base model.
/// </summary>
public partial class BaseModelFilterItem : ObservableObject
{
    /// <summary>
    /// The raw base model name as stored in <c>ModelVersion.BaseModelRaw</c>.
    /// </summary>
    public string BaseModelRaw { get; }

    /// <summary>
    /// True when this label exists only among installed files, not in the Civitai
    /// catalog (e.g. a label Civitai renamed or dropped, or hand-edited sidecar
    /// metadata). Such items stay filterable in the Installed tab but are skipped by
    /// the Browse Civitai mirror — they are not valid values for the Civitai API query.
    /// </summary>
    public bool IsInstalledOnly { get; init; }

    /// <summary>
    /// Whether this filter item is currently selected (active).
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Raised when <see cref="IsSelected"/> changes so the parent can re-apply filters.
    /// </summary>
    public event EventHandler? SelectionChanged;

    public BaseModelFilterItem(string baseModelRaw)
    {
        BaseModelRaw = baseModelRaw;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc/>
    public override string ToString() => BaseModelRaw;
}
