using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.LoraSort.Service.Classes;
using DiffusionNexus.LoraSort.Service.Services;
using DiffusionNexus.LoraSort.Service.Helper;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using DiffusionNexus.UI.Classes;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraSortCustomMappingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<CustomTagMap> customTagMappings = new();

    [ObservableProperty]
    private bool isCustomEnabled = true;

    private readonly CustomTagMapXmlService _xmlService = new();
    private Window? _window;

    public IDialogService DialogService { get; set; } = null!;

    public IRelayCommand MoveUpCommand { get; }
    public IRelayCommand MoveDownCommand { get; }
    public IAsyncRelayCommand AddMappingCommand { get; }
    public IAsyncRelayCommand<CustomTagMap?> EditMappingCommand { get; }
    public IAsyncRelayCommand<CustomTagMap?> DeleteMappingCommand { get; }
    public IAsyncRelayCommand DeleteAllMappingsCommand { get; }

    public LoraSortCustomMappingsViewModel()
    {
        MoveUpCommand = new RelayCommand<CustomTagMap?>(MoveUp);
        MoveDownCommand = new RelayCommand<CustomTagMap?>(MoveDown);
        AddMappingCommand = new AsyncRelayCommand(AddMappingAsync);
        EditMappingCommand = new AsyncRelayCommand<CustomTagMap?>(EditMappingAsync);
        DeleteMappingCommand = new AsyncRelayCommand<CustomTagMap?>(DeleteMappingAsync);
        DeleteAllMappingsCommand = new AsyncRelayCommand(DeleteAllMappingsAsync);
        LoadMappings();
    }

    public void SetWindow(Window window) => _window = window;

    private void LoadMappings()
    {
        CustomTagMappings = CustomTagMapPriorityHelper.Normalize(_xmlService.LoadMappings());
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

    private async Task EditMappingAsync(CustomTagMap? map)
    {
        if (map == null || _window is null) return;

        var dialog = new Views.CustomTagMapWindow();
        if (dialog.DataContext is CustomTagMapWindowViewModel vm)
        {
            vm.SetWindow(dialog);
            vm.SetMapping(map);
        }

        var result = await dialog.ShowDialog<bool>(_window);
        if (result)
        {
            _xmlService.SaveMappings(CustomTagMappings);
            LoadMappings();
        }
    }


    private async Task DeleteMappingAsync(CustomTagMap? map)
    {
        if (map == null) return;
        var confirm = await DialogService.ShowConfirmationAsync("Delete this mapping?");
        if (confirm != true) return;

        CustomTagMappings.Remove(map);
        CustomTagMappings = CustomTagMapPriorityHelper.Normalize(CustomTagMappings);
        _xmlService.SaveMappings(CustomTagMappings);
    }

    private async Task DeleteAllMappingsAsync()
    {
        var confirm = await DialogService.ShowConfirmationAsync("Are you sure you want to delete all custom mappings?");
        if (confirm != true) return;
        var path = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "mappings.xml");
        _xmlService.DeleteAllMappings(path);
        CustomTagMappings.Clear();
    }

    private void MoveUp(CustomTagMap? map)
    {
        if (map == null) return;
        CustomTagMapPriorityHelper.MoveUp(CustomTagMappings, map);
        CustomTagMappings = CustomTagMapPriorityHelper.Normalize(CustomTagMappings);
        _xmlService.SaveMappings(CustomTagMappings);
    }

    private void MoveDown(CustomTagMap? map)
    {
        if (map == null) return;
        CustomTagMapPriorityHelper.MoveDown(CustomTagMappings, map);
        CustomTagMappings = CustomTagMapPriorityHelper.Normalize(CustomTagMappings);
        _xmlService.SaveMappings(CustomTagMappings);
    }
}
