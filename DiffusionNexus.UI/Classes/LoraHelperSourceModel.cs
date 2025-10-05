using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.Classes;

public partial class LoraHelperSourceModel : ObservableObject
{
    [ObservableProperty]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private bool _isEnabled = true;
}
