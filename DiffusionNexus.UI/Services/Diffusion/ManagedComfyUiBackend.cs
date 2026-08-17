using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.Service.Services;
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

    /// <summary>
    /// The one model the shipped workflow (<see cref="Krea2WorkflowPatcher"/>) can currently
    /// generate. It carries no on-disk file paths because the engine backend never loads it the
    /// way <c>StableDiffusionCppLoader</c> loads a local model — ComfyUI resolves its own files
    /// from the graph. Width/height mirror the workflow's own default aspect ratio node.
    /// </summary>
    public static readonly ModelDescriptor Krea2Model = new()
    {
        Key = "krea2",
        DisplayName = "Krea 2 Turbo",
        Kind = ModelKind.Krea2,
        DefaultSteps = 8,
        DefaultCfg = 1.0f,
        DefaultSampler = "euler",
        DefaultScheduler = "simple",
        DefaultWidth = 1024,
        DefaultHeight = 1024,
    };

    /// <summary>
    /// Unions the fixed <see cref="Krea2Model"/> descriptor with whatever the generic
    /// <see cref="ComfyUiModelCatalog"/> disk scan finds under the engine's install root.
    ///
    /// This exists to close a gap the Canvas would otherwise hit silently: <c>ComfyUiModelCatalog</c>
    /// (see <c>Discover()</c>) only recognizes the four models the local stable-diffusion.cpp backend
    /// can load — it has no notion of Krea 2 at all, since Krea 2 only runs through this engine's
    /// ComfyUI workflow. Without this wrapper, <c>Catalog.TryGet("krea2")</c> would always return
    /// null even on a fully installed engine, and generation could never resolve its own model.
    /// </summary>
    private sealed class EngineModelCatalog(ModelDescriptor fixedDescriptor, IModelCatalog discovered) : IModelCatalog
    {
        public IReadOnlyList<ModelDescriptor> ListAvailable() =>
            new[] { fixedDescriptor }.Concat(discovered.ListAvailable()).ToList();

        public ModelDescriptor? TryGet(string key) =>
            string.Equals(key, fixedDescriptor.Key, StringComparison.Ordinal)
                ? fixedDescriptor
                : discovered.TryGet(key);
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
    /// Models this backend can generate. Always includes <see cref="Krea2Model"/> — the workflow
    /// is fixed regardless of install state — plus whatever else is discovered under the engine's
    /// install root once known (empty before the engine is installed).
    /// </summary>
    public IModelCatalog Catalog { get; private set; } = new EngineModelCatalog(Krea2Model, EmptyModelCatalog.Instance);

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
        var discovered = searchPaths.Count > 0 ? (IModelCatalog)new ComfyUiModelCatalog(searchPaths) : EmptyModelCatalog.Instance;
        Catalog = new EngineModelCatalog(Krea2Model, discovered);

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

        // The base engine install deliberately excludes every custom node (see
        // ManagedEngineInstaller) — only the Krea 2 Turbo workload installs the ones this
        // template hard-requires. "Engine installed + running" alone does not mean "ready to
        // generate": install engine, select it, press Generate is the most likely first-run
        // sequence for a user who hasn't installed the workload yet, and without this check it
        // would fail with whatever raw text ComfyUI returns instead of a fixable message.
        using var wrapper = new ComfyUIWrapperService(_engine.BaseUrl!);
        var missingNodes = await wrapper
            .CheckRequiredNodesAsync(Krea2WorkflowPatcher.RequiredCustomNodeTypes, ct)
            .ConfigureAwait(false);
        if (missingNodes.Count > 0)
        {
            _missingRequirements.Add(
                "The Krea 2 Turbo workload is not installed in the engine (missing: " +
                string.Join(", ", missingNodes) +
                "). Install the Krea 2 Turbo workload from the Diffusion Nexus Engine tile.");
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

        var seed = request.Seed ?? Random.Shared.NextInt64(0, int.MaxValue);
        var startedAt = Stopwatch.GetTimestamp();

        yield return new DiffusionStreamItem(new DiffusionProgress
        {
            Phase = DiffusionPhase.Loading,
            Message = "Submitting to the Diffusion Nexus Engine…"
        });

        using var wrapper = new ComfyUIWrapperService(_engine.BaseUrl!);

        // NOTE(progress): IComfyUIWrapperService.WaitForCompletionAsync reports raw WebSocket event
        // text ("Executing node 62...", "Progress: 3/8") via IProgress<string>, not a typed
        // step/total pair. Parsing that string to synthesize DiffusionPhase.Sampling items would be
        // guessing at a stable format the wrapper doesn't promise, so v1 leaves the stream at
        // Loading -> Completed for the engine path, same as the seam's other honesty rules.
        // TODO(v2-engine-progress): revisit if/when the wrapper exposes structured step progress.

        DiffusionResult? result = null;
        string? failure = null;
        try
        {
            var gguf = await ResolveInstalledKreaGgufAsync(wrapper, cancellationToken).ConfigureAwait(false);
            var templateJson = _templateSource!.LoadTemplateJson()
                ?? throw new InvalidOperationException("The Krea 2 workflow template could not be loaded.");
            var workflowJson = Krea2WorkflowPatcher.Patch(templateJson, request, seed, gguf);

            // QueueWorkflowAsync loads its workflow from a file path and applies per-node modifiers
            // itself (it's shared with the inpaint/outpaint/caption flows, which patch node-by-node).
            // Krea2WorkflowPatcher already produced the fully patched graph in memory, so hand it a
            // scratch file and no modifiers instead of re-deriving per-node patches here.
            var scratchWorkflowPath = Path.Combine(Path.GetTempPath(), $"dn-engine-krea2-{Guid.NewGuid():N}.json");
            string promptId;
            try
            {
                await File.WriteAllTextAsync(scratchWorkflowPath, workflowJson, cancellationToken).ConfigureAwait(false);
                promptId = await wrapper.QueueWorkflowAsync(
                    scratchWorkflowPath, new Dictionary<string, Action<JsonNode>>(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try { File.Delete(scratchWorkflowPath); }
                catch (IOException) { /* best-effort scratch cleanup */ }
                catch (UnauthorizedAccessException) { /* best-effort scratch cleanup */ }
            }

            await wrapper.WaitForCompletionAsync(promptId, ct: cancellationToken).ConfigureAwait(false);

            var comfyResult = await wrapper.GetResultAsync(promptId, cancellationToken).ConfigureAwait(false);
            var image = comfyResult.Images.FirstOrDefault();
            if (image is null)
            {
                failure = "The engine finished but returned no image.";
            }
            else
            {
                var bytes = await wrapper.DownloadImageAsync(image, cancellationToken).ConfigureAwait(false);
                result = new DiffusionResult(bytes, request.Width, request.Height, seed,
                    Stopwatch.GetElapsedTime(startedAt));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Engine generation failed.");
            failure = ex.Message;
        }

        yield return new DiffusionStreamItem(
            new DiffusionProgress { Phase = DiffusionPhase.Completed, Message = failure },
            result);
    }

    /// <summary>
    /// The Krea 2 GGUF present on this machine. The template names the Q8_0 quant, but the
    /// workload downloads whichever quant matches the card's VRAM tier, so submitting the
    /// template unchanged fails on every machine below the top tier. Returns null when the
    /// engine cannot be asked, in which case the template's own name is kept.
    /// </summary>
    /// <remarks>
    /// The folder key <c>"diffusion_models"</c> is ComfyUI's conventional UNet/DiT folder name but
    /// is not independently confirmed against a running engine here — flagged for the manual
    /// engine-generation smoke. If it's wrong, <c>GetModelsInFolderAsync</c> returns an empty list
    /// (logged, not thrown) and this falls back to the template's own Q8_0 name.
    /// </remarks>
    private static async Task<string?> ResolveInstalledKreaGgufAsync(
        ComfyUIWrapperService wrapper, CancellationToken ct)
    {
        try
        {
            var models = await wrapper.GetModelsInFolderAsync("diffusion_models", ct).ConfigureAwait(false);
            return models.FirstOrDefault(m =>
                m.Contains("krea2", StringComparison.OrdinalIgnoreCase) &&
                m.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Could not resolve the installed Krea 2 GGUF; keeping the template's name.");
            return null;
        }
    }
}
