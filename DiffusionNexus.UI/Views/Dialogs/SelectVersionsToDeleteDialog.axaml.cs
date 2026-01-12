using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.ViewModels;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for selecting which versions to delete from a multi-version dataset.
/// Shown when clicking delete on a stacked/unflatten view dataset with multiple versions.
/// </summary>
public partial class SelectVersionsToDeleteDialog : Window, INotifyPropertyChanged
{
    private string _datasetName = string.Empty;
    private bool _hasSelection;
    private string _selectionSummary = "No versions selected";
    private string _message = string.Empty;

    public new event PropertyChangedEventHandler? PropertyChanged;

    public SelectVersionsToDeleteDialog()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the message to display at the top of the dialog.
    /// </summary>
    public string Message
    {
        get => _message;
        private set
        {
            if (_message != value)
            {
                _message = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
            }
        }
    }

    /// <summary>
    /// Gets the collection of version items to display.
    /// </summary>
    public ObservableCollection<VersionDeleteItem> VersionItems { get; } = [];

    /// <summary>
    /// Gets whether any versions are selected.
    /// </summary>
    public bool HasSelection
    {
        get => _hasSelection;
        private set
        {
            if (_hasSelection != value)
            {
                _hasSelection = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelection)));
            }
        }
    }

    /// <summary>
    /// Gets the selection summary text.
    /// </summary>
    public string SelectionSummary
    {
        get => _selectionSummary;
        private set
        {
            if (_selectionSummary != value)
            {
                _selectionSummary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionSummary)));
            }
        }
    }

    /// <summary>
    /// Gets the result after the dialog closes.
    /// Null if cancelled.
    /// </summary>
    public SelectVersionsToDeleteResult? Result { get; private set; }

    private void UpdateSelectionState()
    {
        var selectedCount = VersionItems.Count(v => v.IsSelected);
        var totalCount = VersionItems.Count;
        
        HasSelection = selectedCount > 0;
        
        if (selectedCount == 0)
            SelectionSummary = "No versions selected";
        else if (selectedCount == totalCount)
            SelectionSummary = $"All {totalCount} versions selected - entire dataset will be deleted";
        else
            SelectionSummary = $"{selectedCount} of {totalCount} versions selected";
    }

    /// <summary>
    /// Initializes the dialog with the dataset and its versions.
    /// </summary>
    /// <param name="dataset">The dataset to delete versions from.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public SelectVersionsToDeleteDialog WithDataset(DatasetCardViewModel dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        _datasetName = dataset.Name;
        Title = $"Delete Versions - {_datasetName}";
        Message = $"Select which versions of '{_datasetName}' to delete:";

        var allVersions = dataset.GetAllVersionNumbers();
        
        foreach (var version in allVersions)
        {
            var versionPath = dataset.GetVersionFolderPath(version);
            var (imageCount, videoCount, captionCount) = GetVersionMediaCounts(versionPath);
            var isNsfw = dataset.VersionNsfwFlags.GetValueOrDefault(version, false);
            
            var item = new VersionDeleteItem
            {
                Version = version,
                VersionLabel = $"V{version}",
                ImageCount = imageCount,
                VideoCount = videoCount,
                CaptionCount = captionCount,
                IsNsfw = isNsfw
            };
            
            // Subscribe to selection changes to update UI
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VersionDeleteItem.IsSelected))
                {
                    UpdateSelectionState();
                }
            };
            
            VersionItems.Add(item);
        }

        UpdateSelectionState();
        
        return this;
    }

    private static (int imageCount, int videoCount, int captionCount) GetVersionMediaCounts(string versionPath)
    {
        if (!Directory.Exists(versionPath))
            return (0, 0, 0);

        var files = Directory.EnumerateFiles(versionPath).ToList();
        
        var imageCount = files.Count(f => DatasetCardViewModel.IsImageFile(f) && !DatasetCardViewModel.IsVideoThumbnailFile(f));
        var videoCount = files.Count(f => DatasetCardViewModel.IsVideoFile(f));
        var captionCount = files.Count(f => DatasetCardViewModel.IsCaptionFile(f));
        
        return (imageCount, videoCount, captionCount);
    }

    private void OnSelectAllClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in VersionItems)
        {
            item.IsSelected = true;
        }
    }

    private void OnClearSelectionClick(object? sender, RoutedEventArgs e)
    {
        foreach (var item in VersionItems)
        {
            item.IsSelected = false;
        }
    }

    private void OnDeleteClick(object? sender, RoutedEventArgs e)
    {
        var selectedVersions = VersionItems
            .Where(v => v.IsSelected)
            .Select(v => v.Version)
            .ToList();

        if (selectedVersions.Count == 0)
        {
            Result = SelectVersionsToDeleteResult.Cancelled();
            Close(false);
            return;
        }

        var deleteAll = selectedVersions.Count == VersionItems.Count;

        Result = new SelectVersionsToDeleteResult
        {
            Confirmed = true,
            SelectedVersions = selectedVersions,
            DeleteEntireDataset = deleteAll
        };
        
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = SelectVersionsToDeleteResult.Cancelled();
        Close(false);
    }
}

/// <summary>
/// Represents a version item in the delete selection dialog.
/// </summary>
public partial class VersionDeleteItem : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private int _version;

    [ObservableProperty]
    private string _versionLabel = string.Empty;

    [ObservableProperty]
    private int _imageCount;

    [ObservableProperty]
    private int _videoCount;

    [ObservableProperty]
    private int _captionCount;

    [ObservableProperty]
    private bool _isNsfw;

    /// <summary>
    /// Gets the media count text for display.
    /// </summary>
    public string MediaCountText
    {
        get
        {
            var parts = new List<string>();
            if (ImageCount > 0) parts.Add($"{ImageCount} {(ImageCount == 1 ? "image" : "images")}");
            if (VideoCount > 0) parts.Add($"{VideoCount} {(VideoCount == 1 ? "video" : "videos")}");
            if (CaptionCount > 0) parts.Add($"{CaptionCount} {(CaptionCount == 1 ? "caption" : "captions")}");
            return parts.Count > 0 ? string.Join(", ", parts) : "Empty";
        }
    }

    /// <summary>
    /// Gets the NSFW indicator text.
    /// </summary>
    public string NsfwIndicator => "NSFW";

    /// <summary>
    /// Gets the background brush for the item.
    /// </summary>
    public IBrush Background => new SolidColorBrush(Color.FromRgb(40, 40, 40));
}

/// <summary>
/// Result from the SelectVersionsToDeleteDialog.
/// </summary>
public class SelectVersionsToDeleteResult
{
    /// <summary>
    /// Whether the user confirmed the deletion.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// The list of version numbers selected for deletion.
    /// </summary>
    public List<int> SelectedVersions { get; init; } = [];

    /// <summary>
    /// Whether all versions are selected (entire dataset should be deleted).
    /// </summary>
    public bool DeleteEntireDataset { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static SelectVersionsToDeleteResult Cancelled() => new() { Confirmed = false };
}
