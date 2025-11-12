using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class LoraDownloadWindow : Window
{
    private LoraDownloadViewModel? _viewModel;

    public LoraDownloadWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.RequestClose -= OnRequestClose;
            _viewModel = null;
        }

        if (DataContext is LoraDownloadViewModel vm)
        {
            vm.RequestClose += OnRequestClose;
            _viewModel = vm;
        }
    }

    private void OnRequestClose(object? sender, bool result)
    {
        Close(result);
    }
}
