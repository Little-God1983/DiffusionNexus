using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

public partial class AddToDatasetDialog : Window
{
    private AddToDatasetDialogViewModel? _viewModel;

    public AddToDatasetDialog()
    {
        InitializeComponent();
    }

    public AddToDatasetResult? Result { get; private set; }

    public AddToDatasetDialog WithOptions(
        int selectionCount,
        IEnumerable<DatasetCardViewModel> availableDatasets,
        IEnumerable<DatasetCategoryViewModel> availableCategories)
    {
        _viewModel = new AddToDatasetDialogViewModel(selectionCount, availableDatasets, availableCategories);
        DataContext = _viewModel;
        return this;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || !_viewModel.CanConfirm)
        {
            Result = AddToDatasetResult.Cancelled();
            Close(false);
            return;
        }

        Result = _viewModel.ToResult();
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = AddToDatasetResult.Cancelled();
        Close(false);
    }
}
