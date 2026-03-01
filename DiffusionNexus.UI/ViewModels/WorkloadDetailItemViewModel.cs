using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a single row in the workload details table (model or custom node).
/// </summary>
public partial class WorkloadDetailItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _itemName = string.Empty;

    [ObservableProperty]
    private string _category = string.Empty;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _isPlaceholder;

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

    public WorkloadDetailItemViewModel(string itemName, string category, bool isInstalled, string details = "", bool isPlaceholder = false)
    {
        _itemName = itemName;
        _category = category;
        _isInstalled = isInstalled;
        _details = details;
        _isPlaceholder = isPlaceholder;
    }
}
