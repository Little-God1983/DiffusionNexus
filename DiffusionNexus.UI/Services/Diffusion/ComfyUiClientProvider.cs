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
        // Read on every call: the user may have switched the dropdown since the last generate.
        var settings = await _readSettings(ct);

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
            return new ComfyUiClientLease(_clientFactory(url), ComfyUiServerMode.CustomUrl, url, ownsClient: true);
        }

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
