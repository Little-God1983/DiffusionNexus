using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for creating a new version with content type selection options.
/// </summary>
public partial class CreateVersionDialog : Window
{
    private CreateVersionDialogViewModel? _viewModel;

    public CreateVersionDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the result after the dialog closes.
    /// Null if cancelled.
    /// </summary>
    public CreateVersionResult? Result { get; private set; }

    /// <summary>
    /// Initializes the dialog with version information and content counts.
    /// </summary>
    /// <param name="currentVersion">The current version number.</param>
    /// <param name="nextVersion">The next version number that will be created.</param>
    /// <param name="availableVersions">All available versions to copy from.</param>
    /// <param name="imageCount">Number of images in current version.</param>
    /// <param name="videoCount">Number of videos in current version.</param>
    /// <param name="captionCount">Number of captions in current version.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public CreateVersionDialog WithVersionInfo(
        int currentVersion,
        int nextVersion,
        IReadOnlyList<int> availableVersions,
        int imageCount,
        int videoCount,
        int captionCount)
    {
        _viewModel = new CreateVersionDialogViewModel(
            currentVersion,
            nextVersion,
            availableVersions,
            imageCount,
            videoCount,
            captionCount);
        DataContext = _viewModel;
        return this;
    }

    private void OnCreateClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = CreateVersionResult.Cancelled();
            Close(false);
            return;
        }

        Result = new CreateVersionResult
        {
            Confirmed = true,
            SourceOption = _viewModel.SourceOption,
            SourceVersion = _viewModel.SelectedSourceVersion,
            CopyImages = _viewModel.CopyImages,
            CopyVideos = _viewModel.CopyVideos,
            CopyCaptions = _viewModel.CopyCaptions
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = CreateVersionResult.Cancelled();
        Close(false);
    }
}
