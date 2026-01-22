using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

public partial class GenerationGalleryGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _countText = string.Empty;

    [ObservableProperty]
    private bool _showHeader;

    public ObservableCollection<GenerationGalleryMediaItemViewModel> Items { get; } = [];

    public void UpdateCountText()
    {
        CountText = Items.Count == 1 ? "1 item" : $"{Items.Count} items";
    }
}
