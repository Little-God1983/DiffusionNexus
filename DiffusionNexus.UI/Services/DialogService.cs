using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views.Dialogs;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Avalonia implementation of IDialogService using Window's StorageProvider.
/// </summary>
public class DialogService : IDialogService
{
    private readonly Window _window;

    public DialogService(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public async Task<string?> ShowOpenFileDialogAsync(string title, string? filter = null)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        if (!string.IsNullOrEmpty(filter))
        {
            options.FileTypeFilter = new[] { new FilePickerFileType(filter) { Patterns = new[] { filter } } };
        }

        var result = await _window.StorageProvider.OpenFilePickerAsync(options);
        return result.FirstOrDefault()?.Path.LocalPath;
    }

    public async Task<string?> ShowOpenFileDialogAsync(string title, string startFolder, string? filter)
    {
        var options = new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        // Set starting folder if it exists
        if (!string.IsNullOrEmpty(startFolder) && Directory.Exists(startFolder))
        {
            options.SuggestedStartLocation = await _window.StorageProvider.TryGetFolderFromPathAsync(startFolder);
        }

        if (!string.IsNullOrEmpty(filter))
        {
            options.FileTypeFilter = new[] { new FilePickerFileType(filter) { Patterns = new[] { filter } } };
        }

        var result = await _window.StorageProvider.OpenFilePickerAsync(options);
        return result.FirstOrDefault()?.Path.LocalPath;
    }

    public async Task<string?> ShowSaveFileDialogAsync(string title, string? defaultFileName = null, string? filter = null)
    {
        var options = new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = defaultFileName
        };

        var result = await _window.StorageProvider.SaveFilePickerAsync(options);
        return result?.Path.LocalPath;
    }

    public async Task<string?> ShowOpenFolderDialogAsync(string title)
    {
        var options = new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        };

        var result = await _window.StorageProvider.OpenFolderPickerAsync(options);
        return result.FirstOrDefault()?.Path.LocalPath;
    }

    public async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ConfirmDialog
        {
            Message = message
        };
        dialog.Title = title;

        await dialog.ShowDialog(_window);
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = new ConfirmDialog
        {
            Message = message
        };
        dialog.Title = title;

        await dialog.ShowDialog(_window);
        return dialog.Result;
    }

    public async Task<string?> ShowInputAsync(string title, string message, string? defaultValue = null)
    {
        var dialog = new TextInputDialog
        {
            Message = message,
            InputText = defaultValue ?? string.Empty
        };
        dialog.Title = title;

        await dialog.ShowDialog(_window);
        return dialog.ResultText;
    }

    public async Task<List<string>?> ShowFileDropDialogAsync(string title)
    {
        var dialog = new FileDropDialog()
            .WithTitle(title)
            .ForMediaAndText();

        await dialog.ShowDialog(_window);
        return dialog.ResultFiles;
    }

    public async Task<List<string>?> ShowFileDropDialogAsync(string title, params string[] allowedExtensions)
    {
        var dialog = new FileDropDialog()
            .WithTitle(title)
            .WithExtensions(allowedExtensions);

        await dialog.ShowDialog(_window);
        return dialog.ResultFiles;
    }

    public async Task<List<string>?> ShowFileDropDialogAsync(string title, IEnumerable<string> initialFiles)
    {
        var dialog = new FileDropDialog()
            .WithTitle(title)
            .ForMediaAndText()
            .WithInitialFiles(initialFiles);

        await dialog.ShowDialog(_window);
        return dialog.ResultFiles;
    }

    public async Task<int> ShowOptionsAsync(string title, string message, params string[] options)
    {
        var dialog = new OptionsDialog
        {
            Message = message
        };
        dialog.Title = title;
        dialog.SetOptions(options);

        await dialog.ShowDialog(_window);
        return dialog.SelectedIndex;
    }

    public async Task<ExportDatasetResult> ShowExportDialogAsync(string datasetName, IEnumerable<DatasetImageViewModel> mediaFiles)
    {
        var dialog = new ExportDatasetDialog()
            .WithDataset(datasetName, mediaFiles);

        await dialog.ShowDialog(_window);
        return dialog.Result ?? ExportDatasetResult.Cancelled();
    }

    public async Task<CreateDatasetResult> ShowCreateDatasetDialogAsync(IEnumerable<DatasetCategoryViewModel> availableCategories)
    {
        var dialog = new CreateDatasetDialog()
            .WithCategories(availableCategories);

        await dialog.ShowDialog(_window);
        return dialog.Result ?? CreateDatasetResult.Cancelled();
    }

    public async Task ShowImageViewerDialogAsync(
        ObservableCollection<DatasetImageViewModel> images,
        int startIndex,
        IDatasetEventAggregator? eventAggregator = null,
        Action<DatasetImageViewModel>? onSendToImageEditor = null,
        Action<DatasetImageViewModel>? onDeleteRequested = null)
    {
        var dialog = new ImageViewerDialog()
            .WithImages(images, startIndex, eventAggregator, onSendToImageEditor, onDeleteRequested);

        await dialog.ShowDialog(_window);
    }

    public async Task<SaveAsResult> ShowSaveAsDialogAsync(string originalFilePath)
    {
        var dialog = new SaveAsDialog()
            .WithOriginalFile(originalFilePath);

        await dialog.ShowDialog(_window);
        return dialog.Result ?? SaveAsResult.Cancelled();
    }

    public async Task<bool> ShowBackupCompareDialogAsync(BackupCompareData currentStats, BackupCompareData backupStats)
    {
        var dialog = new BackupCompareDialog()
            .WithData(currentStats, backupStats);

        await dialog.ShowDialog(_window);
        return dialog.ShouldRestore;
    }

    public async Task<CreateVersionResult> ShowCreateVersionDialogAsync(
        int currentVersion,
        IReadOnlyList<int> availableVersions,
        int imageCount,
        int videoCount,
        int captionCount)
    {
        var dialog = new CreateVersionDialog()
            .WithVersionInfo(currentVersion, availableVersions, imageCount, videoCount, captionCount);

        await dialog.ShowDialog(_window);
        return dialog.Result ?? CreateVersionResult.Cancelled();
    }

    public async Task ShowCaptioningDialogAsync(
        ICaptioningService captioningService,
        IEnumerable<DatasetCardViewModel> availableDatasets,
        IDatasetEventAggregator? eventAggregator = null)
    {
        var dialog = new CaptioningDialog()
            .WithDependencies(captioningService, this, availableDatasets, eventAggregator);

        await dialog.ShowDialog(_window);
    }
}
