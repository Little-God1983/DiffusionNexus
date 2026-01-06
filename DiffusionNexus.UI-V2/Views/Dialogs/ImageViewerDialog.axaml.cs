using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Full-screen image viewer dialog for browsing and rating dataset images.
/// </summary>
public partial class ImageViewerDialog : Window
{
    private ImageViewerViewModel? _viewModel;

    public ImageViewerDialog()
    {
        InitializeComponent();
        
        // Handle keyboard navigation
        KeyDown += OnKeyDown;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Initializes the viewer with the dataset images.
    /// </summary>
    /// <param name="images">Collection of all images in the dataset.</param>
    /// <param name="startIndex">Index of the image to display first.</param>
    /// <param name="onSendToImageEditor">Callback when user wants to send to editor.</param>
    /// <param name="onDeleteRequested">Callback when user wants to delete an image.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public ImageViewerDialog WithImages(
        ObservableCollection<DatasetImageViewModel> images,
        int startIndex,
        Action<DatasetImageViewModel>? onSendToImageEditor = null,
        Action<DatasetImageViewModel>? onDeleteRequested = null)
    {
        _viewModel = new ImageViewerViewModel(images, startIndex, onSendToImageEditor, onDeleteRequested);
        _viewModel.CloseRequested += (_, _) => Close();
        DataContext = _viewModel;
        return this;
    }

    /// <summary>
    /// Handles keyboard input for navigation and actions.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        switch (e.Key)
        {
            case Key.Left:
                _viewModel.PreviousCommand.Execute(null);
                e.Handled = true;
                break;
                
            case Key.Right:
                _viewModel.NextCommand.Execute(null);
                e.Handled = true;
                break;
                
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
                
            case Key.Up:
            case Key.W:
                _viewModel.MarkApprovedCommand.Execute(null);
                e.Handled = true;
                break;
                
            case Key.Down:
            case Key.S:
                _viewModel.MarkRejectedCommand.Execute(null);
                e.Handled = true;
                break;
                
            case Key.E:
                if (_viewModel.IsImage)
                {
                    _viewModel.SendToImageEditorCommand.Execute(null);
                    e.Handled = true;
                }
                break;
                
            case Key.Delete:
                _viewModel.DeleteCommand.Execute(null);
                e.Handled = true;
                break;
                
            case Key.Space:
                // Space advances to next image (common in image viewers)
                _viewModel.NextCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Handles click on the background to close the viewer.
    /// </summary>
    private void OnBackgroundPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only close if clicking directly on the background border
        // The image and controls should not trigger this
        if (e.Source == sender)
        {
            Close();
        }
    }
}
