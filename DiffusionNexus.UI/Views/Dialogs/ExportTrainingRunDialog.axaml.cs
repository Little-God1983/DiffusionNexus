using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for selecting which training run artifacts to export (epochs, images, model card).
/// </summary>
public partial class ExportTrainingRunDialog : Window
{
    private ExportTrainingRunDialogViewModel? _viewModel;

    public ExportTrainingRunDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the export result after the dialog closes.
    /// </summary>
    public ExportTrainingRunResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with training run data.
    /// </summary>
    /// <param name="trainingRun">The training run to export from.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public ExportTrainingRunDialog WithTrainingRun(TrainingRunCardViewModel trainingRun)
    {
        _viewModel = new ExportTrainingRunDialogViewModel(trainingRun);
        DataContext = _viewModel;
        return this;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = ExportTrainingRunResult.Cancelled();
            Close(false);
            return;
        }

        Result = new ExportTrainingRunResult
        {
            Confirmed = true,
            EpochPaths = _viewModel.GetSelectedEpochPaths(),
            ImagePaths = _viewModel.GetSelectedImagePaths(),
            IncludeModelCard = _viewModel.IncludeModelCard
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = ExportTrainingRunResult.Cancelled();
        Close(false);
    }
}
