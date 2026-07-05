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
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services;
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

    /// <summary>
    /// The ViewModel associated with this module's view.
    /// Used for <see cref="IThumbnailAware"/> activation when navigating.
    /// </summary>
    public object? ViewModel { get; init; }

    public ModuleItem(string name, string iconPath, object? view = null, bool isVisible = true)
    {
        _name = name;
        _view = view;
        _isVisible = isVisible;
        _icon = SafeAssetBitmap.Load(iconPath);
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
    /// True when <see cref="IActivityLogService"/> is currently tracking a
    /// download. Used by <c>DiffusionNexusMainWindow</c> to prompt the user
    /// before closing — losing a partly-fetched 5–9 GB GGUF mid-stream is
    /// expensive on metered connections.
    /// </summary>
    [ObservableProperty]
    private bool _isDownloadInProgress;

    /// <summary>
    /// Display name of the currently running download, surfaced in the close
    /// confirmation dialog so the user knows what they'd be aborting.
    /// </summary>
    [ObservableProperty]
    private string? _activeDownloadName;

    /// <summary>
    /// Hidden feature toggle exposed in the main window sidebar.
    /// When enabled, the Diffusion Canvas navigation entry is shown.
    /// (Previously this property gated the Dataset Quality tab; that tab is now always visible.)
    /// </summary>
    [ObservableProperty]
    private bool _isDiffusionCanvasEnabled;

    private ModuleItem? _diffusionCanvasModule;

    /// <summary>
    /// Registers the Diffusion Canvas <see cref="ModuleItem"/> so its sidebar visibility
    /// can be driven by <see cref="IsDiffusionCanvasEnabled"/>.
    /// </summary>
    public void SetDiffusionCanvasModule(ModuleItem module)
    {
        _diffusionCanvasModule = module;
        _diffusionCanvasModule.IsVisible = IsDiffusionCanvasEnabled;
    }

    partial void OnIsDiffusionCanvasEnabledChanged(bool value)
    {
        if (_diffusionCanvasModule is not null)
        {
            _diffusionCanvasModule.IsVisible = value;
        }
    }

    /// <summary>
    /// Gets the application version from assembly metadata.
    /// </summary>
    public string AppVersion { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public ObservableCollection<ModuleItem> Modules { get; } = new();

    /// <summary>
    /// Operator-authored announcements shown in the banner above the main content.
    /// </summary>
    public ObservableCollection<ServerMessageItemViewModel> ServerMessages { get; } = new();

    /// <summary>
    /// Whether any server message is currently visible (drives the banner's visibility).
    /// </summary>
    public bool HasServerMessages => ServerMessages.Count > 0;

    public DiffusionNexusMainWindowViewModel()
    {
        // Disclaimer check is called externally after services are initialized
    }

    /// <summary>
    /// Fetches applicable server messages (Gist-backed) and populates <see cref="ServerMessages"/>.
    /// Resolves the service from <see cref="App.Services"/>; failures are logged and never disrupt startup.
    /// </summary>
    public async Task LoadServerMessagesAsync()
    {
        var service = App.Services?.GetService<IServerMessageService>();
        if (service is null)
        {
            return;
        }

        var store = App.Services?.GetService<DismissedMessageStore>();

        try
        {
            var dismissed = store?.Load();
            var result = await service.GetMessagesAsync("app", dismissed);

            if (!string.IsNullOrEmpty(result.ErrorMessage))
            {
                Serilog.Log.Information("Server message check failed: {Error}", result.ErrorMessage);
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                ServerMessages.Clear();
                foreach (var msg in result.Messages)
                {
                    ServerMessages.Add(new ServerMessageItemViewModel(msg, DismissServerMessage, OpenUrl));
                }
                OnPropertyChanged(nameof(HasServerMessages));
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Information(ex, "Server message check error");
        }
    }

    private void DismissServerMessage(ServerMessageItemViewModel item)
    {
        ServerMessages.Remove(item);
        OnPropertyChanged(nameof(HasServerMessages));

        try
        {
            App.Services?.GetService<DismissedMessageStore>()?.Add(item.Id);
        }
        catch
        {
            // Best-effort: failing to persist a dismissal just means it may reappear next launch.
        }
    }

    /// <summary>
    /// Initializes the status bar after services are available.
    /// </summary>
    public void InitializeStatusBar()
    {
        _activityLogService = App.Services?.GetService<IActivityLogService>();
        if (_activityLogService is not null)
        {
            var unifiedLogger = App.Services?.GetService<IUnifiedLogger>();
            var taskTracker = App.Services?.GetService<ITaskTracker>();
            var downloadCoordinator = App.Services?.GetService<IDownloadCoordinator>();
            StatusBar = new StatusBarViewModel(_activityLogService, unifiedLogger, taskTracker, downloadCoordinator);
            _activityLogService.LogInfo("App", "Application started");

            // Subscribe to backup progress changes
            _activityLogService.BackupProgressChanged += OnBackupProgressChanged;

            // Subscribe to download progress changes — the close handler reads
            // IsDownloadInProgress and warns before aborting a partial fetch.
            _activityLogService.DownloadProgressChanged += OnDownloadProgressChanged;
            IsDownloadInProgress = _activityLogService.IsDownloadInProgress;
            ActiveDownloadName = _activityLogService.DownloadOperationName;

            // Wire instance management (Start/Stop/Restart) into the Unified Console
            var processManager = App.Services?.GetService<Services.PackageProcessManager>();
            if (processManager is not null && App.Services is not null)
            {
                StatusBar.InitializeInstanceManagement(processManager, App.Services);
            }
        }
    }

    private void OnBackupProgressChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsBackupInProgress = _activityLogService?.IsBackupInProgress ?? false;
        });
    }

    private void OnDownloadProgressChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsDownloadInProgress = _activityLogService?.IsDownloadInProgress ?? false;
            ActiveDownloadName = _activityLogService?.DownloadOperationName;
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
        
        // Deactivate thumbnails for the previous module
        if (SelectedModule?.ViewModel is IThumbnailAware previousAware)
        {
            previousAware.OnThumbnailDeactivated();
        }

        // Clear previous selection
        foreach (var m in Modules)
        {
            m.IsSelected = false;
        }
        
        module.IsSelected = true;
        SelectedModule = module;
        CurrentModuleView = module.View;
        
        // Activate thumbnails for the new module
        if (module.ViewModel is IThumbnailAware newAware)
        {
            newAware.OnThumbnailActivated();
        }

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
    private async Task OpenFeedbackAsync()
    {
        var dialogService = App.Services?.GetService<IDialogService>();
        if (dialogService is null) return;

        await dialogService.ShowFeedbackDialogAsync();
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
