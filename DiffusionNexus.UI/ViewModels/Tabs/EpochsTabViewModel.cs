using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Epochs sub-tab within dataset version detail view.
/// Manages epoch/checkpoint files (.safetensors, .pt, .pth, .gguf).
/// </summary>
public partial class EpochsTabViewModel : ObservableObject, IDialogServiceAware
{
    private string _epochsFolderPath = string.Empty;
    private bool _isLoading;
    private string? _statusMessage;
    private readonly IDatasetEventAggregator _eventAggregator;

    /// <summary>
    /// Gets or sets the dialog service for file operations.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Collection of epoch files in the current version.
    /// </summary>
    public ObservableCollection<EpochFileViewModel> EpochFiles { get; } = [];

    /// <summary>
    /// Path to the Epochs folder for the current version.
    /// </summary>
    public string EpochsFolderPath
    {
        get => _epochsFolderPath;
        set => SetProperty(ref _epochsFolderPath, value);
    }

    /// <summary>
    /// Whether epoch files are currently loading.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Status message for user feedback.
    /// </summary>
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>
    /// Whether there are no epoch files.
    /// </summary>
    public bool HasNoEpochs => EpochFiles.Count == 0;

    /// <summary>
    /// Whether there are epoch files.
    /// </summary>
    public bool HasEpochs => EpochFiles.Count > 0;

    /// <summary>
    /// Number of selected epoch files.
    /// </summary>
    public int SelectionCount => EpochFiles.Count(e => e.IsSelected);

    /// <summary>
    /// Whether any epochs are selected.
    /// </summary>
    public bool HasSelection => SelectionCount > 0;

    /// <summary>
    /// Supported file extensions for display.
    /// </summary>
    public static string SupportedExtensionsText => ".safetensors, .pt, .pth, .gguf";

    // Commands
    public IAsyncRelayCommand AddEpochFilesCommand { get; }
    public IAsyncRelayCommand<EpochFileViewModel?> DeleteEpochFileCommand { get; }
    public IAsyncRelayCommand DeleteSelectedCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand SelectAllCommand { get; }
    public IRelayCommand ClearSelectionCommand { get; }

