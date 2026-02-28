using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;
using DiffusionNexus.Domain.Services;


namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Installer Manager module.
/// Loads saved installations from the database and displays them as cards.
/// </summary>
public partial class InstallerManagerViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly IInstallerPackageRepository _installerPackageRepository;
    private readonly IAppSettingsRepository _appSettingsRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly PackageProcessManager _processManager;
    private readonly IDatasetEventAggregator _eventAggregator;

    [ObservableProperty]
    private string _welcomeMessage = "Welcome to the Installer Manager!";

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// The collection of installer cards displayed in the view.
    /// </summary>
    public ObservableCollection<InstallerPackageCardViewModel> InstallerCards { get; } = [];

    /// <summary>
    /// Bottom tray with one tab per running process console.
    /// </summary>
    public ProcessConsoleTrayViewModel ConsoleTray { get; } = new();

    /// <summary>
    /// True when there are no installations to show.
    /// </summary>
    public bool IsEmpty => InstallerCards.Count == 0 && !IsLoading;

    public InstallerManagerViewModel(
        IDialogService dialogService,
        IInstallerPackageRepository installerPackageRepository,
        IAppSettingsRepository appSettingsRepository,
        IUnitOfWork unitOfWork,
        PackageProcessManager processManager,
        IDatasetEventAggregator eventAggregator)
    {
        _dialogService = dialogService;
        _installerPackageRepository = installerPackageRepository;
        _appSettingsRepository = appSettingsRepository;
        _unitOfWork = unitOfWork;
        _processManager = processManager;
        _eventAggregator = eventAggregator;

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

            var packages = await _installerPackageRepository.GetAllAsync();

            foreach (var package in packages)
            {
                InstallerCards.Add(CreateCard(package));
            }
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
            await _installerPackageRepository.AddAsync(package);
            await _unitOfWork.SaveChangesAsync();

            // Link or create an ImageGallery for the output folder
            if (!string.IsNullOrWhiteSpace(result.OutputFolderPath))
            {
                await LinkOutputFolderAsync(package, result.OutputFolderPath);
                await _unitOfWork.SaveChangesAsync();

                // Notify other components (Settings dialog, Generation Gallery) about the new gallery
                _eventAggregator.PublishSettingsSaved(new SettingsSavedEventArgs());
            }

            InstallerCards.Add(CreateCard(package));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to save installation {Name}", package.Name);
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
        var settings = await _appSettingsRepository.GetSettingsAsync();
        var appSettingsId = settings?.Id ?? 1;

        // Check if a gallery with this path already exists
        var allSettings = await _appSettingsRepository.GetSettingsWithIncludesAsync();
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
            await _appSettingsRepository.AddImageGalleryAsync(gallery);
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

        // Restore running state if the process is still alive
        if (_processManager.IsRunning(card.Id))
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

            if (running)
            {
                // Auto-open a console tab when a process starts
                ConsoleTray.OpenTab(card);
            }
            else
            {
                // Remove the tab when the process exits
                ConsoleTray.CloseTab(card);
            }
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
        _processManager.Launch(card.Id, fullPath, card.InstallationPath, card.Arguments);
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

    private async Task OnRemoveRequestedAsync(InstallerPackageCardViewModel card)
    {
        if (_processManager.IsRunning(card.Id))
        {
            await _dialogService.ShowMessageAsync("Cannot Remove", "Please stop the running process before removing.");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "Remove Installation",
            $"Remove \"{card.Name}\" from the Installer Manager?\n\nThis will NOT delete any files on disk.");

        if (!confirmed) return;

        try
        {
            var entity = await _installerPackageRepository.GetByIdAsync(card.Id);
            if (entity is not null)
            {
                _installerPackageRepository.Remove(entity);
                await _unitOfWork.SaveChangesAsync();
            }

            InstallerCards.Remove(card);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to remove installation {Name}", card.Name);
            await _dialogService.ShowMessageAsync("Error", $"Failed to remove installation: {ex.Message}");
        }
    }

    private Task OnConsoleRequestedAsync(InstallerPackageCardViewModel card)
    {
        ConsoleTray.OpenTab(card);
        ConsoleTray.IsPinned = true;
        return Task.CompletedTask;
    }

    private async Task OnSettingsRequestedAsync(InstallerPackageCardViewModel card)
    {
        // Load the package with its linked gallery to get the output folder path
        var entity = await _installerPackageRepository.GetByIdWithGalleryAsync(card.Id);
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
            "This will delete the folder on your hard drive including models and generated images.\n\n" +
            "⚠ This action CANNOT be undone.");

        if (!confirmed) return;

        try
        {
            // Remove from database first
            var entity = await _installerPackageRepository.GetByIdAsync(card.Id);
            if (entity is not null)
            {
                _installerPackageRepository.Remove(entity);
                await _unitOfWork.SaveChangesAsync();
            }

            InstallerCards.Remove(card);

            // Delete from disk – clear read-only attributes first (e.g. .git pack files)
            // TODO: Linux Implementation - verify ForceDeleteDirectory works cross-platform
            if (Directory.Exists(card.InstallationPath))
            {
                await Task.Run(() => ForceDeleteDirectory(card.InstallationPath));
                Serilog.Log.Information("Deleted installation folder: {Path}", card.InstallationPath);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to delete installation {Name} from disk", card.Name);
            await _dialogService.ShowMessageAsync("Error", $"Failed to delete: {ex.Message}\n\nSome files may have been partially removed.");
        }
    }

    private async Task OnMakeDefaultRequestedAsync(InstallerPackageCardViewModel card)
    {
        try
        {
            // Clear existing default for this installer type
            await _installerPackageRepository.ClearDefaultByTypeAsync(card.Type);

            // Set the selected package as default
            var entity = await _installerPackageRepository.GetByIdAsync(card.Id);
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
