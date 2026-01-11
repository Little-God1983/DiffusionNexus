using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Create Dataset dialog.
/// Handles validation and selection of name, category, type, and NSFW flag.
/// </summary>
public partial class CreateDatasetDialogViewModel : ObservableObject
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly IReadOnlyList<DatasetCategoryViewModel> _availableCategories;
    private readonly IReadOnlyList<DatasetType> _availableTypes;

    private string _name = string.Empty;
    private DatasetCategoryViewModel? _selectedCategory;
    private DatasetType? _selectedType;
    private string? _nameError;
    private bool _isNsfw;

    /// <summary>
    /// Creates a new CreateDatasetDialogViewModel.
    /// </summary>
    /// <param name="availableCategories">Available categories to choose from.</param>
    public CreateDatasetDialogViewModel(IEnumerable<DatasetCategoryViewModel> availableCategories)
    {
        _availableCategories = availableCategories.ToList();
        _availableTypes = DatasetTypeExtensions.GetAll();
    }

    /// <summary>
    /// The dataset name entered by the user.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                ValidateName();
                OnPropertyChanged(nameof(IsValid));
            }
        }
    }

    /// <summary>
    /// The selected category (optional).
    /// </summary>
    public DatasetCategoryViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }

    /// <summary>
    /// The selected dataset type (optional).
    /// </summary>
    public DatasetType? SelectedType
    {
        get => _selectedType;
        set => SetProperty(ref _selectedType, value);
    }

    /// <summary>
    /// Whether this dataset contains NSFW content.
    /// </summary>
    public bool IsNsfw
    {
        get => _isNsfw;
        set => SetProperty(ref _isNsfw, value);
    }

    /// <summary>
    /// Validation error message for the name field.
    /// </summary>
    public string? NameError
    {
        get => _nameError;
        set => SetProperty(ref _nameError, value);
    }

    /// <summary>
    /// Available categories for the dropdown.
    /// </summary>
    public IReadOnlyList<DatasetCategoryViewModel> AvailableCategories => _availableCategories;

    /// <summary>
    /// Available dataset types for the dropdown.
    /// </summary>
    public IReadOnlyList<DatasetType> AvailableTypes => _availableTypes;

    /// <summary>
    /// Whether the current input is valid and the dialog can be confirmed.
    /// </summary>
    public bool IsValid => ValidateName();

    /// <summary>
    /// Validates the name and returns whether it's valid.
    /// </summary>
    private bool ValidateName()
    {
        // Check minimum length
        if (string.IsNullOrWhiteSpace(_name) || _name.Length < 2)
        {
            NameError = "Name must be at least 2 characters.";
            return false;
        }

        // Check for invalid Windows filename characters
        if (_name.Any(c => InvalidFileNameChars.Contains(c)))
        {
            NameError = "Name contains invalid characters.";
            return false;
        }

        NameError = null;
        return true;
    }

    /// <summary>
    /// Gets the sanitized folder name from the entered name.
    /// </summary>
    public string GetSanitizedName()
    {
        return string.Concat(_name.Where(c => !InvalidFileNameChars.Contains(c)));
    }
}

/// <summary>
/// Result returned from the Create Dataset dialog.
/// </summary>
public class CreateDatasetResult
{
    /// <summary>
    /// Whether the user confirmed the dialog.
    /// </summary>
    public bool Confirmed { get; init; }

    /// <summary>
    /// The dataset name entered by the user.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// The selected category ID (optional).
    /// </summary>
    public int? CategoryId { get; init; }

    /// <summary>
    /// The selected category order (stable across database recreations).
    /// </summary>
    public int? CategoryOrder { get; init; }

    /// <summary>
    /// The selected category name (optional).
    /// </summary>
    public string? CategoryName { get; init; }

    /// <summary>
    /// The selected dataset type (optional).
    /// </summary>
    public DatasetType? Type { get; init; }

    /// <summary>
    /// Whether this dataset contains NSFW content.
    /// </summary>
    public bool IsNsfw { get; init; }

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static CreateDatasetResult Cancelled() => new() { Confirmed = false };
}
