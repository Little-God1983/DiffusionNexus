using System.Collections.Concurrent;

namespace DiffusionNexus.UI.ImageEditor.Events;

/// <summary>
/// Thread-safe in-memory event bus.
/// Handlers are invoked synchronously on the publisher's thread.
/// </summary>
internal sealed class EventBus : IEventBus
{
    private readonly ConcurrentDictionary<Type, List<object>> _handlers = new();
    private readonly object _lock = new();

    /// <inheritdoc />
    public void Publish<TEvent>(TEvent evt) where TEvent : notnull
    {
        var type = typeof(TEvent);
        if (!_handlers.TryGetValue(type, out var handlers))
            return;

        // Snapshot to avoid mutation during iteration
        Action<TEvent>[] snapshot;
        lock (_lock)
        {
            snapshot = handlers
                .OfType<Action<TEvent>>()
                .ToArray();
        }

        foreach (var handler in snapshot)
        {
            handler(evt);
        }
    }

    /// <inheritdoc />
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull
    {
        ArgumentNullException.ThrowIfNull(handler);

        var type = typeof(TEvent);
        var handlers = _handlers.GetOrAdd(type, _ => []);

        lock (_lock)
        {
            handlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                handlers.Remove(handler);
            }
        });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
            }
        }
    }
}
