using System.Text.Json;
using System.Text.Json.Nodes;
using DiffusionNexus.Inference.Abstractions;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Writes a canvas request into the shipped API-format Krea 2 text2image workflow
/// (<c>Assets/Pipelines/Krea2-Text2Image-API.json</c>).
///
/// Node ids are constants because the template is an app asset we control — the same approach
/// the inpaint/outpaint flows already take. A missing node throws instead of silently producing
/// an image that ignores the user's prompt or size.
/// </summary>
public static class Krea2WorkflowPatcher
{
    /// <summary>Positive prompt (KSampler.positive points here).</summary>
    private const string PositivePromptNodeId = "17";

    /// <summary>Negative prompt.</summary>
    private const string NegativePromptNodeId = "35";

    /// <summary>Empty latent. Its width/height ship as links to the AI2Go resolution selector.</summary>
    private const string LatentNodeId = "36";

    /// <summary>Sampler: seed, steps, cfg.</summary>
    private const string SamplerNodeId = "37";

    /// <summary>GGUF UNet loader (calcuis/gguf). Its quant is machine-specific.</summary>
    private const string GgufLoaderNodeId = "62";

    /// <summary>SaveImage — ships with a hardcoded dated prefix.</summary>
    private const string SaveImageNodeId = "21";

    /// <summary>VAEDecode. Read only for its <c>vae</c> link, which the injected encoder reuses.</summary>
    private const string VaeDecodeNodeId = "9";

    /// <summary>
    /// Injected <c>LoadImage</c> holding the canvas region. Ids are in a high range the shipped template
    /// does not use, so the patch cannot collide with a template node.
    /// </summary>
    private const string InjectedLoadImageNodeId = "9001";

    /// <summary>Injected <c>VAEEncode</c> turning that region into the sampler's starting latent.</summary>
    private const string InjectedVaeEncodeNodeId = "9002";

    /// <summary>Output prefix used for canvas generations inside the engine's output folder.</summary>
    private const string CanvasFilenamePrefix = "DiffusionNexus/Canvas";

    /// <summary>
    /// Custom node class types this template requires that the base engine install deliberately
    /// excludes (see <c>ManagedEngineInstaller</c>) — only the Krea 2 Turbo workload installs
    /// them. Kept next to the node-id constants above so a future template edit updates both
    /// together instead of drifting apart. Consumed by
    /// <see cref="ManagedComfyUiBackend"/>'s readiness check via
    /// <c>IComfyUIWrapperService.CheckRequiredNodesAsync</c> to catch the "installed the engine,
    /// selected it, pressed Generate" sequence before it fails with a raw ComfyUI error: without
    /// this check, that is the most likely first-run path for a user who has not yet installed
    /// the workload.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredCustomNodeTypes =
    [
        "LoaderGGUF",                   // node 62 — calcuis/gguf UNet loader
        "Power Lora Loader (rgthree)",  // node 55 — rgthree-comfy
        "AI2GoResolutionSelector",      // node 65 — AI2Go custom node
    ];

    /// <param name="templateJson">The unpatched API-format workflow JSON.</param>
    /// <param name="request">The canvas generation request supplying prompt, size, and overrides.</param>
    /// <param name="seed">The seed to submit (the caller resolves random-vs-fixed, not this patcher).</param>
    /// <param name="ggufFileName">
    /// The Krea 2 GGUF actually present on this machine, or null to keep whatever the template
    /// names. The template ships the Q8_0 quant, which only exists on a 32 GB-tier install.
    /// </param>
    /// <param name="initImageFileName">
    /// The name ComfyUI stored the uploaded canvas region under, or null for a plain text2image run.
    /// When supplied the graph is rewired into an image-to-image pipeline — see <see cref="Patch"/>'s
    /// remarks.
    /// </param>
    /// <remarks>
    /// <b>Image to image.</b> Passing <paramref name="initImageFileName"/> injects a <c>LoadImage</c> and
    /// a <c>VAEEncode</c> and repoints the sampler's <c>latent_image</c> at the encoded region, with
    /// <c>denoise</c> taken from the request's init-image strength. Both injected nodes are core ComfyUI
    /// types, so this adds nothing to <see cref="RequiredCustomNodeTypes"/> and needs no second template
    /// asset. The encoder reuses whatever VAE the template's <c>VAEDecode</c> already points at rather
    /// than naming a loader node, so re-wiring the template's VAE does not silently break this path.
    /// <c>EmptySD3LatentImage</c> then becomes unreachable and ComfyUI never executes it — the same trick
    /// the size patch above uses to strand the resolution selector.
    /// </remarks>
    public static string Patch(
        string templateJson,
        DiffusionRequest request,
        long seed,
        string? ggufFileName,
        string? initImageFileName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templateJson);
        ArgumentNullException.ThrowIfNull(request);

