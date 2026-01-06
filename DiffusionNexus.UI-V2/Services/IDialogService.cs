namespace DiffusionNexus.UI.Services;

/// <summary>
/// Provides dialog operations for file/folder pickers and message boxes.
/// Inject this interface to enable testable UI dialogs.
/// </summary>
/// <example>
/// <code>
/// var path = await DialogService.ShowOpenFolderDialogAsync("Select Output Folder");
/// if (path != null)
/// {
///     OutputPath = path;
/// }
/// </code>
/// </example>
public interface IDialogService
{
    /// <summary>
    /// Shows an open file dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="filter">Optional file filter (e.g., "*.json").</param>
    /// <returns>Selected file path, or null if cancelled.</returns>
    Task<string?> ShowOpenFileDialogAsync(string title, string? filter = null);

    /// <summary>
    /// Shows a save file dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="defaultFileName">Suggested file name.</param>
    /// <param name="filter">Optional file filter.</param>
    /// <returns>Selected save path, or null if cancelled.</returns>
    Task<string?> ShowSaveFileDialogAsync(string title, string? defaultFileName = null, string? filter = null);

    /// <summary>
    /// Shows a folder picker dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <returns>Selected folder path, or null if cancelled.</returns>
    Task<string?> ShowOpenFolderDialogAsync(string title);

    /// <summary>
    /// Shows an informational message dialog.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message content.</param>
    Task ShowMessageAsync(string title, string message);

    /// <summary>
    /// Shows a confirmation dialog with Yes/No options.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Question to ask.</param>
    /// <returns>True if confirmed, false otherwise.</returns>
    Task<bool> ShowConfirmAsync(string title, string message);

    /// <summary>
    /// Shows an input dialog for text entry.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Prompt message.</param>
    /// <param name="defaultValue">Optional default value.</param>
    /// <returns>The entered text, or null if cancelled.</returns>
    Task<string?> ShowInputAsync(string title, string message, string? defaultValue = null);

    /// <summary>
    /// Shows a drag-and-drop file picker dialog for images and text files.
    /// </summary>
    /// <param name="title">Dialog title (e.g., "Add Images to: MyDataset").</param>
    /// <returns>List of selected file paths, or null if cancelled.</returns>
    Task<List<string>?> ShowFileDropDialogAsync(string title);

    /// <summary>
    /// Shows a drag-and-drop file picker dialog with custom extensions.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="allowedExtensions">Allowed file extensions (e.g., ".png", ".jpg").</param>
    /// <returns>List of selected file paths, or null if cancelled.</returns>
    Task<List<string>?> ShowFileDropDialogAsync(string title, params string[] allowedExtensions);

    /// <summary>
    /// Shows an option selection dialog with multiple choices.
    /// </summary>
    /// <param name="title">Dialog title.</param>
    /// <param name="message">Message/description.</param>
    /// <param name="options">Array of option labels to display as buttons.</param>
    /// <returns>The index of the selected option (0-based), or -1 if cancelled.</returns>
    Task<int> ShowOptionsAsync(string title, string message, params string[] options);
}

/// <summary>
/// Marker interface for ViewModels that require dialog service injection.
/// Implement this interface to receive automatic DialogService injection
/// when the view is attached to the visual tree.
/// </summary>
/// <example>
/// <code>
/// public class MyViewModel : ViewModelBase, IDialogServiceAware
/// {
///     public IDialogService? DialogService { get; set; }
/// }
/// </code>
/// </example>
public interface IDialogServiceAware
{
    /// <summary>
    /// Gets or sets the dialog service. Automatically injected by ViewBase/ControlBase.
    /// </summary>
    IDialogService? DialogService { get; set; }
}

/// <summary>
/// Interface for ViewModels that support busy/loading states.
/// Used by UI to show loading indicators.
/// </summary>
/// <example>
/// <code>
/// &lt;ProgressBar IsVisible="{Binding IsBusy}"/&gt;
/// &lt;TextBlock Text="{Binding BusyMessage}"/&gt;
/// </code>
/// </example>
public interface IBusyViewModel
{
    /// <summary>
    /// Gets or sets whether the ViewModel is currently busy.
    /// </summary>
    bool IsBusy { get; set; }

    /// <summary>
    /// Gets or sets an optional message describing the current operation.
    /// </summary>
    string? BusyMessage { get; set; }
}
