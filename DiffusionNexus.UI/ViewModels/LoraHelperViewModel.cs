using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraHelperViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? searchText;

    public ObservableCollection<string> FolderItems { get; } = new() { "Models", "Generated Images" };
    public ObservableCollection<LoraCard> Cards { get; } = new();

    public LoraHelperViewModel()
    {
        for (int i = 1; i <= 10; i++)
        {
            Cards.Add(new LoraCard { Name = $"Sample Lora {i}", Description = "This is a sample lora card for demonstration purposes" });
        }
    }
}

public class LoraCard : ViewModelBase
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}
