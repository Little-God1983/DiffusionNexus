using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for a version toggle button in the model tile.
/// </summary>
public partial class VersionButtonViewModel : ObservableObject
{
    private readonly Action<VersionButtonViewModel> _onSelected;

    /// <summary>
    /// The underlying model version.
    /// </summary>
    public ModelVersion Version { get; }

    /// <summary>
    /// Short label for the button (e.g., "XL", "1.5", "v1").
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Full tooltip text.
    /// </summary>
    public string ToolTip { get; }

    /// <summary>
    /// Whether this version is currently selected.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    public VersionButtonViewModel(ModelVersion version, string label, Action<VersionButtonViewModel> onSelected)
    {
        Version = version;
        Label = label;
        ToolTip = version.Name ?? label;
        _onSelected = onSelected;
    }

    /// <summary>
    /// Command to select this version.
    /// </summary>
    [RelayCommand]
    private void Select()
    {
        _onSelected(this);
    }
}
