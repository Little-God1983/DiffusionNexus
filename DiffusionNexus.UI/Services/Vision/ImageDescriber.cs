using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.UI.Services.Vision;

/// <summary>
/// Describes images with Qwen3-VL on a ComfyUI server through the describe-only workflow
/// (<c>Qwen3-VL-Describe.json</c>). A batch keeps the model loaded between images (<c>keep_vram</c>) and
/// frees it with the last one (<c>direct_clean</c>); <see cref="FreeAsync"/> frees it when a batch stops early.
/// </summary>
public sealed class ImageDescriber
{
    private static readonly ILogger Logger = Log.ForContext<ImageDescriber>();

    internal const string WorkflowRelativePath = "Assets/Workflows/Qwen3-VL-Describe.json";
    internal const string LoadImageNodeId = "1";
    internal const string DescribeNodeId = "3";

    /// <summary>The describe node's type, as a <see cref="ComfyUIExecutionException"/> names it.</summary>
    internal const string DescribeNodeType = "SimpleQwenVLggufV2";

    /// <summary>The workflow's nodes that run before Qwen3-VL: a failure there leaves the model as it was.</summary>
    internal static readonly IReadOnlySet<string> NodesBeforeTheDescriber = new HashSet<string> { "LoadImage", "ImageScaleToMaxDimension" };

    /// <summary>The workflow's nodes that run after Qwen3-VL: a failure there leaves what the node's mode left.</summary>
    internal static readonly IReadOnlySet<string> NodesAfterTheDescriber = new HashSet<string> { "ShowText|pysssss" };

    private readonly IComfyUIWrapperService _client;
    private readonly string _workflowPath;
    private readonly string _config;
    private readonly Random _random = new();

    public ImageDescriber(IComfyUIWrapperService client, string modelPath, string projectorPath, string? workflowPath = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _workflowPath = workflowPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, WorkflowRelativePath);
        _config = QwenVlGguf.BuildConfig(modelPath, projectorPath);
    }

    /// <summary>
    /// The description of an image already uploaded to the server, or null when the node returned none.
    /// <paramref name="freeComfyModels"/> asks the node to unload ComfyUI's own models first: llama.cpp
    /// allocates outside ComfyUI's memory manager, so ComfyUI would not make room for it.
    /// </summary>
    /// <exception cref="ImageDescriptionFailedException">The node reported a failed inference as its text.</exception>
    public async Task<string?> DescribeAsync(string uploadedImage, bool keepLoaded, CancellationToken ct, bool freeComfyModels = false)
    {
        var overrides = Overrides(uploadedImage, keepLoaded ? "keep_vram" : "direct_clean", freeComfyModels);
        var promptId = await _client.QueueWorkflowAsync(_workflowPath, overrides, ct);
        await _client.WaitForCompletionAsync(promptId, progress: null, ct);
        var result = await _client.GetResultAsync(promptId, ct);
        var text = result.Texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();

        // The node catches its own inference errors (a model that will not load, CUDA out of memory) and
        // returns them as its text, after unloading everything.
        if (text is not null && text.StartsWith(InferenceFailedPrefix, StringComparison.Ordinal))
            throw new ImageDescriptionFailedException(text[InferenceFailedPrefix.Length..]
                .Replace("Check console for details.", "").Trim());
        return text;
    }

    private const string InferenceFailedPrefix = "❌ Inference failed:";

    /// <summary>How long <see cref="FreeAsync"/> waits for its job: it runs after Cancel, so nothing else can stop it.</summary>
    public TimeSpan FreeTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Frees a model a batch kept loaded: one <c>direct_clean</c> job with the same config reuses it and
    /// unloads it after answering. Runs even when the batch was cancelled, gives up after
    /// <see cref="FreeTimeout"/>, and never throws.
    /// </summary>
    public async Task FreeAsync(string uploadedImage)
    {
        using var timeout = new CancellationTokenSource(FreeTimeout);
        try
        {
            var promptId = await _client.QueueWorkflowAsync(_workflowPath, Overrides(uploadedImage, "direct_clean", freeComfyModels: false), timeout.Token);
            await _client.WaitForCompletionAsync(promptId, progress: null, timeout.Token);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not free Qwen3-VL on the ComfyUI server");
        }
    }

    private Dictionary<string, Action<JsonNode>> Overrides(string uploadedImage, string mode, bool freeComfyModels)
    {
        var seed = QwenVlGguf.Seed((long)(_random.NextDouble() * long.MaxValue));
        return new Dictionary<string, Action<JsonNode>>
        {
            [LoadImageNodeId] = node => node["inputs"]!["image"] = uploadedImage,
            [DescribeNodeId] = node =>
            {
                node["inputs"]!["mode"] = mode;
                node["inputs"]!["unload_all_models"] = freeComfyModels;
                node["inputs"]!["seed"] = seed;
                node["inputs"]!["config_override"] = _config;
            },
        };
    }
}

/// <summary>Qwen3-VL could not describe an image; the message is the node's reason.</summary>
public sealed class ImageDescriptionFailedException(string reason) : Exception(reason);
