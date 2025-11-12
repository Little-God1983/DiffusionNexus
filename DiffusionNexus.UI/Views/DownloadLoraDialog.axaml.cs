using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class DownloadLoraDialog : Window
{
    private DownloadLoraDialogViewModel? _viewModel;

    public DownloadLoraDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as DownloadLoraDialogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool result)
    {
        Close(result);
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_viewModel is not null && _viewModel.HandleWindowClosing())
        {
            e.Cancel = true;
        }
    }
}
