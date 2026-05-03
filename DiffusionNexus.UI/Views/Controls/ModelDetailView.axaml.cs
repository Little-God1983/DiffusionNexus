using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Detail panel control that displays model information, versions, trigger words, and tags.
/// </summary>
public partial class ModelDetailView : UserControl
{
    public ModelDetailView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ModelDetailViewModel viewModel)
        {
            viewModel.DeleteMetadataConfirmationRequested -= OnDeleteMetadataConfirmationRequested;
            viewModel.DeleteMetadataConfirmationRequested += OnDeleteMetadataConfirmationRequested;
        }
    }

    private void OnDeleteMetadataConfirmationRequested(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = this.FindControl<ScrollViewer>("DetailScrollViewer");
            if (scrollViewer is null) return;

            scrollViewer.Offset = scrollViewer.Offset.WithY(scrollViewer.Extent.Height);
        }, DispatcherPriority.Loaded);
    }
}
