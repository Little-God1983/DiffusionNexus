using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for creating a new training run. Captures the name plus the Civitai
/// base model and category so the run is created with the same metadata fields
/// the detail view exposes.
/// </summary>
public partial class CreateTrainingRunDialog : Window
{
    private CreateTrainingRunDialogViewModel? _viewModel;

    public CreateTrainingRunDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CreateTrainingRunResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with the catalog used for the base-model dropdown
    /// and a default category derived from the parent dataset.
    /// </summary>
    public CreateTrainingRunDialog WithContext(
        ICivitaiBaseModelCatalog? baseModelCatalog,
        CivitaiCategory defaultCategory,
        IEnumerable<string>? existingRunNames = null)
    {
        _viewModel = new CreateTrainingRunDialogViewModel(baseModelCatalog, defaultCategory, existingRunNames);
        DataContext = _viewModel;
        return this;
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.IsValid)
        {
            Result = CreateTrainingRunResult.Cancelled();
            Close(false);
            return;
        }

        Result = new CreateTrainingRunResult
        {
            Confirmed = true,
            Name = _viewModel.GetSanitizedName(),
            BaseModel = string.IsNullOrWhiteSpace(_viewModel.SelectedBaseModel) ? null : _viewModel.SelectedBaseModel,
            Category = _viewModel.SelectedCategory
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = CreateTrainingRunResult.Cancelled();
        Close(false);
    }
}
