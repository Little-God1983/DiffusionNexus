using Avalonia.Threading;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Production <see cref="IUiScheduler"/> — a straight delegation to
/// <see cref="Dispatcher.UIThread"/>. Stateless, so a single shared
/// <see cref="Instance"/> is used as the default for ViewModel constructors and
/// the same type is registered as a DI singleton.
/// </summary>
public sealed class AvaloniaUiScheduler : IUiScheduler
{
    /// <summary>
    /// Shared instance used as the default when no scheduler is injected (keeps
    /// existing <c>new SomeViewModel(...)</c> call sites behaving identically).
    /// </summary>
    public static AvaloniaUiScheduler Instance { get; } = new();

    /// <inheritdoc/>
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    /// <inheritdoc/>
    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    /// <inheritdoc/>
    public bool IsOnUiThread => Dispatcher.UIThread.CheckAccess();
}
