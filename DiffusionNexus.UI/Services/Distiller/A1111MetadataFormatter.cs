using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>AutoV2 hashes for the checkpoint and LoRAs, keyed by LoRA stem.</summary>
public readonly record struct ResourceHashes(string? ModelHash, IReadOnlyDictionary<string, string> LoraHashes);

/// <summary>
/// Formats an <see cref="ImageGenerationData"/> trace as an Automatic1111 <c>parameters</c> string
/// (the format CivitAI's image parser reads). See comfyui-metadata-extraction-for-csharp.md §4.2.
/// </summary>
internal static class A1111MetadataFormatter
{
    // ComfyUI (sampler_name, scheduler) -> A1111 combined sampler name. Unmapped names pass through.
    private static readonly Dictionary<string, string> SamplerMap = new()
    {
        ["euler|normal"] = "Euler",
        ["euler|karras"] = "Euler Karras",
        ["euler_ancestral|normal"] = "Euler a",
        ["dpmpp_2m|normal"] = "DPM++ 2M",
        ["dpmpp_2m|karras"] = "DPM++ 2M Karras",
        ["dpmpp_2m_sde|karras"] = "DPM++ 2M SDE Karras",
        ["dpmpp_sde|karras"] = "DPM++ SDE Karras",
        ["ddim|normal"] = "DDIM",
    };

    public static string Build(
        ImageGenerationData data,
        string positive,
        string? negative,
        IReadOnlyList<LoraInfo> loras,
        ResourceHashes? hashes)
    {
        var sb = new StringBuilder();

        sb.Append(positive?.TrimEnd() ?? string.Empty);
        foreach (var lora in loras)
        {
            var strength = lora.StrengthModel.ToString("0.###", CultureInfo.InvariantCulture);
            sb.Append(" <lora:").Append(lora.Name).Append(':').Append(strength).Append('>');
        }

        if (!string.IsNullOrWhiteSpace(negative))
            sb.Append("\nNegative prompt: ").Append(negative.Trim());

        var settings = new List<string>();
        if (data.Steps is { } steps) settings.Add($"Steps: {steps}");
        settings.Add($"Sampler: {MapSampler(data.SamplerName, data.Scheduler)}");
        if (!string.IsNullOrWhiteSpace(data.Scheduler)) settings.Add($"Schedule type: {data.Scheduler}");
        if (data.Cfg is { } cfg) settings.Add($"CFG scale: {cfg.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (data.Seed is { } seed) settings.Add($"Seed: {seed}");
        if (data.Width > 0 && data.Height > 0) settings.Add($"Size: {data.Width}x{data.Height}");
        if (!string.IsNullOrWhiteSpace(data.Checkpoint)) settings.Add($"Model: {data.Checkpoint}");

        if (hashes is { } h)
        {
            if (!string.IsNullOrEmpty(h.ModelHash)) settings.Add($"Model hash: {h.ModelHash}");
            settings.Add($"Hashes: {BuildHashesJson(h)}");
        }

        sb.Append('\n').Append(string.Join(", ", settings));
        return sb.ToString();
    }

    private static string MapSampler(string? samplerName, string? scheduler)
    {
        if (string.IsNullOrWhiteSpace(samplerName)) return "Euler";
        var key = $"{samplerName.ToLowerInvariant()}|{(scheduler ?? "normal").ToLowerInvariant()}";
        return SamplerMap.TryGetValue(key, out var mapped) ? mapped : samplerName;
    }

    private static string BuildHashesJson(ResourceHashes h)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(h.ModelHash)) parts.Add($"\"model\":\"{h.ModelHash}\"");
        foreach (var kv in h.LoraHashes.OrderBy(k => k.Key))
            parts.Add($"\"lora:{kv.Key}\":\"{kv.Value}\"");
        return "{" + string.Join(",", parts) + "}";
    }
}
