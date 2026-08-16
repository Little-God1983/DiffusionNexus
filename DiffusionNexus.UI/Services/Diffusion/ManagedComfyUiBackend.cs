using System.Runtime.CompilerServices;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.Services.Engine;
using Serilog;
// ComfyUiPathDiscovery lives in DiffusionNexus.UI.Services (not .Diffusion or .Engine) — it is
// shared by the configuration checker and the captioning model manager as well.
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>Supplies the API-format workflow the engine submits for a text2image request.</summary>
public interface IWorkflowTemplateSource
{
    /// <summary>True when a template is available.</summary>
    bool HasTemplate { get; }

    /// <summary>The template JSON, or null when none is configured.</summary>
    string? LoadTemplateJson();
}

/// <summary>
/// The Diffusion Canvas's second backend: the app-owned ComfyUI engine. Implements the same
/// <see cref="IDiffusionBackend"/> seam as the local sd.cpp backend, so the Canvas never learns
/// which engine it is talking to.
///
/// Generation is submitted as an API-format workflow. Until a template is supplied, the backend
/// says so honestly through the seam's error-as-data contract rather than pretending to work.
/// </summary>
public sealed class ManagedComfyUiBackend : IDiffusionBackend
{
    private static readonly ILogger Logger = Log.ForContext<ManagedComfyUiBackend>();

    /// <summary>
    /// Empty <see cref="IModelCatalog"/> used before the engine's models root is known.
    /// <see cref="ComfyUiModelCatalog"/> requires at least one non-empty root and throws otherwise,
    /// so it cannot represent "nothing discovered yet" itself.
    /// </summary>
    private sealed class EmptyModelCatalog : IModelCatalog
    {
        public static readonly EmptyModelCatalog Instance = new();
        public IReadOnlyList<ModelDescriptor> ListAvailable() => [];
        public ModelDescriptor? TryGet(string key) => null;
    }

    private readonly ManagedComfyUiEngine _engine;
    private readonly Func<Task<string?>> _resolveInstallRootAsync;
    private readonly IWorkflowTemplateSource? _templateSource;
    private readonly List<string> _missingRequirements = [];

    public ManagedComfyUiBackend(
        ManagedComfyUiEngine engine,
        Func<Task<string?>> resolveInstallRootAsync,
        IWorkflowTemplateSource? templateSource)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(resolveInstallRootAsync);

        _engine = engine;
        _resolveInstallRootAsync = resolveInstallRootAsync;
        _templateSource = templateSource;
    }

    public string DisplayName => "Diffusion Nexus Engine";

    /// <summary>
    /// Models discovered under the engine's install root. Empty until the engine is installed —
    /// the catalog walks the ComfyUI folder layout, which does not exist before then.
    /// </summary>
    public IModelCatalog Catalog { get; private set; } = EmptyModelCatalog.Instance;

    public IReadOnlyList<string> MissingRequirements => _missingRequirements;

    public IReadOnlyList<string> Warnings => [];

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        _missingRequirements.Clear();

        var installRoot = await _resolveInstallRootAsync().ConfigureAwait(false);

        if (!ManagedEngineLocator.LooksInstalled(installRoot))
        {
            _missingRequirements.Add(
                "The Diffusion Nexus Engine is not installed. Install it from the Installation Manager.");
            return false;
        }

        var searchPaths = ComfyUiPathDiscovery.EnumerateModelSearchPaths(installRoot!);
        Catalog = searchPaths.Count > 0 ? new ComfyUiModelCatalog(searchPaths) : EmptyModelCatalog.Instance;

        if (_templateSource is null || !_templateSource.HasTemplate)
        {
            _missingRequirements.Add(
                "No text2image workflow is configured for the engine yet, so it cannot generate.");
            return false;
        }

        var start = await _engine.EnsureRunningAsync(installRoot!, ct).ConfigureAwait(false);
        if (!start.IsRunning)
        {
            _missingRequirements.Add(start.FailureReason ?? "The engine could not be started.");
            return false;
        }

        return true;
    }

    public async IAsyncEnumerable<DiffusionStreamItem> GenerateAsync(
        DiffusionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var available = await IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (!available)
        {
            var reason = _missingRequirements.Count > 0
                ? string.Join(" ", _missingRequirements)
                : "The Diffusion Nexus Engine is not ready.";

            Logger.Warning("Engine generation refused: {Reason}", reason);

            yield return new DiffusionStreamItem(new DiffusionProgress
            {
                Phase = DiffusionPhase.Completed,
                Message = reason
            });
            yield break;
        }

        // Submission is added with the workflow template (see the workflow task). Until then this
        // point is unreachable: IsAvailableAsync returns false without a template.
        yield return new DiffusionStreamItem(new DiffusionProgress
        {
            Phase = DiffusionPhase.Completed,
            Message = "The engine is ready but workflow submission is not wired up yet."
        });
    }
}
