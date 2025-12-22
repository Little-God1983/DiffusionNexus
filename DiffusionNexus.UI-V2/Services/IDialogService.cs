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
