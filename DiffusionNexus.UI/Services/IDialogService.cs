namespace DiffusionNexus.UI.Services;

using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views.Dialogs;
using System.Collections.ObjectModel;
using DiffusionNexus.Domain.Services;

/// <summary>
/// Provides dialog operations for file/folder pickers and message boxes.
/// Inject this interface to enable testable UI dialogs.
/// </summary>
/// <example>
/// <code>
/// var path = await DialogService.ShowOpenFolderDialogAsync("Select Output Folder");
/// if (path != null)
/// {
///     OutputPath = path;
/// }
/// </code>
/// </example>
public interface IDialogService
{
    /// <summary>
    /// Shows an open file dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">Optional file filter (e.g., "*.json").</param>
    /// <returns>Selected file path, or null if cancelled.</returns>
    Task<string?> ShowOpenFileDialogAsync(string title, string? filter = null);

    /// <summary>
    /// Shows an open file dialog with an initial starting folder.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="startFolder">Initial folder to open the dialog in.</param>
    /// <param name="filter">Optional file filter (e.g., "*.zip").</param>
    /// <returns>Selected file path, or null if cancelled.</returns>
    Task<string?> ShowOpenFileDialogAsync(string title, string startFolder, string? filter);

    /// <summary>
    /// Shows a save file dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="defaultFileName">Suggested file name.</param>
    /// <param name="filter">Optional file filter.</param>
    /// <returns>Selected save path, or null if cancelled.</returns>
    Task<string?> ShowSaveFileDialogAsync(string title, string? defaultFileName = null, string? filter = null);

    /// <summary>
    /// Shows a folder picker dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <returns>Selected folder path, or null if cancelled.</returns>
    Task<string?> ShowOpenFolderDialogAsync(string title);

    /// <summary>
    /// Shows an informational message dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message content.</param>
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// Shows a confirmation dialog with Yes/No options.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Question to ask.</param>
    /// <returns>True if confirmed, false otherwise.</returns>
    Task<bool> ShowConfirmAsync(string title, string message);

    /// <summary>
    /// Shows an input dialog for text entry.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Prompt message.</param>
    /// <param name="defaultValue">Optional default value.</param>
    /// <returns>The entered text, or null if cancelled.</returns>
    Task<string?> ShowInputAsync(string title, string message, string? defaultValue = null);

    /// <summary>
    /// Shows a drag-and-drop file picker dialog for images and text files.
    /// </summary>
    /// <param name="title">Dialog title (e.g., "Add Images to: MyDataset").</param>
    /// <returns>List of selected file paths, or null if cancelled.</returns>
    Task<List<string>?> ShowFileDropDialogAsync(string title);

    /// <summary>
    /// Shows a drag-and-drop file picker dialog with custom extensions.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="allowedExtensions">Allowed file extensions (e.g., ".png", ".jpg").</param>
    /// <returns>List of selected file paths, or null if cancelled.</returns>
    Task<List<string>?> ShowFileDropDialogAsync(string title, params string[] allowedExtensions);

    /// <summary>
    /// Shows a drag-and-drop file picker dialog pre-populated with initial files.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="initialFiles">Files to pre-populate the dialog with.</param>
    /// <returns>List of selected file paths, or null if cancelled.</returns>
    Task<List<string>?> ShowFileDropDialogAsync(string title, IEnumerable<string> initialFiles);

    /// <summary>
    /// Shows an option selection dialog with multiple choices.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message/description.</param>
    /// <param name="options">Array of option labels to display as buttons.</param>
    /// <returns>The index of the selected option (0-based), or -1 if cancelled.</returns>
    Task<int> ShowOptionsAsync(string title, string message, params string[] options);

    /// <summary>
    /// Shows the export dataset dialog with options and preview counts.
    /// </summary>
    /// <param name="datasetName">Name of the dataset being exported.</param>
    /// <param name="mediaFiles">All media files in the dataset.</param>
    /// <returns>Export result with selected options and files, or cancelled result.</returns>
    Task<ExportDatasetResult> ShowExportDialogAsync(string datasetName, IEnumerable<DatasetImageViewModel> mediaFiles);

    /// <summary>
    /// Shows the create dataset dialog with name, category, and type options.
    /// </summary>
    /// <param name="availableCategories">Categories to show in the dropdown.</param>
    /// <returns>Create result with name, category, and type, or cancelled result.</returns>
    Task<CreateDatasetResult> ShowCreateDatasetDialogAsync(IEnumerable<DatasetCategoryViewModel> availableCategories);

