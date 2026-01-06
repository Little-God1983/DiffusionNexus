using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a group of datasets organized by category.
/// Used in the Dataset Overview to display datasets with category separator lines.
/// </summary>
/// <remarks>
/// This design allows for flexible presentation:
/// - The current implementation uses horizontal separator lines with category headers
/// - Future implementations could use collapsible sections, tabs, or other grouping visualizations
/// </remarks>
public partial class DatasetGroupViewModel : ObservableObject
{
    /// <summary>
    /// The category ID. Null represents "Uncategorized" datasets.
    /// </summary>
    public int? CategoryId { get; init; }

    /// <summary>
    /// The display name for this group (category name or "Uncategorized").
    /// </summary>
    [ObservableProperty]
    private string _name = string.Empty;

    /// <summary>
    /// Optional description for the category.
    /// </summary>
    [ObservableProperty]
    private string? _description;

    /// <summary>
    /// The datasets belonging to this category group.
    /// </summary>
    public ObservableCollection<DatasetCardViewModel> Datasets { get; } = [];

    /// <summary>
    /// Whether this group has any datasets.
    /// </summary>
    public bool HasDatasets => Datasets.Count > 0;

    /// <summary>
    /// Display text showing dataset count.
    /// </summary>
    public string DatasetCountText => Datasets.Count == 1 ? "1 dataset" : $"{Datasets.Count} datasets";

    /// <summary>
    /// Whether this is the "Uncategorized" group.
    /// </summary>
    public bool IsUncategorized => CategoryId is null;

    /// <summary>
    /// Sort order for display (categories come first, uncategorized last).
    /// </summary>
    public int SortOrder { get; init; }

    /// <summary>
    /// Creates an "Uncategorized" group for datasets without a category.
    /// </summary>
    public static DatasetGroupViewModel CreateUncategorized(int sortOrder = int.MaxValue)
    {
        return new DatasetGroupViewModel
        {
            CategoryId = null,
            Name = "Uncategorized",
            Description = "Datasets without a category",
            SortOrder = sortOrder
        };
    }

    /// <summary>
    /// Creates a group from a category.
    /// </summary>
    public static DatasetGroupViewModel FromCategory(DatasetCategoryViewModel category, int sortOrder)
    {
        return new DatasetGroupViewModel
        {
            CategoryId = category.Id,
            Name = category.Name ?? "Unknown",
            Description = category.Description,
            SortOrder = sortOrder
        };
    }

    /// <summary>
    /// Refreshes computed properties after datasets are added/removed.
    /// </summary>
    public void RefreshCounts()
    {
        OnPropertyChanged(nameof(HasDatasets));
        OnPropertyChanged(nameof(DatasetCountText));
    }
}
