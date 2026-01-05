using Avalonia.Controls;
using Avalonia.Platform.Storage;
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
        // For now, using a simple approach - can be enhanced with custom dialog windows
        await Task.CompletedTask;
        // TODO: Implement custom message dialog
    }

    public async Task<bool> ShowConfirmAsync(string title, string message)
    {
        // For now, using a simple approach - can be enhanced with custom dialog windows
        await Task.CompletedTask;
        // TODO: Implement custom confirm dialog
        return true;
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
}
