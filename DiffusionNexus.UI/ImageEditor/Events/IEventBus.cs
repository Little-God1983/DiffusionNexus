namespace DiffusionNexus.UI.ImageEditor.Events;

/// <summary>
/// Decoupled publish/subscribe event bus for communication between editor services.
/// Services publish events; other services subscribe without direct references.
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event to all subscribers of <typeparamref name="TEvent"/>.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="evt">The event instance.</param>
    void Publish<TEvent>(TEvent evt) where TEvent : notnull;

    /// <summary>
    /// Subscribes to events of <typeparamref name="TEvent"/>.
    /// Dispose the returned token to unsubscribe.
    /// </summary>
    /// <typeparam name="TEvent">The event type.</typeparam>
    /// <param name="handler">The callback invoked when the event is published.</param>
    /// <returns>A disposable subscription token.</returns>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : notnull;
}
