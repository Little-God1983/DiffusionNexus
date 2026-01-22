using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

public sealed class GenerationGalleryGroupViewModel : ObservableObject
{
    public GenerationGalleryGroupViewModel(string name, IEnumerable<GenerationGalleryMediaItemViewModel> items)
    {
        Name = name;
        Items = new ObservableCollection<GenerationGalleryMediaItemViewModel>(items);
        CountText = FormatCountText(Items.Count);
    }

    public string Name { get; }

    public string CountText { get; }

    public ObservableCollection<GenerationGalleryMediaItemViewModel> Items { get; }

    private static string FormatCountText(int count)
    {
        return count == 1 ? "1 item" : $"{count} items";
    }
}
