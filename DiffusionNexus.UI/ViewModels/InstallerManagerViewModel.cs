using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;


namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Installer Manager module.
/// Loads saved installations from the database and displays them as cards.
/// </summary>
public partial class InstallerManagerViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly IUnitOfWork _unitOfWork;

    /// <summary>
    /// Creates a fresh unit of work (own DbContext) for one destructive operation.
    /// The VM's shared <see cref="_unitOfWork"/> lives for the whole app session, so
    /// its identity map goes stale (rows deleted/edited elsewhere linger) and a failed
    /// save would leave poisoned Deleted-state entries that a later unrelated save
    /// replays. Null in tests that pass only the shared instance.
    /// </summary>
    private readonly Func<IUnitOfWork>? _unitOfWorkFactory;

    private readonly PackageProcessManager _processManager;
    private readonly IDatasetEventAggregator _eventAggregator;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IConfigurationCheckerService _checkerService;
    private readonly IWorkloadInstallService _installService;
    private readonly IEnumerable<IInstallerUpdateService> _updateServices;
    private readonly IUnifiedLogger _unifiedLogger;
    private readonly DiffusionNexus.Inference.Captioning.CaptioningModelManager? _captioningModelManager;
    private readonly ICaptioningService? _captioningService;
    private readonly IActivityLogService? _activityLogService;
    private readonly IDownloadCoordinator? _downloadCoordinator;
    private readonly Services.Diffusion.BaseModelFolderRegistrar? _baseModelFolderRegistrar;
    private readonly Services.Engine.IManagedEngineInstaller? _engineInstaller;
    private readonly IResourceMonitorService? _resourceMonitor;

    /// <summary>
    /// Raised when the unified console panel should be opened (e.g., during an update).
    /// </summary>
    public event EventHandler? UnifiedConsolePanelRequested;

    /// <summary>
    /// Raised when an installer package update state changes (started, completed, failed).
    /// Other components (e.g., Unified Console) subscribe to keep their UI in sync.
    /// </summary>
    public event EventHandler<InstallerUpdateStateEventArgs>? InstallerUpdateStateChanged;

    [ObservableProperty]
    private string _welcomeMessage = "Welcome to the Installer Manager!";

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// The collection of installer cards displayed in the view.
    /// </summary>
    public ObservableCollection<InstallerPackageCardViewModel> InstallerCards { get; } = [];

    /// <summary>
    /// True when there are no installations to show.
    /// </summary>
    public bool IsEmpty => InstallerCards.Count == 0 && !IsLoading;

    /// <summary>
    /// Whether the Diffusion Nexus Engine tile is shown. Bound at startup to the same
    /// hamburger switch that reveals the Diffusion Canvas, so both surfaces stay hidden
    /// together while the feature is unfinished.
    /// </summary>
    [ObservableProperty]
    private bool _isEngineTileVisible;

    /// <summary>
    /// True for the duration of <see cref="InstallEngineAsync"/>. Guards against two hazards
    /// flagged in review: (1) <see cref="OnIsEngineTileVisibleChanged"/> reloading the whole
    /// <see cref="InstallerCards"/> list mid-install, which would detach the live card the
    /// install's progress callbacks and final <c>IsEngineInstalled = true</c> are writing to —
    /// the callbacks would then land on an orphaned VM while a freshly-built card shows the
    /// Install button, one click from a second concurrent SDK install into the same folder; and
    /// (2) that reload running <see cref="LoadInstallationsAsync"/> concurrently with itself over
    /// the VM's single shared <c>IUnitOfWork</c> — the same hazard documented in App.axaml.cs as
    /// the reason startup loading was serialized.
    /// </summary>
    private bool _isEngineInstallInFlight;

    partial void OnIsEngineTileVisibleChanged(bool value)
    {
        // Skip the reload while an install is running — see _isEngineInstallInFlight's doc.
        // InstallEngineAsync catches up once it's done, if the switch actually moved meanwhile.
        if (_isEngineInstallInFlight) return;
        _ = LoadInstallationsCommand.ExecuteAsync(null);
    }

    public InstallerManagerViewModel(
        IDialogService dialogService,
        IUnitOfWork unitOfWork,
        PackageProcessManager processManager,
        IDatasetEventAggregator eventAggregator,
        IConfigurationRepository configurationRepository,
        IConfigurationCheckerService checkerService,
        IWorkloadInstallService installService,
        IEnumerable<IInstallerUpdateService> updateServices,
        IUnifiedLogger unifiedLogger,
        DiffusionNexus.Inference.Captioning.CaptioningModelManager? captioningModelManager = null,
        ICaptioningService? captioningService = null,
        IActivityLogService? activityLogService = null,
        IDownloadCoordinator? downloadCoordinator = null,
        Services.Diffusion.BaseModelFolderRegistrar? baseModelFolderRegistrar = null,
        Func<IUnitOfWork>? unitOfWorkFactory = null,
        Services.Engine.IManagedEngineInstaller? engineInstaller = null,
        IResourceMonitorService? resourceMonitor = null)
    {
        _dialogService = dialogService;
        _unitOfWork = unitOfWork;
        _unitOfWorkFactory = unitOfWorkFactory;
        _processManager = processManager;
        _eventAggregator = eventAggregator;
        _configurationRepository = configurationRepository;
        _checkerService = checkerService;
        _installService = installService;
        _updateServices = updateServices;
        _unifiedLogger = unifiedLogger;
        _captioningModelManager = captioningModelManager;
        _captioningService = captioningService;
        _activityLogService = activityLogService;
        _downloadCoordinator = downloadCoordinator;
        _baseModelFolderRegistrar = baseModelFolderRegistrar;
        _engineInstaller = engineInstaller;
        _resourceMonitor = resourceMonitor;

        InstallerCards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));

        // Wire process manager events
        _processManager.OutputReceived += OnProcessOutput;
        _processManager.RunningStateChanged += OnRunningStateChanged;
        _processManager.WebUrlDetected += OnWebUrlDetected;
    }

    /// <summary>
    /// Loads all saved installations from the database.
    /// </summary>
    [RelayCommand]
    private async Task LoadInstallationsAsync()
    {
        try
        {
            IsLoading = true;
            InstallerCards.Clear();

            // Static "Diffusion Nexus Core" entry — the in-process captioning /
            // embedding / future capabilities. Pinned to the top of the list so
            // users find Workloads downloads regardless of how many installations
            // they have. No backing DB row; the Workloads dialog is the only
            // surfaced action.
            InstallerCards.Add(CreateCoreCard());

            var packages = await _unitOfWork.InstallerPackages.GetAllAsync();

            // The app-owned engine is an ordinary ComfyUI row flagged IsAppManaged. It is
            // rendered as the static engine tile and must not also appear as a normal card.
            // The tile itself is gated behind IsEngineTileVisible — while the switch is off
            // it is omitted from the list entirely rather than hidden in the view.
            if (IsEngineTileVisible)
            {
                var enginePackage = packages.FirstOrDefault(p => p.IsAppManaged);
                InstallerCards.Add(CreateEngineCard(enginePackage));
            }

            foreach (var package in packages.Where(p => !p.IsAppManaged))
            {
                InstallerCards.Add(CreateCard(package));
            }

            // Check for updates in the background after loading
            _ = CheckAllCardsForUpdatesAsync();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load installations from database");
            await _dialogService.ShowMessageAsync("Error", $"Failed to load installations: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddExistingInstallationAsync()
    {
        var path = await _dialogService.ShowOpenFolderDialogAsync("Select Installation Folder");
        if (string.IsNullOrEmpty(path)) return;

        var result = await _dialogService.ShowAddExistingInstallationDialogAsync(path);
        if (result.IsCancelled) return;

        var package = new InstallerPackage
        {
            Name = result.Name,
            InstallationPath = result.InstallationPath,
            Type = result.Type,
            ExecutablePath = result.ExecutablePath,
            Version = result.Version,
            Branch = result.Branch,
            Arguments = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _unitOfWork.InstallerPackages.AddAsync(package);
            await _unitOfWork.SaveChangesAsync();

            // Link or create an ImageGallery for the output folder
            if (!string.IsNullOrWhiteSpace(result.OutputFolderPath))
            {
                await LinkOutputFolderAsync(package, result.OutputFolderPath);
                await _unitOfWork.SaveChangesAsync();

                // Notify other components (Settings dialog, Generation Gallery) about the new gallery
                _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
            }

            // Register the installation's model folders as Base Model Folders
            // (for ComfyUI this includes every extra_model_paths.yaml root).
            // Non-fatal by design — EnsureRegisteredAsync logs and swallows failures.
            if (_baseModelFolderRegistrar is not null
                && await _baseModelFolderRegistrar.EnsureRegisteredAsync([package]) > 0)
            {
                // Let an open Settings page reload its Base Model Folders list.
                _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
            }

            var card = CreateCard(package);
            InstallerCards.Add(card);

            _unifiedLogger.Info(LogCategory.Installation, package.Name,
                $"Added existing installation at {package.InstallationPath}.");

            // Notify other surfaces (e.g. Unified Console's docked-instance bar)
            // so they pick up the new package without an app restart.
            _eventAggregator.PublishInstallerPackagesChanged(new InstallerPackagesChangedEventArgs());

            // Run an update check for the newly added card in the background, so
            // the Update button appears without waiting for an app restart.
            _ = CheckCardForUpdatesAsync(card);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to save installation {Name}", package.Name);
            _unifiedLogger.Error(LogCategory.Installation, package.Name,
                $"Failed to add installation: {ex.Message}", ex);
            await _dialogService.ShowMessageAsync("Error", $"Failed to save installation: {ex.Message}");
        }
    }

    /// <summary>
    /// Links an existing ImageGallery (by FolderPath) to the package,
    /// or creates a new one if none exists.
    /// If the matching gallery is already linked to another installer, the FK is updated
    /// to point to this package (an ImageGallery can only belong to one installer).
    /// </summary>
    private async Task LinkOutputFolderAsync(InstallerPackage package, string outputFolderPath)
    {
        var settings = await _unitOfWork.AppSettings.GetSettingsAsync();
        var appSettingsId = settings?.Id ?? 1;

        // Check if a gallery with this path already exists
        var allSettings = await _unitOfWork.AppSettings.GetSettingsWithIncludesAsync();
        var existing = allSettings.ImageGalleries
            .FirstOrDefault(g => string.Equals(g.FolderPath, outputFolderPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // Re-link the existing gallery to this package
            existing.InstallerPackageId = package.Id;
        }
        else
        {
            // Create a new gallery
            var gallery = new ImageGallery
            {
                AppSettingsId = appSettingsId,
                FolderPath = outputFolderPath,
                IsEnabled = true,
                Order = allSettings.ImageGalleries.Count,
                InstallerPackageId = package.Id
            };
            await _unitOfWork.AppSettings.AddImageGalleryAsync(gallery);
        }
    }

    [RelayCommand]
    private async Task StartNewInstallationAsync()
    {
        await _dialogService.ShowMessageAsync("Coming Soon", "New installation feature is under development.");
    }

    private InstallerPackageCardViewModel CreateCard(InstallerPackage package)
    {
        var card = new InstallerPackageCardViewModel(package);
        card.LaunchRequested += OnLaunchRequestedAsync;
        card.StopRequested += OnStopRequestedAsync;
        card.RestartRequested += OnRestartRequestedAsync;
        card.RemoveRequested += OnRemoveRequestedAsync;
        card.DeleteFromDiskRequested += OnDeleteFromDiskRequestedAsync;
        card.OpenFolderRequested += OnOpenFolderRequested;
        card.SettingsRequested += OnSettingsRequestedAsync;
        card.ConsoleRequested += OnConsoleRequestedAsync;
        card.MakeDefaultRequested += OnMakeDefaultRequestedAsync;
        card.WorkloadsRequested += OnWorkloadsRequestedAsync;
        card.UpdateRequested += OnUpdateRequestedAsync;

        // Check if the installation folder still exists on disk
        card.IsMissing = !Directory.Exists(package.InstallationPath);

        // Restore running state if the process is still alive
        if (!card.IsMissing && _processManager.IsRunning(card.Id))
        {
            card.IsRunning = true;
            card.DetectedWebUrl = _processManager.GetDetectedUrl(card.Id);
            foreach (var line in _processManager.GetOutput(card.Id))
                card.ConsoleLines.Add(line);
        }

        return card;
    }

    // ── Process event handlers ──

    private void OnProcessOutput(int packageId, ConsoleOutputLine line)
    {
        var card = FindCard(packageId);
        card?.AppendConsoleLine(line);
    }

    private void OnRunningStateChanged(int packageId, bool running)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var card = FindCard(packageId);
            if (card is null) return;

            card.IsRunning = running;
        });
    }

    private void OnWebUrlDetected(int packageId, string url)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var card = FindCard(packageId);
            if (card is not null)
                card.DetectedWebUrl = url;
        });
    }

    private InstallerPackageCardViewModel? FindCard(int packageId)
    {
        foreach (var card in InstallerCards)
        {
            if (card.Id == packageId)
                return card;
        }
        return null;
    }

    // ── Card action handlers ──

    private async Task OnLaunchRequestedAsync(InstallerPackageCardViewModel card)
    {
        if (string.IsNullOrWhiteSpace(card.ExecutablePath))
        {
            await _dialogService.ShowMessageAsync("No Executable", "No executable path configured for this installation.");
            return;
        }

        var fullPath = Path.Combine(card.InstallationPath, card.ExecutablePath);
        if (!File.Exists(fullPath))
        {
            await _dialogService.ShowMessageAsync("Not Found", $"Executable not found: {fullPath}");
            return;
        }

        card.ConsoleLines.Clear();
        _processManager.Launch(card.Id, fullPath, card.InstallationPath, card.Arguments, card.Type);
    }

    private async Task OnStopRequestedAsync(InstallerPackageCardViewModel card)
    {
        await _processManager.StopAsync(card.Id);
    }

    private async Task OnRestartRequestedAsync(InstallerPackageCardViewModel card)
    {
        card.ConsoleLines.Clear();
        card.DetectedWebUrl = null;
        await _processManager.RestartAsync(card.Id);
    }

    /// <summary>
    /// Rents a unit of work for one destructive operation — fresh (owned, must be
    /// disposed) when a factory is available, the shared instance otherwise.
    /// </summary>
    private (IUnitOfWork UnitOfWork, bool Owned) RentUnitOfWork() =>
        _unitOfWorkFactory is null ? (_unitOfWork, false) : (_unitOfWorkFactory(), true);

    private async Task OnRemoveRequestedAsync(InstallerPackageCardViewModel card)
    {
        if (_processManager.IsRunning(card.Id))
        {
            await _dialogService.ShowMessageAsync("Cannot Remove", "Please stop the running process before removing.");
            return;
        }

        var (unitOfWork, owned) = RentUnitOfWork();
        try
        {
            var entity = await unitOfWork.InstallerPackages.GetByIdAsync(card.Id);
            if (entity is null)
            {
                // Stale card without a DB row — nothing to clean up, just drop it.
                InstallerCards.Remove(card);
                return;
            }

            // Model roots other registered installations still discover must never be
            // offered for removal — their startup backfill would silently re-add them.
            // ResolveModelRoots reads extra_model_paths.yaml files: keep it off the UI thread.
            var allPackages = await unitOfWork.InstallerPackages.GetAllAsync();
            var otherPackages = allPackages.Where(p => p.Id != entity.Id).ToList();
            var protectedRoots = await Task.Run(() => otherPackages
                .SelectMany(Services.Diffusion.BaseModelFolderRegistrar.ResolveModelRoots)
                .ToList());

            // Resolve the settings folders tied to this installation so the dialog can
            // offer to remove them too (galleries/base folders by FK, LoRA sources by path).
            var settings = await unitOfWork.AppSettings.GetSettingsWithIncludesAsync();
            var plan = InstallationSettingsCleanup.Resolve(settings, entity, protectedRoots);

            var choice = await _dialogService.ShowRemoveInstallationDialogAsync(new RemoveInstallationPrompt(
                card.Name,
                plan.Galleries.Select(g => g.FolderPath).ToList(),
                plan.LoraSources.Select(s => s.FolderPath).ToList(),
                plan.BaseModelFolders.Select(f => f.FolderPath).ToList()));

            if (!choice.Confirmed) return;

            try
            {
                var removedFolders = 0;
                if (choice.RemoveGalleries)
                {
                    foreach (var gallery in plan.Galleries)
                    {
                        unitOfWork.AppSettings.RemoveImageGallery(gallery);
                        removedFolders++;
                    }
                }
                if (choice.RemoveLoraSources)
                {
                    foreach (var source in plan.LoraSources)
                    {
                        unitOfWork.AppSettings.RemoveLoraSource(source);
                        removedFolders++;
                    }
                }
                if (choice.RemoveBaseModelFolders)
                {
                    foreach (var folder in plan.BaseModelFolders)
                    {
                        unitOfWork.AppSettings.RemoveBaseModelFolder(folder);
                        removedFolders++;
                    }
                }

                unitOfWork.InstallerPackages.Remove(entity);
                await unitOfWork.SaveChangesAsync();

                InstallerCards.Remove(card);

                _unifiedLogger.Info(LogCategory.Installation, card.Name,
                    removedFolders > 0
                        ? $"Removed from Installer Manager together with {removedFolders} linked settings folder(s). Files on disk were left untouched."
                        : "Removed from Installer Manager. Files on disk were left untouched.");

                if (removedFolders > 0)
                {
                    // Let the Settings page / galleries / LoRA viewer reload their folder lists.
                    _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
                }

                _eventAggregator.PublishInstallerPackagesChanged(new InstallerPackagesChangedEventArgs());
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Failed to remove installation {Name}", card.Name);
                _unifiedLogger.Error(LogCategory.Installation, card.Name,
                    $"Failed to remove installation: {ex.Message}", ex);
                await _dialogService.ShowMessageAsync("Error", $"Failed to remove installation: {ex.Message}");
            }
        }
        finally
        {
            // Owned = fresh context; disposing on failure also discards any staged
            // deletes so no later unrelated save can replay them.
            if (owned) unitOfWork.Dispose();
        }
    }

    private Task OnConsoleRequestedAsync(InstallerPackageCardViewModel card)
    {
        UnifiedConsolePanelRequested?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private async Task OnSettingsRequestedAsync(InstallerPackageCardViewModel card)
    {
        // Load the package with its linked gallery to get the output folder path
        var entity = await _unitOfWork.InstallerPackages.GetByIdWithGalleryAsync(card.Id);
        if (entity is null) return;

        var currentOutputFolder = entity.ImageGallery?.FolderPath ?? string.Empty;

        var result = await _dialogService.ShowEditInstallationDialogAsync(
            card.Name,
            card.InstallationPath,
            card.Type,
            card.ExecutablePath ?? string.Empty,
            currentOutputFolder);

        if (result.IsCancelled) return;

        try
        {
            entity.Name = result.Name;
            entity.InstallationPath = result.InstallationPath;
            entity.Type = result.Type;
            entity.ExecutablePath = result.ExecutablePath;

            await _unitOfWork.SaveChangesAsync();

            // Update the card VM to reflect changes
            card.Name = result.Name;
            card.InstallationPath = result.InstallationPath;
            card.Type = result.Type;
            card.ExecutablePath = result.ExecutablePath;

            // Update the output folder link if changed
            if (!string.IsNullOrWhiteSpace(result.OutputFolderPath))
            {
                await LinkOutputFolderAsync(entity, result.OutputFolderPath);
                await _unitOfWork.SaveChangesAsync();

                // Notify other components (Settings dialog, Generation Gallery) about the updated gallery
                _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to update installation {Name}", card.Name);
            await _dialogService.ShowMessageAsync("Error", $"Failed to update installation: {ex.Message}");
        }
    }

    private async Task OnDeleteFromDiskRequestedAsync(InstallerPackageCardViewModel card)
    {
        if (_processManager.IsRunning(card.Id))
        {
            await _dialogService.ShowMessageAsync("Cannot Delete", "Please stop the running process before deleting.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Delete from Disk",
            $"Are you sure you want to permanently delete \"{card.Name}\"?\n\n" +
            $"Path: {card.InstallationPath}\n\n" +
            "This will delete the folder on your hard drive including models and generated images. " +
            "Settings folders that live in it (gallery, LoRA sources, base model folders) are removed from the settings as well.\n\n" +
            "⚠ This action CANNOT be undone.");

        if (!confirmed) return;

        var (unitOfWork, owned) = RentUnitOfWork();
        try
        {
            // Remove from database first — including the settings rows tied to this
            // installation: their folders cease to exist, so unlike the plain remove
            // flow there is no choice to offer (dead rows would only linger as ⚠
            // badges and dangling scan/download targets).
            var removedFolders = 0;
            var entity = await unitOfWork.InstallerPackages.GetByIdAsync(card.Id);
            if (entity is not null)
            {
                var settings = await unitOfWork.AppSettings.GetSettingsWithIncludesAsync();
                var plan = InstallationSettingsCleanup.Resolve(settings, entity);

                foreach (var gallery in plan.Galleries)
                {
                    unitOfWork.AppSettings.RemoveImageGallery(gallery);
                    removedFolders++;
                }
                foreach (var source in plan.LoraSources)
                {
                    unitOfWork.AppSettings.RemoveLoraSource(source);
                    removedFolders++;
                }
                foreach (var folder in plan.BaseModelFolders)
                {
                    unitOfWork.AppSettings.RemoveBaseModelFolder(folder);
                    removedFolders++;
                }

                unitOfWork.InstallerPackages.Remove(entity);
                await unitOfWork.SaveChangesAsync();
            }

            InstallerCards.Remove(card);

            // Delete from disk – clear read-only attributes first (e.g. .git pack files)
            // TODO: Linux Implementation - verify ForceDeleteDirectory works cross-platform
            if (Directory.Exists(card.InstallationPath))
            {
                await Task.Run(() => ForceDeleteDirectory(card.InstallationPath));
                Serilog.Log.Information("Deleted installation folder: {Path}", card.InstallationPath);
            }

            _unifiedLogger.Info(LogCategory.Installation, card.Name,
                removedFolders > 0
                    ? $"Deleted installation, removed folder {card.InstallationPath} and {removedFolders} linked settings folder(s)."
                    : $"Deleted installation and removed folder {card.InstallationPath}.");

            if (removedFolders > 0)
            {
                _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
            }

            _eventAggregator.PublishInstallerPackagesChanged(new InstallerPackagesChangedEventArgs());
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to delete installation {Name} from disk", card.Name);
            _unifiedLogger.Error(LogCategory.Installation, card.Name,
                $"Failed to delete from disk: {ex.Message}", ex);
            await _dialogService.ShowMessageAsync("Error", $"Failed to delete: {ex.Message}\n\nSome files may have been partially removed.");
        }
        finally
        {
            if (owned) unitOfWork.Dispose();
        }
    }

    private async Task OnMakeDefaultRequestedAsync(InstallerPackageCardViewModel card)
    {
        try
        {
            // Clear existing default for this installer type
            await _unitOfWork.InstallerPackages.ClearDefaultByTypeAsync(card.Type);

            // Set the selected package as default
            var entity = await _unitOfWork.InstallerPackages.GetByIdAsync(card.Id);
            if (entity is null) return;

            entity.IsDefault = true;
            await _unitOfWork.SaveChangesAsync();

            // Update all cards of the same type
            foreach (var c in InstallerCards)
            {
                if (c.Type == card.Type)
                    c.IsDefault = c.Id == card.Id;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to set default installation {Name}", card.Name);
            await _dialogService.ShowMessageAsync("Error", $"Failed to set default: {ex.Message}");
        }
    }

    private async Task OnWorkloadsRequestedAsync(InstallerPackageCardViewModel card)
    {
        // Diffusion Nexus Core has no ComfyUI-style workloads (custom nodes /
        // model bundles), so the heavy ConfigurationChecker pipeline does not
        // apply — running it against an empty install path is what made the
        // dialog appear stuck. Show a focused captioning-models view instead.
        if (card.IsCore)
        {
            await ShowCoreCaptioningOverviewAsync();
            return;
        }

        if (card.IsEngine)
        {
            if (!card.IsEngineInstalled || string.IsNullOrWhiteSpace(card.InstallationPath))
            {
                await _dialogService.ShowMessageAsync("Diffusion Nexus Engine",
                    "Install the engine first — workloads are installed into it.");
                return;
            }

            await ShowWorkloadsDialogAsync(card.InstallationPath,
                Services.Engine.EngineWorkloadCatalog.WorkloadIds);
            return;
        }

        await ShowWorkloadsDialogAsync(card.InstallationPath, null);
    }

    /// <summary>
    /// Opens the workloads dialog against a ComfyUI root. <paramref name="allowedConfigurationIds"/>
    /// narrows the list to the curated engine workloads; null shows every ComfyUI workload.
    /// </summary>
    private async Task ShowWorkloadsDialogAsync(string comfyUiRoot, IReadOnlyList<Guid>? allowedConfigurationIds)
    {
        try
        {
            var vm = new WorkloadsViewModel(
                _configurationRepository, _checkerService, _installService,
                comfyUiRoot, allowedConfigurationIds, _resourceMonitor);
            await vm.LoadWorkloadsCommand.ExecuteAsync(null);

            var dialog = new Views.Dialogs.WorkloadsDialog { DataContext = vm };

            var parentWindow = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            if (parentWindow is not null)
                await dialog.ShowDialog(parentWindow);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open workloads dialog for {Path}", comfyUiRoot);
            await _dialogService.ShowMessageAsync("Error", $"Failed to load workloads: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the Diffusion Nexus Core captioning models dialog. Uses the same
    /// dark-themed DataGrid layout as the ComfyUI workloads dialog so the two
    /// surfaces feel consistent, but pulls its data from
    /// <see cref="DiffusionNexus.Inference.Captioning.CaptioningModelManager"/>
    /// — which now scans every registered ComfyUI installation (including
    /// paths declared in <c>extra_model_paths.yaml</c>) for matching GGUF and
    /// mmproj files. No ComfyUI configuration checker runs here, so the
    /// dialog can never appear "stuck" against an empty install path.
    /// </summary>
    private async Task ShowCoreCaptioningOverviewAsync()
    {
        try
        {
            // Prefer the DI-managed manager (which has the ComfyUI path
            // discovery wired in). When tests construct the viewmodel without
            // it, fall back to a standalone manager so the action still works.
            var manager = _captioningModelManager
                ?? new DiffusionNexus.Inference.Captioning.CaptioningModelManager();
            var service = _captioningService
                ?? new DiffusionNexus.Inference.Captioning.CaptioningService(manager);

            var parentWindow = (Avalonia.Application.Current?.ApplicationLifetime
                as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;

            // Options picker callback: opens the combined VRAM + destination +
            // disk-space dialog and returns both the tier and the target
            // directory the user chose (or null on cancel). The destinations
            // list is built from the manager's known search paths — Core
            // default first, then any registered ComfyUI install roots and
            // their extra_model_paths.yaml entries.
            Func<DiffusionNexus.Domain.Enums.CaptioningModelType, int[], Task<CaptioningDownloadChoice?>> optionsPicker =
                async (modelType, tiers) =>
            {
                // Non-tiered models pass an empty tier array — the options
                // dialog hides its VRAM picker and only asks for a destination.
                // Don't bail out here, that was the bug that made LLaVA /
                // Qwen 2.5 / Qwen 3 vanilla Download buttons silently do
                // nothing.
                tiers ??= Array.Empty<int>();

                var destinations = manager.GetDownloadDestinations();
                var optionsVm = new CaptioningDownloadOptionsViewModel(manager, modelType, tiers, destinations);

                var optionsDialog = new Views.Dialogs.CaptioningDownloadOptionsDialog
                {
                    DataContext = optionsVm
                };

                if (parentWindow is not null)
                {
                    await optionsDialog.ShowDialog(parentWindow);
                }
                else
                {
                    optionsDialog.Show();
                    await Task.Yield();
                }

                if (optionsDialog.SelectedVramGb is null || optionsDialog.SelectedDestination is null)
                {
                    return null;
                }

                return new CaptioningDownloadChoice(
                    optionsDialog.SelectedVramGb.Value,
                    optionsDialog.SelectedDestination);
            };

            var captioningVm = new CaptioningModelsDialogViewModel(manager, service, _downloadCoordinator, optionsPicker);

            // Host both the guided pipelines (e.g. Anime-To-Real) and the captioning models under
            // the static Diffusion Nexus Core instance, in a tabbed dialog. The pipeline tab is
            // backed by the app-side pipeline asset installer (HF token + Civitai modelId aware).
            var coreVm = new CoreWorkloadsViewModel(
                captioningVm,
                App.Services?.GetService<Services.Pipelines.IPipelineManifestProvider>(),
                App.Services?.GetService<Services.Pipelines.IPipelineAssetInstaller>(),
                App.Services?.GetService<Services.Diffusion.IModelFolderCatalog>());
            await coreVm.LoadCommand.ExecuteAsync(null);

            var dialog = new Views.Dialogs.CoreWorkloadsDialog
            {
                DataContext = coreVm
            };

            if (parentWindow is not null)
            {
                await dialog.ShowDialog(parentWindow);
            }
            else
            {
                dialog.Show();
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open Diffusion Nexus Core captioning dialog");
            await _dialogService.ShowMessageAsync("Diffusion Nexus Core",
                $"Could not open captioning models view: {ex.Message}");
        }
    }

    /// <summary>
    /// Builds the singleton "Diffusion Nexus Core" card pinned to the top of
    /// the manager. Wires only the Workloads handler — the other actions
    /// (Launch/Stop/Update/etc.) are unsupported on Core and the corresponding
    /// buttons are hidden in the view.
    /// </summary>
    private InstallerPackageCardViewModel CreateCoreCard()
    {
        var card = InstallerPackageCardViewModel.CreateCoreCard();
        card.WorkloadsRequested += OnWorkloadsRequestedAsync;
        return card;
    }

    /// <summary>
    /// Builds the static "Diffusion Nexus Engine" tile. Wires the install handler and, once
    /// the engine exists, the workloads handler. Visibility is controlled by
    /// <see cref="IsEngineTileVisible"/>, which follows the same switch as the Diffusion Canvas.
    /// </summary>
    private InstallerPackageCardViewModel CreateEngineCard(InstallerPackage? enginePackage)
    {
        var card = InstallerPackageCardViewModel.CreateEngineCard(enginePackage);
        card.EngineInstallRequested += OnEngineInstallRequestedAsync;
        card.WorkloadsRequested += OnWorkloadsRequestedAsync;
        return card;
    }

    private Task OnEngineInstallRequestedAsync(InstallerPackageCardViewModel card) => InstallEngineAsync();

    /// <summary>
    /// Installs the base engine: asks for a target folder, runs the SDK install with all content
    /// excluded, and on success records it as an app-managed ComfyUI installation. Progress goes
    /// to the Unified Console so a stalled install shows its last successful step.
    /// </summary>
    public async Task InstallEngineAsync()
    {
        var card = InstallerCards.FirstOrDefault(c => c.IsEngine);
        if (card is null || _engineInstaller is null) return;

        // Guards re-entrancy: a second Install click routed through a stale card reference (or a
        // freshly-built one, if the reload guard in OnIsEngineTileVisibleChanged were ever
        // bypassed) must never start a second SDK install writing into the same folder while one
        // is already running. Set before the folder dialog so it also covers the window while
        // that's up, and cleared unconditionally in the outer finally below — every exit path
        // from here on, cancelled pick, thrown exception, or a completed install, reaches it.
        if (_isEngineInstallInFlight) return;
        _isEngineInstallInFlight = true;
        var visibilityAtStart = IsEngineTileVisible;

        // Outer try/finally guarantees the in-flight flag is cleared on every exit from here
        // on, including an exception from the folder picker itself — ShowOpenFolderDialogAsync
        // sat outside any try/finally before this fix, so a throw there left the flag stuck for
        // the rest of the session: every later Install click became a silent no-op, and the
        // Canvas visibility toggle stopped reloading the card list (OnIsEngineTileVisibleChanged
        // also honors this flag).
        try
        {
            var folder = await _dialogService.ShowOpenFolderDialogAsync(
                $"Choose where to install the Diffusion Nexus Engine (default: {Services.Engine.ManagedEngineLocator.DefaultInstallRoot})");

            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            UnifiedConsolePanelRequested?.Invoke(this, EventArgs.Empty);

            card.IsEngineInstalling = true;
            card.EngineProgressPercent = 0;
            card.EngineStatusMessage = "Starting engine install...";
            _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine",
                $"Starting base engine install into {folder}.");

            var logProgress = new Progress<DiffusionNexus.Installer.SDK.Services.InstallLogEntry>(entry =>
            {
                card.EngineStatusMessage = entry.Message;
                _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine", entry.Message);
            });

            var stepProgress = new Progress<DiffusionNexus.Installer.SDK.Services.InstallationProgress>(p =>
            {
                card.EngineProgressPercent = p.ProgressPercentage;
                card.EngineStatusMessage = $"[{p.StepIndex + 1}/{p.TotalSteps}] {p.Message}";
            });

            try
            {
                var sharedRoots = await ResolveSharedModelRootsAsync();

                var outcome = await _engineInstaller.InstallBaseEngineAsync(
                    new Services.Engine.EngineInstallRequest(folder, sharedRoots),
                    logProgress, stepProgress, CancellationToken.None);

                if (outcome.IsSuccess)
                {
                    // Reuse the existing app-managed row instead of always appending. A stale row
                    // (folder gone) is exactly what put the tile back in front of the user with an
                    // Install button in the first place — unconditionally adding a second IsAppManaged
                    // row here would leave the stale one as the older, first-returned row for both
                    // this tile's own load path and the Canvas engine-root resolver in App.axaml.cs
                    // (both do a plain FirstOrDefault(p => p.IsAppManaged)), so the successful
                    // reinstall would still look and behave like a dead install. Updating the row in
                    // place (rather than removing + re-adding) also preserves its Id, so any settings
                    // that ever get FK-linked to it (galleries, base model folders — see the ordinary
                    // Remove flow above for what that can include) stay attached instead of being
                    // orphaned by a delete.
                    var appManagedRows = (await _unitOfWork.InstallerPackages.GetAllAsync())
                        .Where(p => p.IsAppManaged)
                        .ToList();

                    var package = appManagedRows.FirstOrDefault();
                    if (package is null)
                    {
                        package = new InstallerPackage
                        {
                            Name = "Diffusion Nexus Engine",
                            InstallationPath = outcome.RepositoryPath ?? folder,
                            Type = InstallerType.ComfyUI,
                            ExecutablePath = null,
                            Arguments = string.Empty,
                            IsAppManaged = true,
                            CreatedAt = DateTimeOffset.UtcNow
                        };
                        await _unitOfWork.InstallerPackages.AddAsync(package);
                    }
                    else
                    {
                        package.Name = "Diffusion Nexus Engine";
                        package.InstallationPath = outcome.RepositoryPath ?? folder;
                        package.Type = InstallerType.ComfyUI;
                        package.ExecutablePath = null;
                        package.Arguments = string.Empty;
                        package.IsAppManaged = true;
                        package.CreatedAt = DateTimeOffset.UtcNow;
                        _unitOfWork.InstallerPackages.Update(package);

                        // Defensive: a pre-fix build could already have left more than one
                        // app-managed row behind. Converge on exactly one going forward.
                        foreach (var duplicate in appManagedRows.Skip(1))
                            _unitOfWork.InstallerPackages.Remove(duplicate);
                    }

                    await _unitOfWork.SaveChangesAsync();

                    card.InstallationPath = package.InstallationPath;
                    card.IsEngineInstalled = true;
                    card.VersionDisplay = "App-managed";

                    _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine",
                        $"Engine installed at {package.InstallationPath}.");
                    _eventAggregator.PublishInstallerPackagesChanged(new InstallerPackagesChangedEventArgs());
                }
                else if (outcome.IsCancelled)
                {
                    _unifiedLogger.Info(LogCategory.Installation, "Diffusion Nexus Engine",
                        "Engine install cancelled by the user. Nothing was registered.");
                }
                else
                {
                    _unifiedLogger.Error(LogCategory.Installation, "Diffusion Nexus Engine",
                        $"Engine install failed: {outcome.Message}");

                    // A genuine failure (not a cancel) is worth reporting — offer the existing
                    // feedback dialog instead of leaving the user with a dead end.
                    var report = await _dialogService.ShowConfirmAsync("Engine install failed",
                        $"{outcome.Message}\n\nSend a report so this can be fixed?");
                    if (report)
                        await _dialogService.ShowFeedbackDialogAsync();
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "Diffusion Nexus Engine install failed");
                _unifiedLogger.Error(LogCategory.Installation, "Diffusion Nexus Engine",
                    $"Engine install failed: {ex.Message}", ex);
                await _dialogService.ShowMessageAsync("Engine install failed", ex.Message);
            }
            finally
            {
                card.IsEngineInstalling = false;
                card.EngineStatusMessage = null;
                card.EngineProgressPercent = 0;

                // The visibility switch may have moved while we were installing — its own reload was
                // suppressed in OnIsEngineTileVisibleChanged to avoid detaching `card` mid-install.
                // Catch up now: the install has finished, so a reload can no longer orphan it.
                if (IsEngineTileVisible != visibilityAtStart)
                    _ = LoadInstallationsCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            _isEngineInstallInFlight = false;
        }
    }

    /// <summary>
    /// Model libraries the engine should read instead of duplicating. Uses the registered base
    /// model folders when available; an empty list simply means the engine keeps models locally.
    /// The starred default folder (if any) is ordered first, since the SDK installer feeds
    /// <c>SharedModelRoots.FirstOrDefault()</c> into the engine's model base folder.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveSharedModelRootsAsync()
    {
        try
        {
            var settings = await _unitOfWork.AppSettings.GetSettingsWithIncludesAsync();
            return settings?.BaseModelFolders
                .Where(f => f.IsEnabled && !string.IsNullOrWhiteSpace(f.FolderPath))
                .OrderByDescending(f => f.IsDefault)
                .ThenBy(f => f.Order)
                .Select(f => f.FolderPath)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not resolve shared model roots for the engine install.");
            return [];
        }
    }

    private void OnOpenFolderRequested(InstallerPackageCardViewModel card)
    {
        if (!Directory.Exists(card.InstallationPath))
        {
            Serilog.Log.Warning("Installation folder not found: {Path}", card.InstallationPath);
            return;
        }

        try
        {
            // TODO: Linux Implementation - use xdg-open on Linux
            Process.Start(new ProcessStartInfo
            {
                FileName = card.InstallationPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to open folder {Path}", card.InstallationPath);
        }
    }

    private async Task OnUpdateRequestedAsync(InstallerPackageCardViewModel card)
    {
        var service = _updateServices.FirstOrDefault(s => s.SupportedTypes.Contains(card.Type));
        if (service is null)
        {
            await _dialogService.ShowMessageAsync("Not Supported", "Update is not supported for this installer type.");
            return;
        }

        // Auto-stop the running instance before updating
        if (card.IsRunning)
        {
            _unifiedLogger.Info(LogCategory.Installation, card.Name,
                "Stopping running instance before update...");
            await _processManager.StopAsync(card.Id);

            // Brief wait to let the process exit cleanly
            await Task.Delay(1000);
        }

        // Open the unified console panel so the user can follow progress
        UnifiedConsolePanelRequested?.Invoke(this, EventArgs.Empty);

        card.IsUpdating = true;
        card.UpdateStatusMessage = "Starting update...";
        _unifiedLogger.Info(LogCategory.Installation, card.Name, "Starting update...");

        // Notify subscribers (e.g., Unified Console tabs) that update has started
        InstallerUpdateStateChanged?.Invoke(this, new InstallerUpdateStateEventArgs(
            card.Id, IsUpdating: true, IsUpdateAvailable: card.IsUpdateAvailable, UpdateSummary: null));

        var progress = new Progress<string>(msg =>
        {
            card.UpdateStatusMessage = msg;
            _unifiedLogger.Info(LogCategory.Installation, card.Name, msg);
        });

        try
        {
            var result = await service.UpdateAsync(card.InstallationPath, progress);
            if (result.Success)
            {
                card.IsUpdateAvailable = false;

                // Persist new version
                if (result.NewHash is not null)
                {
                    var entity = await _unitOfWork.InstallerPackages.GetByIdAsync(card.Id);
                    if (entity is not null)
                    {
                        entity.Version = result.NewHash;
                        entity.IsUpdateAvailable = false;
                        await _unitOfWork.SaveChangesAsync();

                        // Update the card's version display
                        var branch = string.IsNullOrWhiteSpace(entity.Branch) ? null : entity.Branch;
                        card.VersionDisplay = branch is not null ? $"{branch}@{result.NewHash}" : result.NewHash;
                    }
                }

                _unifiedLogger.Info(LogCategory.Installation, card.Name,
                    $"Update complete: {result.Message}");

                // Notify subscribers BEFORE the dialog so the Unified Console tabs
                // update immediately instead of waiting for the user to dismiss it.
                InstallerUpdateStateChanged?.Invoke(this, new InstallerUpdateStateEventArgs(
                    card.Id, IsUpdating: false, IsUpdateAvailable: false, UpdateSummary: "Up to date"));

                await _dialogService.ShowMessageAsync("Update Complete", result.Message);
            }
            else
            {
                _unifiedLogger.Error(LogCategory.Installation, card.Name,
                    $"Update failed: {result.Message}");

                // Notify subscribers BEFORE the dialog
                InstallerUpdateStateChanged?.Invoke(this, new InstallerUpdateStateEventArgs(
                    card.Id, IsUpdating: false, IsUpdateAvailable: card.IsUpdateAvailable, UpdateSummary: null));

                await _dialogService.ShowMessageAsync("Update Failed", result.Message);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to update installation {Name}", card.Name);
            _unifiedLogger.Error(LogCategory.Installation, card.Name,
                $"Update error: {ex.Message}", ex);

            // Notify subscribers BEFORE the dialog
            InstallerUpdateStateChanged?.Invoke(this, new InstallerUpdateStateEventArgs(
                card.Id, IsUpdating: false, IsUpdateAvailable: card.IsUpdateAvailable, UpdateSummary: null));

            await _dialogService.ShowMessageAsync("Error", $"Update failed: {ex.Message}");
        }
        finally
        {
            card.IsUpdating = false;
            card.UpdateStatusMessage = null;

            // Safety net: always ensure subscribers know updating has stopped,
            // even if an earlier notification was missed.
            InstallerUpdateStateChanged?.Invoke(this, new InstallerUpdateStateEventArgs(
                card.Id, IsUpdating: false, IsUpdateAvailable: card.IsUpdateAvailable, UpdateSummary: null));
        }
    }

    /// <summary>
    /// Triggers an update for a package by its database ID.
    /// Called by the Unified Console to ensure the same update logic is used regardless of entry point.
    /// </summary>
    public async Task UpdatePackageByIdAsync(int packageId)
    {
        var card = FindCard(packageId);
        if (card is null)
        {
            Serilog.Log.Warning("UpdatePackageByIdAsync: No card found for package {Id}", packageId);
            return;
        }

        await OnUpdateRequestedAsync(card);
    }

    /// <summary>
    /// Checks all installer cards for available updates in parallel.
    /// Each check has a per-instance timeout to avoid blocking the others.
    /// </summary>
    private async Task CheckAllCardsForUpdatesAsync()
    {
        var tasks = InstallerCards.Select(card => CheckCardForUpdatesAsync(card));
        await Task.WhenAll(tasks);
    }

    private async Task CheckCardForUpdatesAsync(InstallerPackageCardViewModel card)
    {
        var service = _updateServices.FirstOrDefault(s => s.SupportedTypes.Contains(card.Type));
        if (service is null) return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await service.CheckForUpdatesAsync(card.InstallationPath, ct: cts.Token);

            Dispatcher.UIThread.Post(() =>
            {
                card.IsUpdateAvailable = result.IsUpdateAvailable;
            });

            if (result.IsUpdateAvailable)
            {
                Serilog.Log.Information("Update available for {Name}: {Summary}",
                    card.Name, result.Summary);
            }
        }
        catch (OperationCanceledException)
        {
            Serilog.Log.Warning("Update check timed out for {Name} at {Path}",
                card.Name, card.InstallationPath);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Update check failed for {Name} at {Path}",
                card.Name, card.InstallationPath);
        }
    }

    /// <summary>
    /// Recursively deletes a directory, clearing read-only attributes on files first
    /// so that Git pack/index files (and similar) don't cause <see cref="UnauthorizedAccessException"/>.
    /// </summary>
    private static void ForceDeleteDirectory(string path)
    {
        var dirInfo = new DirectoryInfo(path);

        foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            file.Attributes = FileAttributes.Normal;
        }

        dirInfo.Delete(recursive: true);
    }
}

/// <summary>
/// Event data for <see cref="InstallerManagerViewModel.InstallerUpdateStateChanged"/>.
/// </summary>
public sealed record InstallerUpdateStateEventArgs(
    int PackageId,
    bool IsUpdating,
    bool IsUpdateAvailable,
    string? UpdateSummary);
