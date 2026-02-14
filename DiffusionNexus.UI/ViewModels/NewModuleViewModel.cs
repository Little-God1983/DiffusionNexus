using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

public partial class NewModuleViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _welcomeMessage = "Welcome to the New Module!";

    public NewModuleViewModel()
    {
    }
}
