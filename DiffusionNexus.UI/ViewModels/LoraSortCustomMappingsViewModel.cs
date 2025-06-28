using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.LoraSort.Service.Services;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraSortCustomMappingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<CustomTagMap> customTagMappings = new();

    [ObservableProperty]
    private bool isCustomEnabled = true;

    private readonly CustomTagMapXmlService _xmlService = new();
    private Window? _window;

    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }
    public IAsyncRelayCommand AddMappingCommand { get; }
    public IRelayCommand DeleteAllMappingsCommand { get; }

    public LoraSortCustomMappingsViewModel()
    {
        MoveUpCommand = new RelayCommand<CustomTagMap?>(_ => { });
        MoveDownCommand = new RelayCommand<CustomTagMap?>(_ => { });
        AddMappingCommand = new AsyncRelayCommand(AddMappingAsync);
        DeleteAllMappingsCommand = new RelayCommand(DeleteAllMappings);
        LoadMappings();
    }

    public void SetWindow(Window window) => _window = window;

    private void LoadMappings()
    {
        CustomTagMappings = _xmlService.LoadMappings();
    }

    private async Task AddMappingAsync()
    {
        if (_window is null) return;

        var dialog = new Views.CustomTagMapWindow();
        if (dialog.DataContext is CustomTagMapWindowViewModel vm)
            vm.SetWindow(dialog);

        var result = await dialog.ShowDialog<bool>(_window);
        if (result)
            LoadMappings();
    }

    private void DeleteAllMappings()
    {
        var path = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "mappings.xml");
        _xmlService.DeleteAllMappings(path);
        CustomTagMappings.Clear();
    }
}