    public EpochsTabViewModel(IDatasetEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        AddEpochFilesCommand = new AsyncRelayCommand(AddEpochFilesAsync);
        DeleteEpochFileCommand = new AsyncRelayCommand<EpochFileViewModel?>(DeleteEpochFileAsync);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => HasSelection);
        RefreshCommand = new RelayCommand(LoadEpochFiles);
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
    }

    /// <summary>
    /// Initializes the tab for a specific version folder.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    public void Initialize(string versionFolderPath)
    {
        EpochsFolderPath = Path.Combine(versionFolderPath, "Epochs");
        LoadEpochFiles();
    }

    /// <summary>
    /// Loads epoch files from the Epochs folder.
    /// </summary>
    public void LoadEpochFiles()
    {
        EpochFiles.Clear();

        if (!Directory.Exists(EpochsFolderPath))
        {
            NotifyCollectionChanged();
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(EpochsFolderPath)
                .Where(f => EpochFileItem.IsEpochFile(f))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            foreach (var filePath in files)
            {
                var item = EpochFileItem.FromFile(filePath);
                EpochFiles.Add(new EpochFileViewModel(item, this));
            }

            StatusMessage = EpochFiles.Count > 0
                ? $"Loaded {EpochFiles.Count} epoch file(s)"
                : null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading epochs: {ex.Message}";
        }

        NotifyCollectionChanged();
    }

    /// <summary>
    /// Adds epoch files via file picker dialog.
    /// </summary>
    private async Task AddEpochFilesAsync()
    {
        if (DialogService is null) return;

        var files = await DialogService.ShowFileDropDialogAsync(
            "Add Epoch Files",
            EpochFileItem.SupportedExtensions);

        if (files is null || files.Count == 0) return;

        await AddFilesAsync(files);
    }

    /// <summary>
    /// Adds files to the Epochs folder (called from drag-drop or file picker).
    /// </summary>
    /// <param name="filePaths">Paths to the files to add.</param>
    public async Task AddFilesAsync(IEnumerable<string> filePaths)
    {
        IsLoading = true;
        try
        {
            // Ensure folder exists
            Directory.CreateDirectory(EpochsFolderPath);

            var copied = 0;
            var skipped = 0;

            foreach (var sourcePath in filePaths)
            {
                if (!EpochFileItem.IsEpochFile(sourcePath))
                {
                    skipped++;
                    continue;
                }

                var fileName = Path.GetFileName(sourcePath);
                var destPath = Path.Combine(EpochsFolderPath, fileName);

                if (File.Exists(destPath))
                {
                    skipped++;
                    continue;
                }

                await Task.Run(() => File.Copy(sourcePath, destPath));
                copied++;
            }

            StatusMessage = skipped > 0
                ? $"Added {copied} epoch file(s), skipped {skipped}"
                : $"Added {copied} epoch file(s)";

            LoadEpochFiles();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error adding files: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Deletes a single epoch file.
    /// </summary>
    private async Task DeleteEpochFileAsync(EpochFileViewModel? epochVm)
    {
        if (epochVm is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Epoch File",
            $"Delete '{epochVm.FileName}'?\n\nThis action cannot be undone.");

        if (!confirm) return;

        try
        {
            if (File.Exists(epochVm.FilePath))
            {
                File.Delete(epochVm.FilePath);
            }

            EpochFiles.Remove(epochVm);
            StatusMessage = $"Deleted '{epochVm.FileName}'";
            NotifyCollectionChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting file: {ex.Message}";
        }
    }

    /// <summary>
    /// Deletes all selected epoch files.
    /// </summary>
    private async Task DeleteSelectedAsync()
    {
        if (DialogService is null) return;

        var selected = EpochFiles.Where(e => e.IsSelected).ToList();
        if (selected.Count == 0) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Selected Epoch Files",
            $"Delete {selected.Count} selected epoch file(s)?\n\nThis action cannot be undone.");

        if (!confirm) return;

        var deleted = 0;
        foreach (var epochVm in selected)
        {
            try
            {
                if (File.Exists(epochVm.FilePath))
                {
                    File.Delete(epochVm.FilePath);
                }
                EpochFiles.Remove(epochVm);
                deleted++;
            }
            catch
            {
                // Continue with other files
            }
        }

        StatusMessage = $"Deleted {deleted} epoch file(s)";
        NotifyCollectionChanged();
    }

    private void SelectAll()
    {
        foreach (var epoch in EpochFiles)
        {
            epoch.IsSelected = true;
        }
        NotifySelectionChanged();
    }

    private void ClearSelection()
    {
        foreach (var epoch in EpochFiles)
        {
            epoch.IsSelected = false;
        }
        NotifySelectionChanged();
    }

    internal void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(HasSelection));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCollectionChanged()
    {
        OnPropertyChanged(nameof(HasNoEpochs));
        OnPropertyChanged(nameof(HasEpochs));
        NotifySelectionChanged();
    }
}

/// <summary>
/// ViewModel wrapper for individual epoch files with selection support.
/// </summary>
public partial class EpochFileViewModel : ObservableObject
{
    private readonly EpochsTabViewModel _parent;
    private bool _isSelected;

    public EpochFileItem Item { get; }

    public string FileName => Item.FileName;
    public string DisplayName => Item.DisplayName;
    public string FilePath => Item.FilePath;
    public string FileSizeDisplay => Item.FileSizeDisplay;
    public string Extension => Item.Extension;
    public DateTime ModifiedAt => Item.ModifiedAt;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _parent.NotifySelectionChanged();
            }
        }
    }

    public EpochFileViewModel(EpochFileItem item, EpochsTabViewModel parent)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _parent = parent ?? throw new ArgumentNullException(nameof(parent));
    }
}
