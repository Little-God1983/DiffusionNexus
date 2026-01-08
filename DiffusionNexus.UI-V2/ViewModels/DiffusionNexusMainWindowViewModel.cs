using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Views;
using Microsoft.Extensions.DependencyInjection;

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

    [ObservableProperty]
    private bool _isDisclaimerAccepted;

    [ObservableProperty]
    private bool _disclaimerCheckboxChecked;

    public ObservableCollection<ModuleItem> Modules { get; } = new();

    public DiffusionNexusMainWindowViewModel()
    {
        // Disclaimer check is called externally after services are initialized
    }

    /// <summary>
    /// Checks disclaimer status against the database. Call after App.Services is initialized.
    /// </summary>
    public async Task CheckDisclaimerStatusAsync()
    {
        try
        {
            var disclaimerService = App.Services?.GetService<IDisclaimerService>();
            if (disclaimerService is not null)
            {
                IsDisclaimerAccepted = await disclaimerService.HasUserAcceptedDisclaimerAsync();
            }
        }
        catch
        {
            // If check fails, show disclaimer
            IsDisclaimerAccepted = false;
        }
    }

    [RelayCommand]
    private async Task AcceptDisclaimerAsync()
    {
        if (!DisclaimerCheckboxChecked)
            return;

        try
        {
            var disclaimerService = App.Services?.GetService<IDisclaimerService>();
            if (disclaimerService is not null)
            {
                await disclaimerService.AcceptDisclaimerAsync();
                
                // Double-check against database
                IsDisclaimerAccepted = await disclaimerService.HasUserAcceptedDisclaimerAsync();
            }
        }
        catch
        {
            // If save fails, don't unlock
            IsDisclaimerAccepted = false;
        }
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

    [RelayCommand]
    private void OpenYoutube()
    {
        OpenUrl("https://youtube.com/@AIKnowledge2Go");
    }

    [RelayCommand]
    private void OpenCivitai()
    {
        OpenUrl("https://civitai.com/user/AIknowlege2go");
    }

    [RelayCommand]
    private void OpenPatreon()
    {
        OpenUrl("https://patreon.com/AIKnowledgeCentral?utm_medium=unknown&utm_source=join_link&utm_campaign=creatorshare_creator&utm_content=copyLink");
    }

    [RelayCommand]
    private void OpenSettings()
    {
        CurrentModuleView = new SettingsView();
    }

    [RelayCommand]
    private void OpenAbout()
    {
        CurrentModuleView = new AboutView();
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

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
