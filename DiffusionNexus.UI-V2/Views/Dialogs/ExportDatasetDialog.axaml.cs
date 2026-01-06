using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for configuring and previewing dataset export options.
/// </summary>
public partial class ExportDatasetDialog : Window
{
    private ExportDatasetDialogViewModel? _viewModel;

    public ExportDatasetDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the export result after the dialog closes.
    /// Null if cancelled.
    /// </summary>
    public ExportDatasetResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with the dataset information.
    /// </summary>
    /// <param name="datasetName">Name of the dataset being exported.</param>
    /// <param name="mediaFiles">All media files in the dataset.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public ExportDatasetDialog WithDataset(string datasetName, IEnumerable<DatasetImageViewModel> mediaFiles)
    {
        _viewModel = new ExportDatasetDialogViewModel(datasetName, mediaFiles);
        DataContext = _viewModel;
        return this;
    }

    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = ExportDatasetResult.Cancelled();
            Close(false);
            return;
        }

        Result = new ExportDatasetResult
        {
            Confirmed = true,
            ExportType = _viewModel.ExportType,
            ExportUnrated = _viewModel.ExportUnrated,
            IncludeFailedImages = _viewModel.IncludeFailedImages,
            FilesToExport = _viewModel.GetFilesToExport()
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = ExportDatasetResult.Cancelled();
        Close(false);
    }
}
