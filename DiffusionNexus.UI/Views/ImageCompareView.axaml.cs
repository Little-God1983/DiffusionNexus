using Avalonia.Controls;
using Avalonia.Input;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views;

public partial class ImageCompareView : UserControl
{
    public ImageCompareView()
    {
        InitializeComponent();
    }

    private void OnTrayPointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is ImageCompareViewModel viewModel)
        {
            viewModel.RequestTrayOpen();
        }
    }

    private async void OnTrayPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is ImageCompareViewModel viewModel)
        {
            await viewModel.RequestTrayCloseAsync();
        }
    }
}
