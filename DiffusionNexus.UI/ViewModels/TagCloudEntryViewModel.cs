using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One clickable chip in the Advanced Search tag cloud.</summary>
public partial class TagCloudEntryViewModel : ObservableObject
{
    public string Name { get; }
    public int Count { get; }
    public string DisplayText => $"{Name} ({Count})";

    [ObservableProperty]
    private bool _isActive;

    public TagCloudEntryViewModel(string name, int count)
    {
        Name = name;
        Count = count;
    }
}
