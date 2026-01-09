using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Presentation sub-tab within dataset version detail view.
/// Displays media gallery (images/videos) and document list for showcasing trained models.
/// </summary>
public partial class PresentationTabViewModel : ObservableObject, IDialogServiceAware
{
    private string _presentationFolderPath = string.Empty;
    private bool _isLoading;
    private string? _statusMessage;
    private readonly IDatasetEventAggregator _eventAggregator;

    /// <summary>
    /// Gets or sets the dialog service for file operations.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Collection of media files (images/videos) for gallery display.
    /// Reuses DatasetImageViewModel for consistent display with main dataset view.
    /// </summary>
    public ObservableCollection<DatasetImageViewModel> MediaFiles { get; } = [];

    /// <summary>
    /// Collection of document/design files for list display.
    /// </summary>
    public ObservableCollection<PresentationDocumentViewModel> DocumentFiles { get; } = [];

    /// <summary>
    /// Path to the Presentation folder for the current version.
    /// </summary>
    public string PresentationFolderPath
    {
        get => _presentationFolderPath;
        set => SetProperty(ref _presentationFolderPath, value);
    }

    /// <summary>
    /// Whether files are currently loading.
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
    /// Whether there are no presentation files at all.
    /// </summary>
    public bool HasNoFiles => MediaFiles.Count == 0 && DocumentFiles.Count == 0;

    /// <summary>
    /// Whether there are any presentation files.
    /// </summary>
    public bool HasFiles => !HasNoFiles;

    /// <summary>
    /// Whether there are media files for the gallery.
    /// </summary>
    public bool HasMediaFiles => MediaFiles.Count > 0;

    /// <summary>
    /// Whether there are document files.
    /// </summary>
    public bool HasDocumentFiles => DocumentFiles.Count > 0;

    /// <summary>
    /// Number of media files.
    /// </summary>
    public int MediaCount => MediaFiles.Count;

    /// <summary>
    /// Number of document files.
    /// </summary>
    public int DocumentCount => DocumentFiles.Count;

    /// <summary>
    /// Supported media extensions for display.
    /// </summary>
    public static string SupportedMediaExtensionsText => "Images: .png, .jpg, .webp, .gif | Videos: .mp4, .webm, .mov";

    /// <summary>
    /// Supported document extensions for display.
    /// </summary>
    public static string SupportedDocumentExtensionsText => ".txt, .md, .json, .xml, .pdf, .docx, .psd, .xcf, and more";

    // Commands
    public IAsyncRelayCommand AddMediaFilesCommand { get; }
    public IAsyncRelayCommand AddDocumentFilesCommand { get; }
    public IAsyncRelayCommand<DatasetImageViewModel?> DeleteMediaFileCommand { get; }
    public IAsyncRelayCommand<PresentationDocumentViewModel?> DeleteDocumentFileCommand { get; }
    public IRelayCommand<PresentationDocumentViewModel?> OpenDocumentCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    
    // Placeholder commands for future features
    public IRelayCommand UploadToCivitAICommand { get; }
    public IRelayCommand UploadToHuggingFaceCommand { get; }

    public PresentationTabViewModel(IDatasetEventAggregator eventAggregator)
    {
        _eventAggregator = eventAggregator ?? throw new ArgumentNullException(nameof(eventAggregator));

        AddMediaFilesCommand = new AsyncRelayCommand(AddMediaFilesAsync);
        AddDocumentFilesCommand = new AsyncRelayCommand(AddDocumentFilesAsync);
        DeleteMediaFileCommand = new AsyncRelayCommand<DatasetImageViewModel?>(DeleteMediaFileAsync);
        DeleteDocumentFileCommand = new AsyncRelayCommand<PresentationDocumentViewModel?>(DeleteDocumentFileAsync);
        OpenDocumentCommand = new RelayCommand<PresentationDocumentViewModel?>(OpenDocument);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        RefreshCommand = new RelayCommand(LoadFiles);
        
        // Placeholder commands - show coming soon message
        UploadToCivitAICommand = new RelayCommand(ShowCivitAIComingSoon);
        UploadToHuggingFaceCommand = new RelayCommand(ShowHuggingFaceComingSoon);
    }

