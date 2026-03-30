using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

public partial class AddToTrainingRunDialog : Window
{
    private AddToTrainingRunDialogViewModel? _viewModel;

    public AddToTrainingRunDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public AddToTrainingRunResult? Result { get; private set; }

    /// <summary>
    /// Configures the dialog with available datasets and selected file count.
    /// </summary>
    public AddToTrainingRunDialog WithOptions(int selectedFileCount, IEnumerable<DatasetCardViewModel> availableDatasets)
    {
        _viewModel = new AddToTrainingRunDialogViewModel(selectedFileCount, availableDatasets);
        DataContext = _viewModel;
        return this;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = AddToTrainingRunResult.Cancelled();
            Close(false);
            return;
        }

        Result = new AddToTrainingRunResult
        {
            Confirmed = true,
            ImportAction = _viewModel.ImportAction,
            SelectedDataset = _viewModel.SelectedDataset,
            SelectedVersion = _viewModel.SelectedVersionItem?.Version,
            IsNewTrainingRun = _viewModel.IsCreateNewRun,
            NewTrainingRunName = _viewModel.IsCreateNewRun ? _viewModel.NewTrainingRunName.Trim() : null,
            SelectedTrainingRunName = _viewModel.IsCreateNewRun
                ? null
                : _viewModel.SelectedTrainingRun?.Name
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = AddToTrainingRunResult.Cancelled();
        Close(false);
    }
}
