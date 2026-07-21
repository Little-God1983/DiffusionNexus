namespace DiffusionNexus.UI.Services;

/// <summary>
/// Abstraction over the Avalonia UI-thread dispatcher. Exists purely as a testing
/// seam: ViewModels marshal work back onto the UI thread through this interface
/// instead of calling <c>Dispatcher.UIThread</c> directly, so their marshalling
/// logic can be exercised synchronously in unit tests (see
/// <c>ImmediateUiScheduler</c> in the test project).
/// <para>
/// The production implementation (<see cref="AvaloniaUiScheduler"/>) is a straight
/// delegation to <c>Dispatcher.UIThread</c> — there is no behavioural difference
/// from the original direct calls.
/// </para>
/// </summary>
public interface IUiScheduler
{
    /// <summary>
    /// Queues <paramref name="action"/> to run on the UI thread and returns
    /// immediately (fire-and-forget). Maps to <c>Dispatcher.UIThread.Post</c>.
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread and returns a task that
    /// completes when it has finished. Maps to
    /// <c>Dispatcher.UIThread.InvokeAsync</c>.
    /// </summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Whether the calling thread is the UI thread. Maps to
    /// <c>Dispatcher.UIThread.CheckAccess()</c>.
    /// </summary>
    bool IsOnUiThread { get; }
}
