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

    /// <summary>The description of an image already uploaded to the server, or null when the node returned none.</summary>
    public async Task<string?> DescribeAsync(string uploadedImage, bool keepLoaded, CancellationToken ct)
    {
        var promptId = await _client.QueueWorkflowAsync(_workflowPath, Overrides(uploadedImage, keepLoaded ? "keep_vram" : "direct_clean"), ct);
        await _client.WaitForCompletionAsync(promptId, progress: null, ct);
        var result = await _client.GetResultAsync(promptId, ct);
        return result.Texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();
    }

    /// <summary>
    /// Frees a model a batch kept loaded: one <c>direct_clean</c> job with the same config reuses it and
    /// unloads it after answering. Runs to the end even when the batch was cancelled, and never throws.
    /// </summary>
    public async Task FreeAsync(string uploadedImage)
    {
        try
        {
            var promptId = await _client.QueueWorkflowAsync(_workflowPath, Overrides(uploadedImage, "direct_clean"), CancellationToken.None);
            await _client.WaitForCompletionAsync(promptId, progress: null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not free Qwen3-VL on the ComfyUI server");
        }
    }

    private Dictionary<string, Action<JsonNode>> Overrides(string uploadedImage, string mode)
    {
        var seed = QwenVlGguf.Seed((long)(_random.NextDouble() * long.MaxValue));
        return new Dictionary<string, Action<JsonNode>>
        {
            [LoadImageNodeId] = node => node["inputs"]!["image"] = uploadedImage,
            [DescribeNodeId] = node =>
            {
                node["inputs"]!["mode"] = mode;
                node["inputs"]!["seed"] = seed;
                node["inputs"]!["config_override"] = _config;
            },
        };
    }
}