        var graph = JsonNode.Parse(templateJson)?.AsObject()
            ?? throw new InvalidOperationException("The workflow template is not a JSON object.");

        Inputs(graph, PositivePromptNodeId)["text"] = request.Prompt;
        Inputs(graph, NegativePromptNodeId)["text"] = request.NegativePrompt ?? string.Empty;

        // The template drives the latent size from an AI2GoResolutionSelector link. The canvas
        // owns the frame size, so replace the links with literals; node 65 then becomes
        // unreachable and ComfyUI never executes it.
        var latent = Inputs(graph, LatentNodeId);
        latent["width"] = request.Width;
        latent["height"] = request.Height;

        var sampler = Inputs(graph, SamplerNodeId);
        sampler["seed"] = seed;
        if (request.Steps is { } steps) sampler["steps"] = steps;
        if (request.Cfg is { } cfg) sampler["cfg"] = cfg;

        if (!string.IsNullOrWhiteSpace(ggufFileName))
            Inputs(graph, GgufLoaderNodeId)["gguf_name"] = ggufFileName;

        Inputs(graph, SaveImageNodeId)["filename_prefix"] = CanvasFilenamePrefix;

        if (!string.IsNullOrWhiteSpace(initImageFileName))
            ApplyImageToImage(graph, sampler, initImageFileName, request.InitImage?.Strength ?? 1.0f);

        return graph.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    /// <summary>
    /// Rewires the graph from "sample an empty latent" to "sample the encoded canvas region".
    /// </summary>
    private static void ApplyImageToImage(
        JsonObject graph, JsonObject sampler, string initImageFileName, float strength)
    {
        if (graph[InjectedLoadImageNodeId] is not null || graph[InjectedVaeEncodeNodeId] is not null)
            throw new InvalidOperationException(
                $"The workflow template already defines node '{InjectedLoadImageNodeId}' or " +
                $"'{InjectedVaeEncodeNodeId}'. The asset and the patcher are out of sync.");

        // Follow the template's own VAE wiring instead of naming a loader node, so a template that
        // swaps its VAE source keeps working.
        var vaeLink = Inputs(graph, VaeDecodeNodeId)["vae"]?.DeepClone()
            ?? throw new InvalidOperationException(
                $"Workflow node '{VaeDecodeNodeId}' has no 'vae' input to borrow for image-to-image.");

        graph[InjectedLoadImageNodeId] = new JsonObject
        {
            ["class_type"] = "LoadImage",
            ["inputs"] = new JsonObject
            {
                ["image"] = initImageFileName,
                ["upload"] = "image",
            },
        };

        graph[InjectedVaeEncodeNodeId] = new JsonObject
        {
            ["class_type"] = "VAEEncode",
            ["inputs"] = new JsonObject
            {
                ["pixels"] = new JsonArray(InjectedLoadImageNodeId, 0),
                ["vae"] = vaeLink,
            },
        };

        sampler["latent_image"] = new JsonArray(InjectedVaeEncodeNodeId, 0);
        sampler["denoise"] = Math.Clamp(strength, 0f, 1f);
    }

    private static JsonObject Inputs(JsonObject graph, string nodeId)
    {
        if (graph[nodeId] is not JsonObject node)
            throw new InvalidOperationException(
                $"The Krea 2 workflow template has no node '{nodeId}'. The asset and the patcher are out of sync.");

        if (node["inputs"] is not JsonObject inputs)
            throw new InvalidOperationException($"Workflow node '{nodeId}' has no inputs object.");

        return inputs;
    }
}
