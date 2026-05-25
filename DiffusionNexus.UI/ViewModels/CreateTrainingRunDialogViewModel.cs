using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the Create Training Run dialog. Captures the run name plus the
/// Civitai base model and category so newly created runs land with the same
/// metadata fields the detail view exposes.
/// </summary>
public partial class CreateTrainingRunDialogViewModel : ObservableObject
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();

    private readonly ICivitaiBaseModelCatalog? _baseModelCatalog;
    private readonly HashSet<string> _existingRunNames;

    private string _name = string.Empty;
    private string? _selectedBaseModel;
    private CivitaiCategory _selectedCategory;
    private string? _nameError;

    public CreateTrainingRunDialogViewModel(
        ICivitaiBaseModelCatalog? baseModelCatalog,
        CivitaiCategory defaultCategory,
        IEnumerable<string>? existingRunNames = null)
    {
        _baseModelCatalog = baseModelCatalog;
        _existingRunNames = new HashSet<string>(
            existingRunNames ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);

        _selectedCategory = defaultCategory == CivitaiCategory.Unknown
            ? CivitaiCategory.Character
            : defaultCategory;

        _ = LoadBaseModelCatalogAsync();
    }

    /// <summary>The run name entered by the user. Doubles as the folder name.</summary>
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

    /// <summary>Selected base model label (e.g. "SDXL 1.0"). Optional.</summary>
    public string? SelectedBaseModel
    {
        get => _selectedBaseModel;
        set => SetProperty(ref _selectedBaseModel, value);
    }

    /// <summary>
    /// Selected Civitai category. Defaults to the dataset's matching category
    /// (or Character when the dataset category doesn't map to a known value).
    /// </summary>
    public CivitaiCategory SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }

    /// <summary>Validation error message for the name field.</summary>
    public string? NameError
    {
        get => _nameError;
        set => SetProperty(ref _nameError, value);
    }

    /// <summary>
    /// Base model labels sourced from the Civitai catalog. Populated
    /// asynchronously; the ComboBox stays usable while the live fetch runs.
    /// </summary>
    public ObservableCollection<string> AvailableBaseModels { get; } = [];

    /// <summary>Civitai categories for the dropdown (Unknown excluded).</summary>
    public IReadOnlyList<CivitaiCategory> AvailableCategories { get; } =
        Enum.GetValues<CivitaiCategory>().Where(c => c != CivitaiCategory.Unknown).ToArray();

    /// <summary>Whether the current input is valid and the dialog can be confirmed.</summary>
    public bool IsValid => ValidateName();

    /// <summary>
    /// Returns the sanitized folder name (invalid filename chars stripped).
    /// </summary>
    public string GetSanitizedName()
    {
        return string.Concat(_name.Where(c => !InvalidFileNameChars.Contains(c))).Trim();
    }

    /// <summary>
    /// Tries to map the dataset's user-defined category name onto a Civitai
    /// category value. Returns <see cref="CivitaiCategory.Unknown"/> when there
    /// is no name-based match.
    /// </summary>
    public static CivitaiCategory MapDatasetCategoryName(string? datasetCategoryName)
    {
        if (string.IsNullOrWhiteSpace(datasetCategoryName)) return CivitaiCategory.Unknown;

        return Enum.TryParse<CivitaiCategory>(datasetCategoryName.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : CivitaiCategory.Unknown;
    }

    private bool ValidateName()
    {
        if (string.IsNullOrWhiteSpace(_name))
        {
            NameError = "Name is required.";
            return false;
        }

        var trimmed = _name.Trim();

        if (trimmed.IndexOfAny(InvalidFileNameChars) >= 0)
        {
            NameError = "Name contains characters that are not allowed in folder names.";
            return false;
        }

        if (_existingRunNames.Contains(trimmed))
        {
            NameError = $"A training run named '{trimmed}' already exists.";
            return false;
        }

        NameError = null;
        return true;
    }

    private async Task LoadBaseModelCatalogAsync()
    {
        if (_baseModelCatalog is null) return;

        IReadOnlyList<string> labels;
        try
        {
            labels = await _baseModelCatalog.GetBaseModelsAsync().ConfigureAwait(false);
        }
        catch
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            AvailableBaseModels.Clear();
            foreach (var label in labels)
            {
                AvailableBaseModels.Add(label);
            }
        });
    }
}

/// <summary>
/// Result returned from the Create Training Run dialog.
/// </summary>
public class CreateTrainingRunResult
{
    public bool Confirmed { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? BaseModel { get; init; }

    public CivitaiCategory Category { get; init; } = CivitaiCategory.Unknown;

    public static CreateTrainingRunResult Cancelled() => new() { Confirmed = false };
}
