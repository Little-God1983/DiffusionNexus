using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using Serilog;

namespace DiffusionNexus.UI.ViewModels.Dialogs;

/// <summary>
/// Represents an individual image within a duplicate cluster in the fixer view.
/// Each image can be selected for comparison or chosen as the one to keep.
/// </summary>
public partial class DuplicateFixerImageItem : ObservableObject
{
    /// <summary>Full path to the image file.</summary>
    public required string FilePath { get; init; }

    /// <summary>File name for display.</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Parent cluster this image belongs to.</summary>
    public required DuplicateFixerClusterItem Cluster { get; init; }

    /// <summary>Rating status loaded from the .rating sidecar file.</summary>
    public ImageRatingStatus RatingStatus { get; private set; } = ImageRatingStatus.Unrated;

    /// <summary>Display label for the rating: "Ready", "Trash", or empty for unrated.</summary>
    public string RatingLabel => RatingStatus switch
    {
        ImageRatingStatus.Approved => "Ready",
        ImageRatingStatus.Rejected => "Trash",
        _ => string.Empty
    };

    /// <summary>Whether this image has a rating to display.</summary>
    public bool HasRating => RatingStatus != ImageRatingStatus.Unrated;

    /// <summary>Color for the rating badge.</summary>
    public string RatingColor => RatingStatus switch
    {
        ImageRatingStatus.Approved => "#4CAF50",
        ImageRatingStatus.Rejected => "#FF6B6B",
        _ => "Transparent"
    };

