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

    /// <summary>
    /// False until deferred startup (database init + module registration) has
    /// finished. The main window shows a lightweight loading overlay while false.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDisclaimer))]
    private bool _isStartupComplete;

    /// <summary>Ready-check list shown by the startup overlay (null only in design mode).</summary>
    [ObservableProperty]
    private StartupOverlayViewModel? _startupOverlay;

    [ObservableProperty]
    private ModuleItem? _selectedModule;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDisclaimer))]
    private bool _isDisclaimerAccepted;

    [ObservableProperty]
    private bool _disclaimerCheckboxChecked;

    /// <summary>
    /// Gates the disclaimer overlay so it can only appear after deferred startup
    /// has finished. <see cref="CheckDisclaimerStatusAsync"/> reads the database
    /// and now runs at the tail of deferred startup (Task 6), so showing the
    /// disclaimer any earlier would cover the "Starting DiffusionNexus…" overlay
    /// on every launch — even for users who accepted long ago — and would let the
    /// Continue button issue a DB write concurrent with the pool-thread DB
    /// migration on fresh installs.
    /// </summary>
    public bool ShowDisclaimer => IsStartupComplete && !IsDisclaimerAccepted;

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

    /// <summary>
    /// Community links shown directly in the sidebar: the whole list when it fits
    /// <see cref="CommunityLinkSlots.Count"/>, otherwise all but the last slot, which "More" takes.
    /// </summary>
    public ObservableCollection<CommunityLinkItemViewModel> InlineCommunityLinks { get; } = new();

    /// <summary>Community links behind the sidebar's "More" button; empty when the list fits.</summary>
    public ObservableCollection<CommunityLinkItemViewModel> OverflowCommunityLinks { get; } = new();

    public bool HasCommunityLinkOverflow => OverflowCommunityLinks.Count > 0;

    public string CommunityLinkOverflowLabel => $"More ({OverflowCommunityLinks.Count})";

    /// <summary>Opens a URL in the system browser. Replaceable so tests never launch one.</summary>
    internal Action<string> OpenExternalUrl { get; init; } = UrlLauncher.Open;

    private const string CommunityLinksLogSource = "CommunityLinks";

    public DiffusionNexusMainWindowViewModel()
    {
        // Disclaimer check is called externally after services are initialized

        // The compiled-in list renders until the remote one lands, and stays when it never does
        // (offline, timeout, bad document), so the sidebar is never empty.
        ApplyCommunityLinks(CommunityLink.Defaults);
    }

    /// <summary>
    /// Fetches the operator-edited community links (Gist-backed, through the SDK) and swaps them in.
    /// Never throws and never delays startup: on any failure the compiled-in list stays on screen.
    /// </summary>
    public Task LoadCommunityLinksAsync()
    {
        var service = App.Services?.GetService<ICommunityLinksService>();
        if (service is null)
        {
            return Task.CompletedTask;
        }

        return LoadCommunityLinksAsync(
            service,
            App.Services?.GetService<IUnifiedLogger>(),
            App.Services?.GetService<IUiScheduler>() ?? AvaloniaUiScheduler.Instance);
    }

    internal async Task LoadCommunityLinksAsync(ICommunityLinksService service, IUnifiedLogger? logger, IUiScheduler uiScheduler)
    {
        logger?.Debug(LogCategory.Network, CommunityLinksLogSource, "Fetching community links");

        try
        {
            var result = await service.GetLinksAsync();

            if (result.IsFallback)
            {
                // Info, not Warn: offline is a normal state and must not light the status bar every launch.
                logger?.Info(LogCategory.Network, CommunityLinksLogSource,
                    "Community links unavailable; keeping the built-in list", result.ErrorMessage);
                return;
            }

            await uiScheduler.InvokeAsync(() => ApplyCommunityLinks(result.Links));

            logger?.Info(LogCategory.Network, CommunityLinksLogSource,
                $"Loaded {result.Links.Count} community links ({InlineCommunityLinks.Count} in the sidebar, {OverflowCommunityLinks.Count} under More)");
        }
        catch (Exception ex)
        {
            // The service reports failures in its result and only throws on cancellation, which
            // nothing here requests; the swap itself runs binding handlers. Guarded as a whole:
            // the sidebar must never take startup down, and the failure belongs under this source.
            logger?.Warn(LogCategory.Network, CommunityLinksLogSource,
                "Community links update failed", ex.Message);
        }
    }

    /// <summary>Replaces the sidebar's community links with <paramref name="links"/>, split per <see cref="CommunityLinkSlots"/>.</summary>
    internal void ApplyCommunityLinks(IReadOnlyList<CommunityLink> links)
    {
        var (inline, overflow) = CommunityLinkSlots.Split(links);

        InlineCommunityLinks.Clear();
        foreach (var link in inline)
        {
            InlineCommunityLinks.Add(new CommunityLinkItemViewModel(link, OpenCommunityLink));
        }

        OverflowCommunityLinks.Clear();
        foreach (var link in overflow)
        {
            OverflowCommunityLinks.Add(new CommunityLinkItemViewModel(link, OpenCommunityLink));
        }

        OnPropertyChanged(nameof(HasCommunityLinkOverflow));
        OnPropertyChanged(nameof(CommunityLinkOverflowLabel));
    }

    private void OpenCommunityLink(CommunityLinkItemViewModel link)
    {
        App.Services?.GetService<IUnifiedLogger>()
            ?.Info(LogCategory.General, CommunityLinksLogSource, $"Opening community link '{link.Name}'", link.Url);
        OpenUrl(link.Url, CommunityLinksLogSource);
    }

    /// <summary>
    /// Every URL this window opens (sidebar links, banner actions) goes through here, so a machine
    /// with no default browser logs a warning instead of crashing a command on the UI thread.
    /// </summary>
    private void OpenUrl(string url, string source)
        => UrlLauncher.TryOpen(url, OpenExternalUrl, App.Services?.GetService<IUnifiedLogger>(), source);

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
                    ServerMessages.Add(new ServerMessageItemViewModel(msg, DismissServerMessage, url => OpenUrl(url, "ServerMessages")));
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
        }
    }

    /// <summary>
    /// Wires instance management (Start/Stop/Restart) into the Unified Console.
    /// Kept separate from <see cref="InitializeStatusBar"/> because it triggers
    /// DB-backed instance loading — <c>UnifiedConsoleViewModel.LoadInstancesAsync</c>
    /// reads the core database's <c>InstallerPackages</c> table — so it must run
    /// only after the databases have been initialized. Invoked from
    /// <c>App.CompleteStartupAsync</c> once DB init has completed.
    /// </summary>
    public void InitializeInstanceManagement()
    {
        if (StatusBar is null)
        {
            return;
        }

        var processManager = App.Services?.GetService<Services.PackageProcessManager>();
        if (processManager is not null && App.Services is not null)
        {
            StatusBar.InitializeInstanceManagement(processManager, App.Services);
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

        DeactivateCurrentModule();

        module.IsSelected = true;
        SelectedModule = module;
        CurrentModuleView = module.View;
        
        // Activate thumbnails for the new module
        if (module.ViewModel is IThumbnailAware newAware)
        {
            newAware.OnThumbnailActivated();
        }

        if (module.ViewModel is IModuleActivationAware activationAware)
        {
            activationAware.OnModuleActivated();
        }

        // Collapse the menu after selection
        IsMenuOpen = false;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        ShowAppLevelView(new SettingsView());
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
        ShowAppLevelView(new AboutView());
    }

    /// <summary>
    /// Shows a view that is not a registered module (Settings, About).
    /// </summary>
    /// <remarks>
    /// Performs the same teardown as <see cref="NavigateToModule"/>: without it the outgoing
    /// module keeps its selection highlight while an unrelated screen is displayed, and an
    /// <see cref="IThumbnailAware"/> module keeps its thumbnail pipeline running behind a view
    /// that is no longer on screen.
    /// </remarks>
    private void ShowAppLevelView(object view)
    {
        DeactivateCurrentModule();

        SelectedModule = null;
        CurrentModuleView = view;
        IsMenuOpen = false;
    }

    /// <summary>
    /// Tears the outgoing module down: stops its thumbnail pipeline and clears every selection
    /// highlight.
    /// </summary>
    /// <remarks>
    /// Shared by <see cref="NavigateToModule"/> and <see cref="ShowAppLevelView"/>, which had
    /// two copies of it. One place to add the next teardown step means it cannot be added to
    /// one path and forgotten on the other - the failure mode being a module left running
    /// behind a screen that replaced it.
    /// </remarks>
    private void DeactivateCurrentModule()
    {
        if (SelectedModule?.ViewModel is IThumbnailAware previousAware)
        {
            previousAware.OnThumbnailDeactivated();
        }

        foreach (var module in Modules)
        {
            module.IsSelected = false;
        }
    }

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