    /// <summary>
    /// Shows the full-screen image viewer dialog for browsing dataset images.
    /// Integrates with the event aggregator for cross-component state synchronization.
    /// </summary>
    /// <param name="images">Collection of all images in the dataset.</param>
    /// <param name="startIndex">Index of the image to display first.</param>
    /// <param name="eventAggregator">Event aggregator for publishing rating changes.</param>
    /// <param name="onSendToImageEditor">Callback when user wants to send to editor.</param>
    /// <param name="onDeleteRequested">Callback when user wants to delete an image.</param>
    Task ShowImageViewerDialogAsync(
        ObservableCollection<DatasetImageViewModel> images,
        int startIndex,
        IDatasetEventAggregator? eventAggregator = null,
        Action<DatasetImageViewModel>? onSendToImageEditor = null,
        Action<DatasetImageViewModel>? onDeleteRequested = null,
        bool showRatingControls = true);

    /// <summary>
    /// Shows the Save As dialog for saving an image with a new name and optional rating.
    /// </summary>
    /// <param name="originalFilePath">Full path to the original file.</param>
    /// <param name="availableDatasets">List of available datasets to offer as save destinations.</param>
    /// <returns>Save result with filename, destination, and rating, or cancelled result.</returns>
    Task<SaveAsResult> ShowSaveAsDialogAsync(string originalFilePath, IEnumerable<DatasetCardViewModel> availableDatasets);


    /// <summary>
    /// Shows the Replace Image dialog for comparing and replacing an image.
    /// </summary>
    /// <param name="originalImage">The original image view model.</param>
    /// <returns>The result indicating action (Replace, AddAsNew) and new file path.</returns>
    Task<ReplaceImageResult> ShowReplaceImageDialogAsync(DatasetImageViewModel originalImage);

    /// <summary>
    /// Shows the backup comparison dialog for comparing current data with a backup.
    /// </summary>
    /// <param name="currentStats">Statistics about the current dataset storage.</param>
    /// <param name="backupStats">Analysis results from the selected backup.</param>
    /// <returns>True if the user chooses to restore, false if cancelled.</returns>
    Task<bool> ShowBackupCompareDialogAsync(BackupCompareData currentStats, BackupCompareData backupStats);

    /// <summary>
    /// Shows the create version dialog with content type and rating selection options.
    /// </summary>
    /// <param name="currentVersion">The current version number (used as default source version).</param>
    /// <param name="availableVersions">All available versions to copy from.</param>
    /// <param name="mediaFiles">All media files in the current version.</param>
    /// <returns>Create version result with selected options, or cancelled result.</returns>
    Task<CreateVersionResult> ShowCreateVersionDialogAsync(
        int currentVersion,
        IReadOnlyList<int> availableVersions,
        IEnumerable<DatasetImageViewModel> mediaFiles);

    /// <summary>
    /// Shows the auto-captioning dialog.
    /// </summary>
    /// <param name="captioningService">The captioning service instance.</param>
    /// <param name="availableDatasets">List of available datasets to caption.</param>
    /// <param name="eventAggregator">Event aggregator for updates.</param>
    /// <param name="initialDataset">The dataset to select initially.</param>
    /// <param name="initialVersion">The version to select initially.</param>
    Task ShowCaptioningDialogAsync(
        ICaptioningService captioningService,
        IEnumerable<DatasetCardViewModel> availableDatasets,
        IDatasetEventAggregator? eventAggregator = null,
        DatasetCardViewModel? initialDataset = null,
        int? initialVersion = null);

    /// <summary>
    /// Shows the file conflict resolution dialog for handling duplicate filenames.
    /// </summary>
    /// <param name="conflicts">The list of file conflicts to resolve.</param>
    /// <returns>Resolution result with user selections, or cancelled result.</returns>
    Task<FileConflictResolutionResult> ShowFileConflictDialogAsync(IEnumerable<FileConflictItem> conflicts);

    /// <summary>
    /// Shows the file conflict resolution dialog for handling duplicate filenames,
    /// also displaying non-conflicting files that will be added.
    /// </summary>
    /// <param name="conflicts">The list of file conflicts to resolve.</param>
    /// <param name="nonConflictingFilePaths">Paths to files that don't conflict and will be added.</param>
    /// <returns>Resolution result with user selections, or cancelled result.</returns>
    Task<FileConflictResolutionResult> ShowFileConflictDialogAsync(
        IEnumerable<FileConflictItem> conflicts,
        IEnumerable<string> nonConflictingFilePaths);

