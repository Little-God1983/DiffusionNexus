using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for creating a new version with content type and rating selection options.
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
    /// Initializes the dialog with version information and media files.
    /// </summary>
    /// <param name="currentVersion">The current version number (used as default source version).</param>
    /// <param name="availableVersions">All available versions to copy from.</param>
    /// <param name="mediaFiles">All media files in the current version.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public CreateVersionDialog WithVersionInfo(
        int currentVersion,
        IReadOnlyList<int> availableVersions,
        IEnumerable<DatasetImageViewModel> mediaFiles)
    {
        _viewModel = new CreateVersionDialogViewModel(
            currentVersion,
            availableVersions,
            mediaFiles);
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
            CopyCaptions = _viewModel.CopyCaptions,
            IncludeProductionReady = _viewModel.IncludeProductionReady,
            IncludeUnrated = _viewModel.IncludeUnrated,
            IncludeTrash = _viewModel.IncludeTrash
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = CreateVersionResult.Cancelled();
        Close(false);
    }
}