    /// <summary>Loads rating from the .rating sidecar file next to the image.</summary>
    public void LoadRating()
    {
        var ratingPath = Path.ChangeExtension(FilePath, ".rating");
        if (!File.Exists(ratingPath))
            return;

        try
        {
            var content = File.ReadAllText(ratingPath).Trim();
            if (Enum.TryParse<ImageRatingStatus>(content, out var status))
            {
                RatingStatus = status;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Represents a duplicate cluster in the fixer view with selectable images.
/// </summary>
public partial class DuplicateFixerClusterItem : ObservableObject
{
    private bool _isResolved;

    /// <summary>Group label (e.g. "Exact Duplicate Group 1").</summary>
    public required string GroupLabel { get; init; }

    /// <summary>Formatted similarity display.</summary>
    public required string SimilarityDisplay { get; init; }

    /// <summary>Similarity percentage.</summary>
    public required double SimilarityPercent { get; init; }

    /// <summary>Whether these are exact duplicates.</summary>
    public required bool IsExactDuplicate { get; init; }

    /// <summary>Severity color for display.</summary>
    public string SeverityColor => IsExactDuplicate ? "#FF6B6B" : "#FFA726";

    /// <summary>Severity icon for display.</summary>
    public string SeverityIcon => IsExactDuplicate ? "\u2716" : "\u26A0";

    /// <summary>Individual images in this cluster.</summary>
    public ObservableCollection<DuplicateFixerImageItem> Images { get; } = [];

    /// <summary>Whether this cluster has been resolved (an image was kept/deleted).</summary>
    public bool IsResolved
    {
        get => _isResolved;
        set => SetProperty(ref _isResolved, value);
    }
}

/// <summary>
/// ViewModel for the Duplicate Fixer window.
/// Shows an image comparer at the top and a list of duplicate clusters at the bottom.
/// The user selects an image to keep; the others in the cluster are deleted after confirmation.
/// </summary>
public partial class DuplicateFixerViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<DuplicateFixerViewModel>();

    private DuplicateFixerClusterItem? _selectedCluster;
    private DuplicateFixerImageItem? _selectedLeftImage;
    private DuplicateFixerImageItem? _selectedRightImage;
    private string _leftComparePath = string.Empty;
    private string _rightComparePath = string.Empty;
    private int _deletedCount;

    /// <summary>All duplicate clusters to resolve.</summary>
    public ObservableCollection<DuplicateFixerClusterItem> Clusters { get; } = [];

    /// <summary>Currently selected cluster.</summary>
    public DuplicateFixerClusterItem? SelectedCluster
    {
        get => _selectedCluster;
        set
        {
            if (SetProperty(ref _selectedCluster, value))
            {
                OnPropertyChanged(nameof(HasSelectedCluster));
                AutoSelectImages();
            }
        }
    }

    /// <summary>Whether a cluster is selected.</summary>
    public bool HasSelectedCluster => _selectedCluster is not null;

    /// <summary>Left image in the comparer.</summary>
    public DuplicateFixerImageItem? SelectedLeftImage
    {
        get => _selectedLeftImage;
        set
        {
            if (SetProperty(ref _selectedLeftImage, value))
            {
                LeftComparePath = value?.FilePath ?? string.Empty;
                OnPropertyChanged(nameof(CanKeepLeft));
                KeepLeftCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Right image in the comparer.</summary>
    public DuplicateFixerImageItem? SelectedRightImage
    {
        get => _selectedRightImage;
        set
        {
            if (SetProperty(ref _selectedRightImage, value))
            {
                RightComparePath = value?.FilePath ?? string.Empty;
                OnPropertyChanged(nameof(CanKeepRight));
                KeepRightCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Left image path for the comparer control.</summary>
    public string LeftComparePath
    {
        get => _leftComparePath;
        private set => SetProperty(ref _leftComparePath, value);
    }

    /// <summary>Right image path for the comparer control.</summary>
    public string RightComparePath
    {
        get => _rightComparePath;
        private set => SetProperty(ref _rightComparePath, value);
    }

    /// <summary>Number of images deleted in this session.</summary>
    public int DeletedCount
    {
        get => _deletedCount;
        private set => SetProperty(ref _deletedCount, value);
    }

    /// <summary>Whether the left image can be kept (both images selected).</summary>
    public bool CanKeepLeft => _selectedLeftImage is not null && _selectedRightImage is not null;

    /// <summary>Whether the right image can be kept (both images selected).</summary>
    public bool CanKeepRight => _selectedLeftImage is not null && _selectedRightImage is not null;

    /// <summary>Keeps the left image and deletes the right after confirmation.</summary>
    public IAsyncRelayCommand KeepLeftCommand { get; }

    /// <summary>Keeps the right image and deletes the left after confirmation.</summary>
    public IAsyncRelayCommand KeepRightCommand { get; }

    /// <summary>
    /// Dialog service for showing confirmation dialogs. Set by the window.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Creates a new <see cref="DuplicateFixerViewModel"/>.
    /// </summary>
    public DuplicateFixerViewModel()
    {
        KeepLeftCommand = new AsyncRelayCommand(KeepLeftAsync, () => CanKeepLeft);
        KeepRightCommand = new AsyncRelayCommand(KeepRightAsync, () => CanKeepRight);
    }

    /// <summary>
    /// Populates the fixer from analyzed cluster data.
    /// </summary>
    public void LoadClusters(IEnumerable<DuplicateClusterItemViewModel> clusters)
    {
        Clusters.Clear();

        foreach (var src in clusters)
        {
            var clusterItem = new DuplicateFixerClusterItem
            {
                GroupLabel = src.GroupLabel,
                SimilarityDisplay = src.SimilarityDisplay,
                SimilarityPercent = src.SimilarityPercent,
                IsExactDuplicate = src.IsExactDuplicate
            };

            foreach (var path in src.ImagePaths)
            {
                var imageItem = new DuplicateFixerImageItem
                {
                    FilePath = path,
                    Cluster = clusterItem
                };
                imageItem.LoadRating();
                clusterItem.Images.Add(imageItem);
            }

            Clusters.Add(clusterItem);
        }

        // Auto-select first unresolved cluster
        SelectedCluster = Clusters.FirstOrDefault(c => !c.IsResolved);
    }

    private void AutoSelectImages()
    {
        if (_selectedCluster is null || _selectedCluster.Images.Count < 2)
        {
            SelectedLeftImage = null;
            SelectedRightImage = null;
            return;
        }

        SelectedLeftImage = _selectedCluster.Images[0];
        SelectedRightImage = _selectedCluster.Images[1];
    }

    private async Task KeepLeftAsync()
    {
        if (_selectedLeftImage is null || _selectedRightImage is null)
            return;

        await DeleteImageAsync(_selectedRightImage, _selectedLeftImage);
    }

    private async Task KeepRightAsync()
    {
        if (_selectedLeftImage is null || _selectedRightImage is null)
            return;

        await DeleteImageAsync(_selectedLeftImage, _selectedRightImage);
    }

    private async Task DeleteImageAsync(DuplicateFixerImageItem toDelete, DuplicateFixerImageItem toKeep)
    {
        if (DialogService is null)
            return;

        var confirmed = await DialogService.ShowConfirmAsync(
            "Delete Duplicate Image",
            $"You are keeping:\n  {toKeep.FileName}\n\nThis will permanently delete:\n  {toDelete.FileName}\n\nAre you sure?");

        if (!confirmed)
            return;

        try
        {
            if (File.Exists(toDelete.FilePath))
            {
                // Also delete associated caption sidecar files
                File.Delete(toDelete.FilePath);
                DeleteSidecarFiles(toDelete.FilePath);
                Logger.Information("Deleted duplicate image: {Path}", toDelete.FilePath);
            }

            DeletedCount++;

            // Remove from cluster
            var cluster = toDelete.Cluster;
            cluster.Images.Remove(toDelete);

            // If cluster only has 1 image left, mark as resolved
            if (cluster.Images.Count <= 1)
            {
                cluster.IsResolved = true;
                // Move to next unresolved cluster
                SelectedCluster = Clusters.FirstOrDefault(c => !c.IsResolved) ?? cluster;
            }
            else
            {
                // Re-select images in current cluster
                AutoSelectImages();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to delete duplicate image: {Path}", toDelete.FilePath);
            await DialogService.ShowMessageAsync("Error", $"Failed to delete file:\n{ex.Message}");
        }
    }

    private static void DeleteSidecarFiles(string imagePath)
    {
        var dir = Path.GetDirectoryName(imagePath);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(imagePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(nameWithoutExt))
            return;

        string[] sidecarExtensions = [".txt", ".caption"];
        foreach (var ext in sidecarExtensions)
        {
            var sidecarPath = Path.Combine(dir, nameWithoutExt + ext);
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
                Log.Information("Deleted sidecar file: {Path}", sidecarPath);
            }
        }
    }
}