    /// <summary>
    /// Shows a drag-and-drop file picker dialog with conflict detection.
    /// When files are dropped that conflict with existing files, the conflict dialog is shown immediately.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="existingFileNames">Names of files that already exist in the destination.</param>
    /// <param name="destinationFolder">The destination folder path.</param>
    /// <returns>List of selected file paths with conflict resolutions applied, or null if cancelled.</returns>
    Task<FileDropWithConflictResult?> ShowFileDropDialogWithConflictDetectionAsync(
        string title,
        IEnumerable<string> existingFileNames,
        string destinationFolder);

    /// <summary>
    /// Shows a dialog for selecting which versions of a multi-version dataset to delete.
    /// Used in stacked/unflatten view when deleting a dataset with multiple versions.
    /// </summary>
    /// <param name="dataset">The dataset with multiple versions to delete from.</param>
    /// <returns>Result containing selected versions to delete, or cancelled result.</returns>
    Task<SelectVersionsToDeleteResult> ShowSelectVersionsToDeleteDialogAsync(DatasetCardViewModel dataset);

    /// <summary>
    /// Shows the add to dataset dialog for importing files into datasets.
    /// </summary>
    /// <param name="selectedFileCount">Number of selected files.</param>
    /// <param name="availableDatasets">Datasets available for selection.</param>
    /// <returns>Result describing import options, or cancelled result.</returns>
    Task<AddToDatasetResult> ShowAddToDatasetDialogAsync(
        int selectedFileCount,
        IEnumerable<DatasetCardViewModel> availableDatasets);
}

/// <summary>
/// Data for one side of the backup comparison (current or backup).
/// </summary>
public class BackupCompareData
{
    /// <summary>
    /// Label for this data set (e.g., "Current" or "Backup").
    /// </summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>
    /// Date/time for this data set.
    /// </summary>
    public DateTimeOffset Date { get; init; }

    /// <summary>
    /// Number of datasets.
    /// </summary>
    public int DatasetCount { get; init; }

    /// <summary>
    /// Number of images.
    /// </summary>
    public int ImageCount { get; init; }

    /// <summary>
    /// Number of videos.
    /// </summary>
    public int VideoCount { get; init; }

    /// <summary>
    /// Number of captions.
    /// </summary>
    public int CaptionCount { get; init; }

    /// <summary>
    /// Total size in bytes.
    /// </summary>
    public long TotalSizeBytes { get; init; }
}

/// <summary>
/// Marker interface for ViewModels that require dialog service injection.
/// Implement this interface to receive automatic DialogService injection
/// when the view is attached to the visual tree.
/// </summary>
/// <example>
/// <code>
/// public class MyViewModel : ViewModelBase, IDialogServiceAware
/// {
///     public IDialogService? DialogService { get; set; }
/// }
/// </code>
/// </example>
public interface IDialogServiceAware
{
    /// <summary>
    /// Gets or sets the dialog service. Automatically injected by ViewBase/ControlBase.
    /// </summary>
    IDialogService? DialogService { get; set; }
}

/// <summary>
/// Interface for ViewModels that support busy/loading states.
/// Used by UI to show loading indicators.
/// </summary>
/// <example>
/// <code>
/// &lt;ProgressBar IsVisible="{Binding IsBusy}"/&gt;
/// &lt;TextBlock Text="{Binding BusyMessage}"/&gt;
/// </code>
/// </example>
public interface IBusyViewModel
{
    /// <summary>
    /// Gets or sets whether the ViewModel is currently busy.
    /// </summary>
    bool IsBusy { get; set; }

    /// <summary>
    /// Gets or sets an optional message describing the current operation.
    /// </summary>
    string? BusyMessage { get; set; }
}

/// <summary>
/// Result from the file drop dialog with conflict detection.
/// </summary>
public class FileDropWithConflictResult
{
    /// <summary>
    /// Files that do not conflict and should be copied directly.
    /// </summary>
    public List<string> NonConflictingFiles { get; init; } = [];

    /// <summary>
    /// Conflict resolution results for files that had naming conflicts.
    /// </summary>
    public FileConflictResolutionResult? ConflictResolutions { get; init; }

    /// <summary>
    /// Whether the user cancelled the operation.
    /// </summary>
    public bool Cancelled { get; init; }

    /// <summary>
    /// Gets all files that should be added (non-conflicting + resolved conflicts).
    /// </summary>
    public IEnumerable<string> GetFilesToAdd()
    {
        foreach (var file in NonConflictingFiles)
        {
            yield return file;
        }

        if (ConflictResolutions?.Confirmed == true)
        {
            foreach (var conflict in ConflictResolutions.Conflicts)
            {
                if (conflict.Resolution != FileConflictResolution.Ignore)
                {
                    yield return conflict.NewFilePath;
                }
            }
        }
    }
}
