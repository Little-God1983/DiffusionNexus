using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace DiffusionNexus.UI.ViewModels;

public partial class FolderItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? name;

    [ObservableProperty]
    private int modelCount;

    public ObservableCollection<FolderItemViewModel> Children { get; } = new();

    public string DisplayName => $"{Name} ({ModelCount})";
}
