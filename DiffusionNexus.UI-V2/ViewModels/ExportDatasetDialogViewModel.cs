using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Export Dataset dialog.
/// Provides export options and preview counts for dataset export.
/// </summary>
public partial class ExportDatasetDialogViewModel : ObservableObject
{
    private readonly List<DatasetImageViewModel> _allMediaFiles;

    private ExportType _exportType = ExportType.SingleFiles;
    private bool _exportUnrated;
    private bool _includeFailedImages;
    private string _datasetName = string.Empty;

    /// <summary>
    /// Creates a new ExportDatasetDialogViewModel.
    /// </summary>
    /// <param name="datasetName">Name of the dataset being exported.</param>
    /// <param name="mediaFiles">All media files in the dataset.</param>
    public ExportDatasetDialogViewModel(string datasetName, IEnumerable<DatasetImageViewModel> mediaFiles)
    {
        _datasetName = datasetName;
        _allMediaFiles = mediaFiles.ToList();
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
    /// Whether to include unrated images in the export.
    /// Default: false (only export production-ready images by default).
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
    /// Whether to include failed/rejected images in the export.
    /// Default: false.
    /// </summary>
    public bool IncludeFailedImages
    {
        get => _includeFailedImages;
        set
        {
            if (SetProperty(ref _includeFailedImages, value))
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
    /// Number of media files marked as failed (rejected).
    /// </summary>
    public int FailedCount => _allMediaFiles.Count(m => m.IsRejected);

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
            // Always start with production-ready files
            var count = ProductionReadyCount;
            
            // Add unrated if checkbox is checked
            if (_exportUnrated)
                count += UnratedCount;
            
            // Add failed if checkbox is checked
            if (_includeFailedImages)
                count += FailedCount;
            
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
    /// Display text for failed count.
    /// </summary>
    public string FailedText => FormatCount(FailedCount, "failed");

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
    /// Always includes production-ready files, optionally includes unrated and/or failed.
    /// </summary>
    public List<DatasetImageViewModel> GetFilesToExport()
    {
        // Always include production-ready files
        var files = _allMediaFiles.Where(m => m.IsApproved).ToList();
        
        // Add unrated if checkbox is checked
        if (_exportUnrated)
            files.AddRange(_allMediaFiles.Where(m => m.IsUnrated));
        
        // Add failed if checkbox is checked
        if (_includeFailedImages)
            files.AddRange(_allMediaFiles.Where(m => m.IsRejected));
        
        return files;
    }

    private static string FormatCount(int count, string label)
    {
        return $"{count} {label}";
    }

    #endregion
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
    Zip
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
    /// Whether to include unrated images in the export.
    /// </summary>
    public bool ExportUnrated { get; init; }

    /// <summary>
    /// Whether to include failed images in the export.
    /// </summary>
    public bool IncludeFailedImages { get; init; }

    /// <summary>
    /// List of files to export.
    /// </summary>
    public List<DatasetImageViewModel> FilesToExport { get; init; } = [];

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static ExportDatasetResult Cancelled() => new() { Confirmed = false };
}
