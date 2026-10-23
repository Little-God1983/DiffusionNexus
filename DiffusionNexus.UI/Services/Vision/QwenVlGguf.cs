using System.Text.Json;

namespace DiffusionNexus.UI.Services.Vision;

/// <summary>
/// Qwen3-VL through KLL535's <c>SimpleQwenVLggufV2</c> node (llama.cpp): the catalog model names whose
/// paths readiness reports, and the node's <c>config_override</c>. Shared by Outpaint Vision (#607) and
/// Batch Upscale Vision (#608).
/// </summary>
public static class QwenVlGguf
{
    /// <summary>Catalog name of the GGUF model (VRAM-tiered quantizations).</summary>
    public const string ModelName = "Qwen3-VL-8B-Abliterated-Caption-it";

    /// <summary>Catalog name of its vision projector.</summary>
    public const string ProjectorName = "Qwen3-VL-8B-Abliterated-Caption-it mmproj";

    /// <summary>Both file paths from a readiness check's <c>ModelPaths</c>; false when either is missing.</summary>
    public static bool TryGetPaths(IReadOnlyDictionary<string, string> modelPaths, out string modelPath, out string projectorPath)
    {
        modelPath = projectorPath = "";
        if (!modelPaths.TryGetValue(ModelName, out var model) || !modelPaths.TryGetValue(ProjectorName, out var projector))
            return false;
        (modelPath, projectorPath) = (model, projector);
        return true;
    }

    /// <summary>
    /// The node's config: model and projector paths, a bounded answer (a looping description once ran
    /// to 2048 tokens and became the prompt), low temperature.
    /// </summary>
    public static string BuildConfig(string modelPath, string projectorPath) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["model_path"] = modelPath,
            ["mmproj_path"] = projectorPath,
            ["chat_handler"] = "qwen3",
            ["ctx"] = 8192,
            ["output_max_tokens"] = 400,
            ["temperature"] = 0.3,
            ["repeat_penalty"] = 1.1,
        });

    /// <summary>The node's seed input tops out at 0xFFFFFFFF; a KSampler's 63-bit seed is rejected.</summary>
    public static long Seed(long seed) => seed & 0xFFFFFFFFL;
}
