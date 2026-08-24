namespace DiffusionNexus.Domain.Services;

/// <summary>Carries the local model id of a model the library just gained.</summary>
public sealed class ModelDownloadedEventArgs(int modelId) : EventArgs
{
    public int ModelId { get; } = modelId;
}

/// <summary>
/// Cross-module "the library gained a model" signal. Raised by the one download path after
/// persist, subscribed by LoraViewerViewModel — fixes the Browse queue never notifying the
/// Installed tab (spec RC5) and replaces the detail panel's ad-hoc DownloadCompleted event.
/// Events are raised on the caller's thread; subscribers marshal to the UI thread themselves.
/// </summary>
public interface ILibraryChangeNotifier
{
    event EventHandler<ModelDownloadedEventArgs>? ModelDownloaded;

    void NotifyModelDownloaded(int modelId);
}
