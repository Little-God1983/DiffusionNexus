using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a navigation module in the application.
/// </summary>
public partial class ModuleItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private IImage? _icon;

    [ObservableProperty]
    private object? _view;

    public ModuleItem(string name, string iconPath, object? view = null)
    {
        Name = name;
        View = view;
        
        if (!string.IsNullOrEmpty(iconPath))
        {
            try
            {
                using var stream = AssetLoader.Open(new Uri(iconPath));
                Icon = new Bitmap(stream);
            }
            catch
            {
                // Fallback or log error if needed
                Icon = null;
            }
        }
    }
}

/// <summary>
/// Main window ViewModel managing navigation and application state.
/// </summary>
public partial class DiffusionNexusMainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isMenuOpen = true;

    [ObservableProperty]
    private object? _currentModuleView;

    [ObservableProperty]
    private ModuleItem? _selectedModule;

    public ObservableCollection<ModuleItem> Modules { get; } = new();

    public DiffusionNexusMainWindowViewModel()
    {
        // Modules will be registered here as they are created
        // For now, start with an empty shell
    }

    [RelayCommand]
    private void ToggleMenu()
    {
        IsMenuOpen = !IsMenuOpen;
    }

    [RelayCommand]
    private void NavigateToModule(ModuleItem? module)
    {
        if (module is null) return;
        
        SelectedModule = module;
        CurrentModuleView = module.View;
    }

    /// <summary>
    /// Registers a module for navigation.
    /// </summary>
    public void RegisterModule(ModuleItem module)
    {
        Modules.Add(module);
        
        // Set first module as default
        if (CurrentModuleView is null)
        {
            NavigateToModule(module);
        }
    }
}
