using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a single row in the workload details table (model or custom node).
/// </summary>
public partial class WorkloadDetailItemViewModel : ViewModelBase
{
    /// <summary>
    /// The underlying entity ID (ModelDownload or GitRepository).
    /// </summary>
    public Guid Id { get; }

    [ObservableProperty]
    private string _itemName = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isPlaceholder;

    /// <summary>
    /// Whether the user has selected this item for installation.
    /// Pre-checked for missing items.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// True when the item is not present on disk and is not a runtime placeholder.
    /// Only missing items are eligible for installation.
    /// </summary>
    public bool IsMissing => !IsInstalled && !IsPlaceholder;

    /// <summary>
    /// True when this model has download links with VRAM profile variants.
    /// Only relevant for items in the "Model" category.
    /// </summary>
    public bool HasVramProfiles { get; }

    /// <summary>
    /// Human-readable status text.
    /// </summary>
    public string StatusText => IsPlaceholder ? "Downloaded on run" : IsInstalled ? "Installed" : "Missing";

    /// <summary>
    /// Hex color for the status text.
    /// </summary>
    public string StatusColor => IsPlaceholder ? "#FF9800" : IsInstalled ? "#4CAF50" : "#F44336";

    /// <summary>
    /// Additional info such as the path where the item was found, or the expected path.
    /// </summary>
    [ObservableProperty]
    private string _details = string.Empty;

    public WorkloadDetailItemViewModel(
        Guid id,
        string itemName,
        string category,
        bool isInstalled,
        string details = "",
        bool isPlaceholder = false,
        bool hasVramProfiles = false)
    {
        Id = id;
        _itemName = itemName;
        _category = category;
        _isInstalled = isInstalled;
        _details = details;
        _isPlaceholder = isPlaceholder;
        HasVramProfiles = hasVramProfiles;

        // Pre-select missing items for installation
        _isSelected = IsMissing;
    }
}
