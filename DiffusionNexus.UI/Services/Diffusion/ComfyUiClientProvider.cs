using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services.Engine;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <inheritdoc cref="IComfyUiClientProvider"/>
public sealed class ComfyUiClientProvider : IComfyUiClientProvider
{
    private const string EngineSource = "Diffusion Nexus Engine";
    private const string ComfySource = "ComfyUI";
    private static readonly ILogger Logger = Log.ForContext<ComfyUiClientProvider>();

    private readonly Func<CancellationToken, Task<AppSettings>> _readSettings;
    private readonly IEngineRootResolver _rootResolver;
    private readonly IManagedComfyUiEngine _engine;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly Func<string, IComfyUIWrapperService> _clientFactory;
    private readonly Func<string?, bool> _looksInstalled;

    /// <param name="readSettings">Reads the current settings; DI gives each call its own scope.</param>
    /// <param name="clientFactory">Test seam; defaults to <c>new ComfyUIWrapperService(url)</c>.</param>
    /// <param name="looksInstalled">Test seam; defaults to <see cref="ManagedEngineLocator.LooksInstalled"/>.</param>
    public ComfyUiClientProvider(
        Func<CancellationToken, Task<AppSettings>> readSettings,
        IEngineRootResolver rootResolver,
        IManagedComfyUiEngine engine,
        IUnifiedLogger? unifiedLogger = null,
        Func<string, IComfyUIWrapperService>? clientFactory = null,
        Func<string?, bool>? looksInstalled = null)
    {
        ArgumentNullException.ThrowIfNull(readSettings);
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(engine);
        _readSettings = readSettings;
        _rootResolver = rootResolver;
        _engine = engine;
        _unifiedLogger = unifiedLogger;
        _clientFactory = clientFactory ?? (url => new ComfyUIWrapperService(url));
        _looksInstalled = looksInstalled ?? ManagedEngineLocator.LooksInstalled;
    }

    public async Task<ComfyUiClientLease> AcquireAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Anything unexpected below (a locked database, a resolver that throws) becomes a
        // ComfyUiUnavailableException worded for the server in use, so the panel never guesses the
        // hint from a lease it never got. Cancellation passes through unchanged.
        AppSettings settings;
        try
        {
            // Read on every call: the user may have switched the dropdown since the last generate.
            settings = await _readSettings(ct);
        }
        catch (Exception ex) when (IsUnexpected(ex))
        {
            throw Unavailable(ComfySource, $"Could not read the ComfyUI server setting: {ex.Message}", ex);
        }

        if (settings.ComfyUiServerMode == ComfyUiServerMode.CustomUrl)
        {
            var url = settings.ComfyUiServerUrl;
            if (!ComfyUiUrl.IsValid(url))
            {
                // Checked before the client factory: an invalid URL throws inside new Uri(...).
                var invalid = $"The ComfyUI server URL in Settings is not valid: '{ComfyUiUrl.Describe(url)}'. " +
                              "Fix it in Settings → ComfyUI Server.";
                Warn(ComfySource, invalid);
                throw new ComfyUiUnavailableException(invalid);
            }

            Info(LogCategory.Configuration, ComfySource, $"Using your own ComfyUI at {url}.");
            IComfyUIWrapperService client;
            try
            {
                client = _clientFactory(url);
            }
            catch (Exception ex) when (IsUnexpected(ex))
            {
                throw Unavailable(ComfySource, $"Could not connect to your ComfyUI at {url}: {ex.Message}", ex);
            }

            return new ComfyUiClientLease(client, ComfyUiServerMode.CustomUrl, url, ownsClient: true);
        }

        try
        {
            return await AcquireEngineAsync(progress, ct);
        }
        catch (Exception ex) when (IsUnexpected(ex))
        {
            throw Unavailable(EngineSource, $"Diffusion Nexus Engine could not be prepared: {ex.Message}", ex);
        }
    }

    private async Task<ComfyUiClientLease> AcquireEngineAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var root = await _rootResolver.ResolveAsync(ct);
        if (!_looksInstalled(root))
        {
            const string notInstalled =
                "Diffusion Nexus Engine is not installed. Install it in the Installation Manager.";
            Warn(EngineSource, notInstalled);
            throw new ComfyUiUnavailableException(notInstalled);
        }

        if (_engine.BaseUrl is null)
        {
            progress?.Report("Starting Diffusion Nexus Engine…");
            Info(LogCategory.InstanceManagement, EngineSource, "Engine not running; starting it for this generate.");
        }

        var started = await _engine.EnsureRunningAsync(root!, ct);
        if (!started.IsRunning || started.BaseUrl is null)
        {
            var reason = $"Diffusion Nexus Engine failed to start: {started.FailureReason ?? "unknown reason"}";
            Warn(EngineSource, reason);
            throw new ComfyUiUnavailableException(reason);
        }

        Info(LogCategory.InstanceManagement, EngineSource, $"Using the Engine at {started.BaseUrl}.");
        return new ComfyUiClientLease(_clientFactory(started.BaseUrl), ComfyUiServerMode.Engine, started.BaseUrl, ownsClient: true);
    }

    private static bool IsUnexpected(Exception ex) =>
        ex is not OperationCanceledException and not ComfyUiUnavailableException;

    private ComfyUiUnavailableException Unavailable(string source, string message, Exception cause)
    {
        Logger.Warning(cause, "{Source}: {Message}", source, message);
        _unifiedLogger?.Warn(LogCategory.InstanceManagement, source, message, cause.ToString());
        return new ComfyUiUnavailableException(message, cause);
    }

    private void Info(LogCategory category, string source, string message)
    {
        Logger.Information("{Source}: {Message}", source, message);
        _unifiedLogger?.Info(category, source, message);
    }

    private void Warn(string source, string message)
    {
        Logger.Warning("{Source}: {Message}", source, message);
        _unifiedLogger?.Warn(LogCategory.InstanceManagement, source, message);
    }
}
