using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.Views.Dialogs;

/// <summary>
/// Dialog for resolving file naming conflicts when adding files to a dataset.
/// Shows a side-by-side comparison of existing and new files with resolution options.
/// Also displays non-conflicting files that will be added.
/// </summary>
public partial class FileConflictDialog : Window
{
    private FileConflictDialogViewModel? _viewModel;

    public FileConflictDialog()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Gets the resolution result after the dialog closes.
    /// Null if cancelled.
    /// </summary>
    public FileConflictResolutionResult? Result { get; private set; }

    /// <summary>
    /// Gets the list of non-conflicting file paths that were displayed.
    /// </summary>
    public IReadOnlyList<string> NonConflictingFilePaths => 
        _viewModel?.NonConflictingFiles.Select(f => f.FilePath).ToList() ?? [];

    /// <summary>
    /// Initializes the dialog with conflict information.
    /// </summary>
    /// <param name="conflicts">The list of file conflicts to resolve.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public FileConflictDialog WithConflicts(IEnumerable<FileConflictItem> conflicts)
    {
        _viewModel = new FileConflictDialogViewModel(conflicts);
        DataContext = _viewModel;

        // Load previews asynchronously
        _ = LoadPreviewsAsync();

        return this;
    }

    /// <summary>
    /// Initializes the dialog with conflict information and non-conflicting files.
    /// </summary>
    /// <param name="conflicts">The list of file conflicts to resolve.</param>
    /// <param name="nonConflictingFilePaths">Paths to files that don't conflict and will be added.</param>
    /// <returns>The dialog instance for fluent chaining.</returns>
    public FileConflictDialog WithConflictsAndNonConflicting(
        IEnumerable<FileConflictItem> conflicts,
        IEnumerable<string> nonConflictingFilePaths)
    {
        _viewModel = new FileConflictDialogViewModel(conflicts, nonConflictingFilePaths);
        DataContext = _viewModel;

        // Load previews asynchronously
        _ = LoadPreviewsAsync();

        return this;
    }

    /// <summary>
    /// Loads thumbnail previews for image files asynchronously.
    /// </summary>
    private async Task LoadPreviewsAsync()
    {
        if (_viewModel is null) return;

        foreach (var conflict in _viewModel.Conflicts.Where(c => c.IsImage))
        {
            try
            {
                // Load existing file preview
                if (File.Exists(conflict.ExistingFilePath))
                {
                    conflict.ExistingPreview = await Task.Run(() => LoadThumbnail(conflict.ExistingFilePath));
                }

                // Load new file preview
                if (File.Exists(conflict.NewFilePath))
                {
                    conflict.NewPreview = await Task.Run(() => LoadThumbnail(conflict.NewFilePath));
                }
            }
            catch
            {
                // Ignore preview loading errors
            }
        }
    }

    /// <summary>
    /// Loads a thumbnail from a file path.
    /// </summary>
    private static Bitmap? LoadThumbnail(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return Bitmap.DecodeToWidth(stream, 60, BitmapInterpolationMode.LowQuality);
        }
        catch
        {
            return null;
        }
    }

    private void OnApplyClick(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            Result = FileConflictResolutionResult.Cancelled();
            Close(false);
            return;
        }

        Result = new FileConflictResolutionResult
        {
            Confirmed = true,
            Conflicts = _viewModel.Conflicts.ToList()
        };
        Close(true);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Result = FileConflictResolutionResult.Cancelled();
        Close(false);
    }
}
