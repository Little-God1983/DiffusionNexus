using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Extended ViewModel base class that provides busy state management
/// and dialog service awareness. Use this for ViewModels that need 
/// loading indicators or file/folder dialogs.
/// </summary>
/// <remarks>
/// <para>
/// This class provides:
/// <list type="bullet">
///   <item><description>IsBusy/BusyMessage properties for loading indicators</description></item>
///   <item><description>RunBusy/RunBusyAsync helpers that auto-manage busy state</description></item>
///   <item><description>DialogService property for file/folder pickers</description></item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public partial class MyViewModel : BusyViewModelBase
/// {
///     [RelayCommand]
///     private async Task LoadDataAsync()
///     {
///         await RunBusyAsync(async () =>
///         {
///             Data = await _service.LoadAsync();
///         }, "Loading data...");
///     }
/// }
/// </code>
/// </example>
public abstract partial class BusyViewModelBase : ViewModelBase, IDialogServiceAware, IBusyViewModel
{
    /// <summary>
    /// Gets or sets whether the ViewModel is currently performing a long-running operation.
    /// Bind to this property to show/hide loading indicators.
    /// </summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>
    /// Gets or sets an optional message describing the current operation.
    /// Display this to inform users what is happening.
    /// </summary>
    [ObservableProperty]
    private string? _busyMessage;

    /// <summary>
    /// Gets or sets the dialog service for file/folder pickers.
    /// Automatically injected by ViewBase when the view is attached to the visual tree.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Executes a synchronous action while showing a busy indicator.
    /// Automatically sets IsBusy=true before and IsBusy=false after.
    /// </summary>
    /// <param name="action">The action to execute.</param>
    /// <param name="message">Optional message to display during execution.</param>
    protected void RunBusy(Action action, string? message = null)
    {
        try
        {
            IsBusy = true;
            BusyMessage = message;
            action();
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    /// <summary>
    /// Executes an async action while showing a busy indicator.
    /// Automatically sets IsBusy=true before and IsBusy=false after.
    /// </summary>
    /// <param name="action">The async action to execute.</param>
    /// <param name="message">Optional message to display during execution.</param>
    /// <example>
    /// <code>
    /// await RunBusyAsync(async () =>
    /// {
    ///     await _apiService.FetchDataAsync();
    /// }, "Fetching data from server...");
    /// </code>
    /// </example>
    protected async Task RunBusyAsync(Func<Task> action, string? message = null)
    {
        try
        {
            IsBusy = true;
            BusyMessage = message;
            await action();
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }

    /// <summary>
    /// Executes an async action with a return value while showing a busy indicator.
    /// </summary>
    /// <typeparam name="T">The return type.</typeparam>
    /// <param name="action">The async action to execute.</param>
    /// <param name="message">Optional message to display during execution.</param>
    /// <returns>The result of the action.</returns>
    /// <example>
    /// <code>
    /// var result = await RunBusyAsync(async () =>
    /// {
    ///     return await _service.CalculateAsync();
    /// }, "Calculating...");
    /// </code>
    /// </example>
    protected async Task<T> RunBusyAsync<T>(Func<Task<T>> action, string? message = null)
    {
        try
        {
            IsBusy = true;
            BusyMessage = message;
            return await action();
        }
        finally
        {
            IsBusy = false;
            BusyMessage = null;
        }
    }
}