    /// <summary>
    /// Initializes the tab for a specific version folder.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    public void Initialize(string versionFolderPath)
    {
        PresentationFolderPath = Path.Combine(versionFolderPath, "Presentation");
        LoadFiles();
    }

    /// <summary>
    /// Loads all presentation files from the Presentation folder.
    /// </summary>
    public void LoadFiles()
    {
        MediaFiles.Clear();
        DocumentFiles.Clear();

        if (!Directory.Exists(PresentationFolderPath))
        {
            NotifyCollectionChanged();
            return;
        }

        try
        {
            var files = Directory.EnumerateFiles(PresentationFolderPath)
                .Where(f => !Path.GetFileName(f).StartsWith("."))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            foreach (var filePath in files)
            {
                var category = PresentationFileItem.GetCategory(filePath);

                switch (category)
                {
                    case PresentationFileCategory.Image:
                    case PresentationFileCategory.Video:
                        // Reuse DatasetImageViewModel for consistent gallery display
                        var mediaVm = DatasetImageViewModel.FromFile(filePath, _eventAggregator);
                        MediaFiles.Add(mediaVm);
                        break;

                    case PresentationFileCategory.Document:
                    case PresentationFileCategory.RawDesign:
                        var docItem = PresentationFileItem.FromFile(filePath);
                        DocumentFiles.Add(new PresentationDocumentViewModel(docItem));
                        break;

                    default:
                        // Other files - treat as documents
                        var otherItem = PresentationFileItem.FromFile(filePath);
                        DocumentFiles.Add(new PresentationDocumentViewModel(otherItem));
                        break;
                }
            }

            var mediaCount = MediaFiles.Count;
            var docCount = DocumentFiles.Count;

            if (mediaCount > 0 || docCount > 0)
            {
                var parts = new List<string>();
                if (mediaCount > 0) parts.Add($"{mediaCount} media");
                if (docCount > 0) parts.Add($"{docCount} documents");
                StatusMessage = $"Loaded {string.Join(", ", parts)}";
            }
            else
            {
                StatusMessage = null;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading files: {ex.Message}";
        }

        NotifyCollectionChanged();
    }

    /// <summary>
    /// Adds media files via file picker dialog.
    /// </summary>
    private async Task AddMediaFilesAsync()
    {
        if (DialogService is null) return;

        var allMediaExtensions = PresentationFileItem.ImageExtensions
            .Concat(PresentationFileItem.VideoExtensions)
            .ToArray();

        var files = await DialogService.ShowFileDropDialogAsync(
            "Add Presentation Media",
            allMediaExtensions);

        if (files is null || files.Count == 0) return;

        await AddFilesAsync(files, isMedia: true);
    }

    /// <summary>
    /// Adds document files via file picker dialog.
    /// </summary>
    private async Task AddDocumentFilesAsync()
    {
        if (DialogService is null) return;

        var allDocExtensions = PresentationFileItem.DocumentExtensions
            .Concat(PresentationFileItem.RawDesignExtensions)
            .ToArray();

        var files = await DialogService.ShowFileDropDialogAsync(
            "Add Presentation Documents",
            allDocExtensions);

        if (files is null || files.Count == 0) return;

        await AddFilesAsync(files, isMedia: false);
    }

    /// <summary>
    /// Adds files to the Presentation folder (called from drag-drop or file picker).
    /// </summary>
    /// <param name="filePaths">Paths to the files to add.</param>
    /// <param name="isMedia">True for media files, false for documents (affects filtering).</param>
    public async Task AddFilesAsync(IEnumerable<string> filePaths, bool isMedia = true)
    {
        IsLoading = true;
        try
        {
            // Ensure folder exists
            Directory.CreateDirectory(PresentationFolderPath);

            var copied = 0;
            var skipped = 0;

            foreach (var sourcePath in filePaths)
            {
                // Check if file is supported
                if (!PresentationFileItem.IsSupportedFile(sourcePath))
                {
                    skipped++;
                    continue;
                }

                var fileName = Path.GetFileName(sourcePath);
                var destPath = Path.Combine(PresentationFolderPath, fileName);

                if (File.Exists(destPath))
                {
                    skipped++;
                    continue;
                }

                await Task.Run(() => File.Copy(sourcePath, destPath));
                copied++;
            }

            StatusMessage = skipped > 0
                ? $"Added {copied} file(s), skipped {skipped}"
                : $"Added {copied} file(s)";

            LoadFiles();
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
    /// Deletes a media file.
    /// </summary>
    private async Task DeleteMediaFileAsync(DatasetImageViewModel? mediaVm)
    {
        if (mediaVm is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Media File",
            $"Delete '{mediaVm.FullFileName}'?\n\nThis action cannot be undone.");

        if (!confirm) return;

        try
        {
            if (File.Exists(mediaVm.ImagePath))
            {
                File.Delete(mediaVm.ImagePath);
            }

            // Also delete associated caption/rating files if they exist
            if (File.Exists(mediaVm.CaptionFilePath))
            {
                File.Delete(mediaVm.CaptionFilePath);
            }
            if (File.Exists(mediaVm.RatingFilePath))
            {
                File.Delete(mediaVm.RatingFilePath);
            }

            MediaFiles.Remove(mediaVm);
            StatusMessage = $"Deleted '{mediaVm.FullFileName}'";
            NotifyCollectionChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting file: {ex.Message}";
        }
    }

    /// <summary>
    /// Deletes a document file.
    /// </summary>
    private async Task DeleteDocumentFileAsync(PresentationDocumentViewModel? docVm)
    {
        if (docVm is null || DialogService is null) return;

        var confirm = await DialogService.ShowConfirmAsync(
            "Delete Document",
            $"Delete '{docVm.FileName}'?\n\nThis action cannot be undone.");

        if (!confirm) return;

        try
        {
            if (File.Exists(docVm.FilePath))
            {
                File.Delete(docVm.FilePath);
            }

            DocumentFiles.Remove(docVm);
            StatusMessage = $"Deleted '{docVm.FileName}'";
            NotifyCollectionChanged();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error deleting file: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens a document with the system default application.
    /// </summary>
    private void OpenDocument(PresentationDocumentViewModel? docVm)
    {
        if (docVm is null || !File.Exists(docVm.FilePath)) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = docVm.FilePath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening file: {ex.Message}";
        }
    }

    /// <summary>
    /// Opens the Presentation folder in the system file explorer.
    /// </summary>
    private void OpenFolder()
    {
        if (!Directory.Exists(PresentationFolderPath))
        {
            Directory.CreateDirectory(PresentationFolderPath);
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = PresentationFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error opening folder: {ex.Message}";
        }
    }

    private void ShowCivitAIComingSoon()
    {
        StatusMessage = "Upload to CivitAI - Coming Soon!";
    }

    private void ShowHuggingFaceComingSoon()
    {
        StatusMessage = "Upload to Hugging Face - Coming Soon!";
    }

    private void NotifyCollectionChanged()
    {
        OnPropertyChanged(nameof(HasNoFiles));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(HasMediaFiles));
        OnPropertyChanged(nameof(HasDocumentFiles));
        OnPropertyChanged(nameof(MediaCount));
        OnPropertyChanged(nameof(DocumentCount));
    }
}

/// <summary>
/// ViewModel wrapper for document/design files in the Presentation tab.
/// </summary>
public partial class PresentationDocumentViewModel : ObservableObject
{
    public PresentationFileItem Item { get; }

    public string FileName => Item.FileName;
    public string DisplayName => Item.DisplayName;
    public string FilePath => Item.FilePath;
    public string FileSizeDisplay => Item.FileSizeDisplay;
    public string Extension => Item.Extension;
    public DateTime ModifiedAt => Item.ModifiedAt;
    public PresentationFileCategory Category => Item.Category;

    /// <summary>
    /// Icon/emoji for the file category.
    /// </summary>
    public string CategoryIcon => PresentationFileItem.GetCategoryIcon(Item.Category);

    /// <summary>
    /// Display text for the category.
    /// </summary>
    public string CategoryText => Item.Category switch
    {
        PresentationFileCategory.Document => "Document",
        PresentationFileCategory.RawDesign => "Design File",
        _ => "File"
    };

    public PresentationDocumentViewModel(PresentationFileItem item)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
    }
}
