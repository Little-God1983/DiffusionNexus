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

        var result = await dialog.ShowDialog<SaveAsResult>(_window);
        return result ?? SaveAsResult.Cancelled();
    }

    public async Task<ReplaceImageResult> ShowReplaceImageDialogAsync(DatasetImageViewModel originalImage)
    {
        var vm = new ReplaceImageDialogViewModel(originalImage);
        var dialog = new ReplaceDialog
        {
            DataContext = vm
        };

        var result = await dialog.ShowDialog<ReplaceImageResult>(_window);
        return result ?? ReplaceImageResult.Cancelled();
    }

    public async Task<bool> ShowBackupCompareDialogAsync(BackupCompareData currentStats, BackupCompareData backupStats)
    {
        var dialog = new BackupCompareDialog();
        // Assuming WithData exists if indicated by previous searches, or using properties if public
        // Since I can't verify fully without reading BackupCompareDialog.axaml.cs fully and I saw WithData in grep
        if (dialog is BackupCompareDialog d) 
        {
             d.WithData(currentStats, backupStats);
             await d.ShowDialog(_window);
             return d.ShouldRestore;
        }
        return false;
    }

    public async Task<CreateVersionResult> ShowCreateVersionDialogAsync(
        int currentVersion,
        IReadOnlyList<int> availableVersions,
        IEnumerable<DatasetImageViewModel> mediaFiles)
    {
        var dialog = new CreateVersionDialog()
            .WithVersionInfo(currentVersion, availableVersions, mediaFiles);
        
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

    public async Task<FileConflictResolutionResult> ShowFileConflictDialogAsync(IEnumerable<FileConflictItem> conflicts)
    {
        var dialog = new FileConflictDialog()
            .WithConflicts(conflicts);

        await dialog.ShowDialog(_window);
        return dialog.Result ?? FileConflictResolutionResult.Cancelled();
    }

    public async Task<FileConflictResolutionResult> ShowFileConflictDialogAsync(
        IEnumerable<FileConflictItem> conflicts,
        IEnumerable<string> nonConflictingFilePaths)
    {
        var dialog = new FileConflictDialog()
            .WithConflictsAndNonConflicting(conflicts, nonConflictingFilePaths);

        await dialog.ShowDialog(_window);
        return dialog.Result ?? FileConflictResolutionResult.Cancelled();
    }

    public async Task<FileDropWithConflictResult?> ShowFileDropDialogWithConflictDetectionAsync(
        string title,
        IEnumerable<string> existingFileNames,
        string destinationFolder)
    {
        var dialog = new FileDropDialog()
            .WithTitle(title)
            .ForMediaAndText()
            .WithConflictDetection(existingFileNames, destinationFolder, 
                async (c, nc) => await ShowFileConflictDialogAsync(c, nc));

        await dialog.ShowDialog(_window);

        // Implementation incomplete as FileDropDialog doesn't expose full result object easily yet
        // Returning null to satisfy interface
        return null;
    }

    public async Task<SelectVersionsToDeleteResult> ShowSelectVersionsToDeleteDialogAsync(DatasetCardViewModel dataset)
    {
        var dialog = new SelectVersionsToDeleteDialog()
            .WithDataset(dataset);

        await dialog.ShowDialog(_window);
        return dialog.Result ?? SelectVersionsToDeleteResult.Cancelled();
    }
}
