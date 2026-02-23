using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Export Dataset dialog.
/// Provides export options and preview counts for dataset export.
/// </summary>
public partial class ExportDatasetDialogViewModel : ObservableObject
{
    private readonly List<DatasetImageViewModel> _allMediaFiles;

    private ExportType _exportType = ExportType.SingleFiles;
    private bool _exportProductionReady = true;
    private bool _exportUnrated;
    private bool _exportTrash;
    private string _datasetName = string.Empty;
    private InstallerPackage? _selectedAIToolkitInstance;
    private string _aiToolkitFolderName = string.Empty;

    /// <summary>
    /// Creates a new ExportDatasetDialogViewModel.
    /// </summary>
    /// <param name="datasetName">Name of the dataset being exported.</param>
    /// <param name="mediaFiles">All media files in the dataset.</param>
    /// <param name="aiToolkitInstances">Available AI Toolkit installations for direct export.</param>
    public ExportDatasetDialogViewModel(
        string datasetName,
        IEnumerable<DatasetImageViewModel> mediaFiles,
        IEnumerable<InstallerPackage>? aiToolkitInstances = null)
    {
        _datasetName = datasetName;
        _aiToolkitFolderName = datasetName;
        _allMediaFiles = mediaFiles.ToList();

        if (aiToolkitInstances is not null)
        {
            foreach (var instance in aiToolkitInstances)
            {
                AIToolkitInstances.Add(instance);
            }
        }

        if (AIToolkitInstances.Count > 0)
        {
            _selectedAIToolkitInstance = AIToolkitInstances[0];
        }

        RefreshFolderExistsState();
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ExportDatasetDialogViewModel() : this("Sample Dataset", [])
    {
        // Design-time preview counts
    }

    #region Properties

    /// <summary>
    /// Name of the dataset being exported.
    /// </summary>
    public string DatasetName
    {
        get => _datasetName;
        set => SetProperty(ref _datasetName, value);
    }

    /// <summary>
    /// The selected export type.
    /// </summary>
    public ExportType ExportType
    {
        get => _exportType;
        set
        {
            if (SetProperty(ref _exportType, value))
            {
                OnPropertyChanged(nameof(IsSingleFilesExport));
                OnPropertyChanged(nameof(IsZipExport));
                OnPropertyChanged(nameof(IsAIToolkitExport));
                RefreshFolderExistsState();
            }
        }
    }

    /// <summary>
    /// Whether Single Files export is selected.
    /// </summary>
    public bool IsSingleFilesExport
    {
        get => _exportType == ExportType.SingleFiles;
        set
        {
            if (value)
                ExportType = ExportType.SingleFiles;
        }
    }

    /// <summary>
    /// Whether Zip export is selected.
    /// </summary>
    public bool IsZipExport
    {
        get => _exportType == ExportType.Zip;
        set
        {
            if (value)
                ExportType = ExportType.Zip;
        }
    }

    /// <summary>
    /// Whether AI Toolkit export is selected.
    /// </summary>
    public bool IsAIToolkitExport
    {
        get => _exportType == ExportType.AIToolkit;
        set
        {
            if (value)
                ExportType = ExportType.AIToolkit;
        }
    }

    /// <summary>
    /// Whether any AI Toolkit instances are available for export.
    /// </summary>
    public bool HasAIToolkitInstances => AIToolkitInstances.Count > 0;

    /// <summary>
    /// Available AI Toolkit installations.
    /// </summary>
    public ObservableCollection<InstallerPackage> AIToolkitInstances { get; } = [];

    /// <summary>
    /// The selected AI Toolkit instance to export to.
    /// </summary>
    public InstallerPackage? SelectedAIToolkitInstance
    {
        get => _selectedAIToolkitInstance;
        set
        {
            if (SetProperty(ref _selectedAIToolkitInstance, value))
                RefreshFolderExistsState();
        }
    }

    /// <summary>
    /// The folder name to create inside the AI Toolkit datasets directory.
    /// Pre-populated with the dataset name but can be changed by the user.
    /// </summary>
    public string AIToolkitFolderName
    {
        get => _aiToolkitFolderName;
        set
        {
            if (SetProperty(ref _aiToolkitFolderName, value))
                RefreshFolderExistsState();
        }
    }

    /// <summary>
    /// Whether the target AI Toolkit dataset folder already exists and contains files.
    /// </summary>
    public bool AIToolkitFolderExists { get; private set; }

    /// <summary>
    /// Number of existing files in the target folder.
    /// </summary>
    public int AIToolkitExistingFileCount { get; private set; }

    /// <summary>
    /// Display text for the existing folder warning.
    /// </summary>
    public string AIToolkitFolderExistsText => AIToolkitExistingFileCount == 1
        ? "This folder already exists and contains 1 file."
        : $"This folder already exists and contains {AIToolkitExistingFileCount} files.";

    /// <summary>
    /// How to handle an existing folder: overwrite (clear first) or merge (add/replace).
    /// </summary>
    public AIToolkitConflictMode AIToolkitConflictMode { get; set; } = AIToolkitConflictMode.Merge;

    /// <summary>
    /// Whether merge mode is selected.
    /// </summary>
    public bool IsMergeMode
    {
        get => AIToolkitConflictMode == AIToolkitConflictMode.Merge;
        set { if (value) AIToolkitConflictMode = AIToolkitConflictMode.Merge; }
    }

    /// <summary>
    /// Whether overwrite mode is selected.
    /// </summary>
    public bool IsOverwriteMode
    {
        get => AIToolkitConflictMode == AIToolkitConflictMode.Overwrite;
        set { if (value) AIToolkitConflictMode = AIToolkitConflictMode.Overwrite; }
    }

    /// <summary>
    /// Whether to export production-ready (approved) images.
    /// Default: true.
    /// </summary>
    public bool ExportProductionReady
    {
        get => _exportProductionReady;
        set
        {
            if (SetProperty(ref _exportProductionReady, value))
            {
                OnPropertyChanged(nameof(ToExportCount));
                OnPropertyChanged(nameof(ToExportText));
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    /// <summary>
    /// Whether to export unrated images.
    /// Default: false.
    /// </summary>
    public bool ExportUnrated
    {
        get => _exportUnrated;
        set
        {
            if (SetProperty(ref _exportUnrated, value))
            {
                OnPropertyChanged(nameof(ToExportCount));
                OnPropertyChanged(nameof(ToExportText));
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    /// <summary>
    /// Whether to export trash (rejected) images.
    /// Default: false.
    /// </summary>
    public bool ExportTrash
    {
        get => _exportTrash;
        set
        {
            if (SetProperty(ref _exportTrash, value))
            {
                OnPropertyChanged(nameof(ToExportCount));
                OnPropertyChanged(nameof(ToExportText));
                OnPropertyChanged(nameof(CanExport));
            }
        }
    }

    #endregion

    #region Preview Counts

    /// <summary>
    /// Total number of media files in the dataset.
    /// </summary>
    public int TotalCount => _allMediaFiles.Count;

    /// <summary>
    /// Number of media files marked as production ready (approved).
    /// </summary>
    public int ProductionReadyCount => _allMediaFiles.Count(m => m.IsApproved);

    /// <summary>
    /// Number of media files marked as trash (rejected).
    /// </summary>
    public int TrashCount => _allMediaFiles.Count(m => m.IsRejected);

    /// <summary>
    /// Number of media files that are unrated (neither approved nor rejected).
    /// </summary>
    public int UnratedCount => _allMediaFiles.Count(m => m.IsUnrated);

    /// <summary>
    /// Number of media files that will be exported based on current settings.
    /// </summary>
    public int ToExportCount
    {
        get
        {
            var count = 0;
            
            if (_exportProductionReady)
                count += ProductionReadyCount;
            
            if (_exportUnrated)
                count += UnratedCount;
            
            if (_exportTrash)
                count += TrashCount;
            
            return count;
        }
    }

    /// <summary>
    /// Display text for total count.
    /// </summary>
    public string TotalText => FormatCount(TotalCount, "total");

    /// <summary>
    /// Display text for production ready count.
    /// </summary>
    public string ProductionReadyText => FormatCount(ProductionReadyCount, "production ready");

    /// <summary>
    /// Display text for trash count.
    /// </summary>
    public string TrashText => FormatCount(TrashCount, "trash");

    /// <summary>
    /// Display text for unrated count.
    /// </summary>
    public string UnratedText => FormatCount(UnratedCount, "unrated");

    /// <summary>
    /// Display text for to-export count.
    /// </summary>
    public string ToExportText => FormatCount(ToExportCount, "to export");

    /// <summary>
    /// Whether export is possible (at least one file to export).
    /// </summary>
    public bool CanExport => ToExportCount > 0;

    #endregion

    #region Methods

    /// <summary>
    /// Gets the list of files to export based on current settings.
    /// </summary>
    public List<DatasetImageViewModel> GetFilesToExport()
    {
        var files = new List<DatasetImageViewModel>();
        
        if (_exportProductionReady)
            files.AddRange(_allMediaFiles.Where(m => m.IsApproved));
        
        if (_exportUnrated)
            files.AddRange(_allMediaFiles.Where(m => m.IsUnrated));
        
        if (_exportTrash)
            files.AddRange(_allMediaFiles.Where(m => m.IsRejected));
        
        return files;
    }

    private static string FormatCount(int count, string label)
    {
        return $"{count} {label}";
    }

    /// <summary>
    /// Checks whether the resolved AI Toolkit destination folder exists and has files.
    /// </summary>
    private void RefreshFolderExistsState()
    {
        var exists = false;
        var fileCount = 0;

        if (_selectedAIToolkitInstance is not null
            && !string.IsNullOrWhiteSpace(_aiToolkitFolderName))
        {
            var path = Path.Combine(
                _selectedAIToolkitInstance.InstallationPath,
                "datasets",
                _aiToolkitFolderName.Trim());

            if (Directory.Exists(path))
            {
                fileCount = Directory.GetFiles(path).Length;
                exists = fileCount > 0;
            }
        }

        AIToolkitFolderExists = exists;
        AIToolkitExistingFileCount = fileCount;
        OnPropertyChanged(nameof(AIToolkitFolderExists));
        OnPropertyChanged(nameof(AIToolkitExistingFileCount));
        OnPropertyChanged(nameof(AIToolkitFolderExistsText));
    }

    #endregion
}

/// <summary>
/// How to handle an existing AI Toolkit dataset folder during export.
/// </summary>
public enum AIToolkitConflictMode
{
    /// <summary>
    /// Merge into the existing folder (add new files, replace same-named files).
    /// </summary>
    Merge,

    /// <summary>
    /// Clear the folder contents first, then export.
    /// </summary>
    Overwrite
}

/// <summary>
/// Specifies the type of export to perform.
/// </summary>
public enum ExportType
{
    /// <summary>
    /// Export as individual image/video files with accompanying .txt caption files.
    /// </summary>
    SingleFiles,

    /// <summary>
    /// Export as a single ZIP archive containing all files.
    /// </summary>
    Zip,

    /// <summary>
    /// Export directly into an AI Toolkit installation's datasets folder.
    /// </summary>
    AIToolkit
}

/// <summary>
/// Result of the export dialog.
/// </summary>
public class ExportDatasetResult
{
    /// <summary>
    /// Whether the user confirmed the export (true) or cancelled (false).
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// The selected export type.
    /// </summary>
    public ExportType ExportType { get; init; }

    /// <summary>
    /// Whether to export production-ready images.
    /// </summary>
    public bool ExportProductionReady { get; init; }

    /// <summary>
    /// Whether to export unrated images.
    /// </summary>
    public bool ExportUnrated { get; init; }

    /// <summary>
    /// Whether to export trash images.
    /// </summary>
    public bool ExportTrash { get; init; }

    /// <summary>
    /// List of files to export.
    /// </summary>
    public List<DatasetImageViewModel> FilesToExport { get; init; } = [];

    /// <summary>
    /// The selected AI Toolkit installation path, when exporting to AI-Toolkit.
    /// </summary>
    public string? AIToolkitInstallationPath { get; init; }

    /// <summary>
    /// The display name of the selected AI Toolkit instance.
    /// </summary>
    public string? AIToolkitInstanceName { get; init; }

    /// <summary>
    /// The folder name to create inside the AI Toolkit datasets directory.
    /// </summary>
    public string? AIToolkitFolderName { get; init; }

    /// <summary>
    /// How to handle an existing folder when exporting to AI Toolkit.
    /// </summary>
    public AIToolkitConflictMode AIToolkitConflictMode { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static ExportDatasetResult Cancelled() => new() { Confirmed = false };
}
