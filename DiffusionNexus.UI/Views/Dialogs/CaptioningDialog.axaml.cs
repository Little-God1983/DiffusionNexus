using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

public partial class CaptioningDialog : Window
{
    private CaptioningViewModel? _viewModel;

    public CaptioningDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public CaptioningDialog WithDependencies(
        ICaptioningService captioningService,
        IDialogService dialogService,
        IEnumerable<DatasetCardViewModel> availableDatasets,
        IDatasetEventAggregator? eventAggregator,
        DatasetCardViewModel? initialDataset = null,
        int? initialVersion = null)
    {
        _viewModel = new CaptioningViewModel(
            captioningService, 
            dialogService, 
            availableDatasets, 
            eventAggregator,
            initialDataset,
            initialVersion);
            
        DataContext = _viewModel;
        return this;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
