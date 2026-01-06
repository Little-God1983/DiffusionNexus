using Avalonia.Controls;
using Avalonia.Platform.Storage;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.Views.Dialogs;

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
}
