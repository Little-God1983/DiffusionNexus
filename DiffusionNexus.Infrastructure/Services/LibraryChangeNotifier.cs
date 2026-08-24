using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Infrastructure.Services;

/// <summary>
/// The default <see cref="ILibraryChangeNotifier"/> — a singleton relay with no state of its own.
/// Thread safety comes from the event's own delegate immutability: the invocation list is captured
/// into a local before it is called, so a subscriber unsubscribing from another thread mid-raise
/// cannot null it out underneath us.
/// </summary>
public sealed class LibraryChangeNotifier : ILibraryChangeNotifier
{
    public event EventHandler<ModelDownloadedEventArgs>? ModelDownloaded;

    public void NotifyModelDownloaded(int modelId)
        => ModelDownloaded?.Invoke(this, new ModelDownloadedEventArgs(modelId));
}
