using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Hands out a ComfyUI client for the server chosen in Settings → ComfyUI Server. The one place that
/// knows whether a job goes to the Diffusion Nexus Engine or to the user's own ComfyUI.
/// </summary>
public interface IComfyUiClientProvider
{
    /// <summary>
    /// Returns a client for the active mode. In Engine mode this starts the Engine when it is not
    /// running (reporting <c>"Starting Diffusion Nexus Engine…"</c> on <paramref name="progress"/>),
    /// which can take up to about two minutes on a cold start.
    /// </summary>
    /// <exception cref="ComfyUiUnavailableException">The Engine is not installed or failed to start.</exception>
    Task<ComfyUiClientLease> AcquireAsync(IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// A client for one operation. Dispose it when the operation ends; the Engine process itself keeps
/// running until the app exits.
/// </summary>
public sealed class ComfyUiClientLease : IDisposable
{
    private readonly bool _ownsClient;

    public ComfyUiClientLease(IComfyUIWrapperService client, ComfyUiServerMode mode, string baseUrl, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        Client = client;
        Mode = mode;
        BaseUrl = baseUrl;
        _ownsClient = ownsClient;
    }

    public IComfyUIWrapperService Client { get; }
    public ComfyUiServerMode Mode { get; }
    public string BaseUrl { get; }

    public void Dispose()
    {
        if (_ownsClient)
            Client.Dispose();
    }
}

/// <summary>The selected ComfyUI cannot be used right now. The message is written for the user.</summary>
public sealed class ComfyUiUnavailableException : Exception
{
    public ComfyUiUnavailableException(string message) : base(message) { }
}
