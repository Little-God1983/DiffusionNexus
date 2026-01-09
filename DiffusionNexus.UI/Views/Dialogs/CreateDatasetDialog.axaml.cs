using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;
using System.Globalization;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for creating a new dataset with name, category, and type options.
/// </summary>
public partial class CreateDatasetDialog : Window
{
    private CreateDatasetDialogViewModel? _viewModel;

    public CreateDatasetDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the result after the dialog closes.
    /// Null if cancelled.
    /// </summary>
    public CreateDatasetResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with available categories.
    /// </summary>
    /// <param name="availableCategories">Categories to show in the dropdown.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public CreateDatasetDialog WithCategories(IEnumerable<DatasetCategoryViewModel> availableCategories)
    {
        _viewModel = new CreateDatasetDialogViewModel(availableCategories);
        DataContext = _viewModel;
        return this;
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.IsValid)
        {
            Result = CreateDatasetResult.Cancelled();
            Close(false);
            return;
        }

        Result = new CreateDatasetResult
        {
            Confirmed = true,
            Name = _viewModel.GetSanitizedName(),
            CategoryId = _viewModel.SelectedCategory?.Id,
            CategoryName = _viewModel.SelectedCategory?.Name,
            Type = _viewModel.SelectedType,
            IsNsfw = _viewModel.IsNsfw
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = CreateDatasetResult.Cancelled();
        Close(false);
    }
}

/// <summary>
/// Converter to display DatasetType enum values as user-friendly strings.
/// </summary>
public class DatasetTypeConverter : IValueConverter
{
    /// <summary>
    /// Singleton instance for XAML usage.
    /// </summary>
    public static readonly DatasetTypeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DatasetType type)
        {
            return type.GetDisplayName();
        }
        return value?.ToString();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
