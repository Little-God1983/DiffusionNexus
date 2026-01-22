using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;
using System.Collections.Generic;

namespace DiffusionNexus.UI.Views.Dialogs;

public partial class AddToDatasetDialog : Window
{
    private AddToDatasetDialogViewModel? _viewModel;

    public AddToDatasetDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public AddToDatasetResult? Result { get; private set; }

    public AddToDatasetDialog WithOptions(int selectedFileCount, IEnumerable<DatasetCardViewModel> availableDatasets)
    {
        _viewModel = new AddToDatasetDialogViewModel(selectedFileCount, availableDatasets);
        DataContext = _viewModel;
        return this;
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = AddToDatasetResult.Cancelled();
            Close(false);
            return;
        }

        Result = new AddToDatasetResult
        {
            Confirmed = true,
            ImportAction = _viewModel.ImportAction,
            DestinationOption = _viewModel.DestinationOption,
            VersionOption = _viewModel.VersionOption,
            SelectedDataset = _viewModel.SelectedDataset,
            SelectedVersion = _viewModel.SelectedVersion
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = AddToDatasetResult.Cancelled();
        Close(false);
    }
}
