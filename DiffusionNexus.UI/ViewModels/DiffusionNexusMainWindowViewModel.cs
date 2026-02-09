using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reflection;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
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

    [ObservableProperty]
    private bool _isVisible = true;

    [ObservableProperty]
    private bool _isSelected;

    public ModuleItem(string name, string iconPath, object? view = null, bool isVisible = true)
    {
        _name = name;
        _view = view;
        _isVisible = isVisible;
        
        if (!string.IsNullOrEmpty(iconPath))
        {
            try
            {
                // Copy asset data into a MemoryStream so the Bitmap has a stable backing
                // buffer. The AssetLoader stream can be disposed once the bytes are copied;
                // MemoryStream is backed by a managed array and needs no disposal.
                using var assetStream = AssetLoader.Open(new Uri(iconPath));
                var ms = new MemoryStream();
                assetStream.CopyTo(ms);
                ms.Position = 0;
                _icon = new Bitmap(ms);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Failed to load icon from {IconPath}", iconPath);
                _icon = null;
            }
        }
    }
}

/// <summary>
/// Main window ViewModel managing navigation and application state.
/// </summary>
public partial class DiffusionNexusMainWindowViewModel : ViewModelBase
{
    private IActivityLogService? _activityLogService;

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

    [ObservableProperty]
    private StatusBarViewModel? _statusBar;

    [ObservableProperty]
    private bool _isBackupInProgress;

    /// <summary>
    /// Gets the application version from assembly metadata.
    /// </summary>
    public string AppVersion { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public ObservableCollection<ModuleItem> Modules { get; } = new();

    public DiffusionNexusMainWindowViewModel()
    {
        // Disclaimer check is called externally after services are initialized
    }

    /// <summary>
    /// Initializes the status bar after services are available.
    /// </summary>
    public void InitializeStatusBar()
    {
        _activityLogService = App.Services?.GetService<IActivityLogService>();
        if (_activityLogService is not null)
        {
            StatusBar = new StatusBarViewModel(_activityLogService);
            _activityLogService.LogInfo("App", "Application started");
            
            // Subscribe to backup progress changes
            _activityLogService.BackupProgressChanged += OnBackupProgressChanged;
        }
    }

    private void OnBackupProgressChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsBackupInProgress = _activityLogService?.IsBackupInProgress ?? false;
        });
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
        
        // Clear previous selection
        foreach (var m in Modules)
        {
            m.IsSelected = false;
        }
        
        module.IsSelected = true;
        SelectedModule = module;
        CurrentModuleView = module.View;
        
        // Collapse the menu after selection
        IsMenuOpen = false;
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
        
            // Set first module as default (without collapsing menu)
            if (CurrentModuleView is null)
            {
                module.IsSelected = true;
                SelectedModule = module;
                CurrentModuleView = module.View;
            }
        }
    }
