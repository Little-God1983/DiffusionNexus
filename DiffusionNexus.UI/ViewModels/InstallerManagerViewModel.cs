using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;


namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Installer Manager module.
/// Loads saved installations from the database and displays them as cards.
/// </summary>
public partial class InstallerManagerViewModel : ViewModelBase
{
    private readonly IDialogService _dialogService;
    private readonly IInstallerPackageRepository _installerPackageRepository;
    private readonly IUnitOfWork _unitOfWork;

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

    public InstallerManagerViewModel(
        IDialogService dialogService,
        IInstallerPackageRepository installerPackageRepository,
        IUnitOfWork unitOfWork)
    {
        _dialogService = dialogService;
        _installerPackageRepository = installerPackageRepository;
        _unitOfWork = unitOfWork;

        InstallerCards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
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
        // 1. Pick Folder
        var path = await _dialogService.ShowOpenFolderDialogAsync("Select Installation Folder");
        if (string.IsNullOrEmpty(path)) return;

        // 2. Show Dialog for details
        var result = await _dialogService.ShowAddExistingInstallationDialogAsync(path);

        if (result.IsCancelled) return;

        // 3. Save to Database
        var package = new InstallerPackage
        {
            Name = result.Name,
            InstallationPath = result.InstallationPath,
            Type = result.Type,
            ExecutablePath = result.ExecutablePath,
            Arguments = string.Empty,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            await _installerPackageRepository.AddAsync(package);
            await _unitOfWork.SaveChangesAsync();

            // Add card to the UI
            InstallerCards.Add(CreateCard(package));

            await _dialogService.ShowMessageAsync("Success", $"Successfully added {package.Name}");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to save installation {Name}", package.Name);
            await _dialogService.ShowMessageAsync("Error", $"Failed to save installation: {ex.Message}");
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
        card.RemoveRequested += OnRemoveRequestedAsync;
        card.SettingsRequested += OnSettingsRequestedAsync;
        return card;
    }

    private async Task OnLaunchRequestedAsync(InstallerPackageCardViewModel card)
    {
        if (string.IsNullOrWhiteSpace(card.ExecutablePath))
        {
            await _dialogService.ShowMessageAsync("No Executable", "No executable path configured for this installation.");
            return;
        }

        try
        {
            // TODO: Linux Implementation - use platform-appropriate process start
            var fullPath = Path.Combine(card.InstallationPath, card.ExecutablePath);
            if (!File.Exists(fullPath))
            {
                await _dialogService.ShowMessageAsync("Not Found", $"Executable not found: {fullPath}");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = fullPath,
                WorkingDirectory = card.InstallationPath,
                Arguments = card.Arguments,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to launch {Name}", card.Name);
            await _dialogService.ShowMessageAsync("Launch Failed", $"Failed to launch: {ex.Message}");
        }
    }

    private async Task OnRemoveRequestedAsync(InstallerPackageCardViewModel card)
    {
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

    private Task OnSettingsRequestedAsync(InstallerPackageCardViewModel card)
    {
        // TODO: Implement settings dialog for editing installation details
        return _dialogService.ShowMessageAsync("Coming Soon", "Installation settings editor is under development.");
    }
}
