using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents an image entry that can be assigned to either side of the comparer.
/// </summary>
public partial class ImageCompareItem : ObservableObject
{
    public ImageCompareItem(string imagePath, string displayName)
    {
        ImagePath = imagePath;
        DisplayName = displayName;
    }

    public string ImagePath { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _isSelectedLeft;

    [ObservableProperty]
    private bool _isSelectedRight;
}
