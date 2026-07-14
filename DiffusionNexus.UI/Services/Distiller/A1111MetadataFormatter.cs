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
    // ComfyUI sampler_name -> A1111 base label. Ported from the AI2Go civitai_metadata sampler_names.py
    // so CivitAI recognizes the sampler. Unmapped names title-case as a fallback.
    private static readonly Dictionary<string, string> SamplerLabels = new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["euler"] = "Euler",
        ["euler_cfg_pp"] = "Euler",
        ["euler_ancestral"] = "Euler a",
        ["euler_ancestral_cfg_pp"] = "Euler a",
        ["heun"] = "Heun",
        ["heunpp2"] = "Heun",
        ["dpm_2"] = "DPM2",
        ["dpm_2_ancestral"] = "DPM2 a",
        ["lms"] = "LMS",
        ["dpm_fast"] = "DPM fast",
        ["dpm_adaptive"] = "DPM adaptive",
        ["dpmpp_2s_ancestral"] = "DPM++ 2S a",
        ["dpmpp_sde"] = "DPM++ SDE",
        ["dpmpp_sde_gpu"] = "DPM++ SDE",
        ["dpmpp_2m"] = "DPM++ 2M",
        ["dpmpp_2m_sde"] = "DPM++ 2M SDE",
        ["dpmpp_2m_sde_gpu"] = "DPM++ 2M SDE",
        ["dpmpp_3m_sde"] = "DPM++ 3M SDE",
        ["dpmpp_3m_sde_gpu"] = "DPM++ 3M SDE",
        ["ddim"] = "DDIM",
        ["uni_pc"] = "UniPC",
        ["uni_pc_bh2"] = "UniPC",
        ["lcm"] = "LCM",
    };

    // Schedulers A1111/CivitAI treats as the plain sampler (no suffix).
    private static readonly HashSet<string> PlainSchedulers = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "normal", "simple", "sgm_uniform", "ddim_uniform", "beta",
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

        // Order + fields mirror the AI2Go a1111.py format that CivitAI parses: the scheduler is folded
        // into the combined Sampler name (no separate "Schedule type" field), and a trailing Version tag.
        var settings = new List<string>();
        if (data.Steps is { } steps) settings.Add($"Steps: {steps}");
        if (!string.IsNullOrWhiteSpace(data.SamplerName))
            settings.Add($"Sampler: {ToA1111Sampler(data.SamplerName, data.Scheduler)}");
        if (data.Cfg is { } cfg) settings.Add($"CFG scale: {cfg.ToString("0.###", CultureInfo.InvariantCulture)}");
        if (data.Seed is { } seed) settings.Add($"Seed: {seed}");
        if (data.Width > 0 && data.Height > 0) settings.Add($"Size: {data.Width}x{data.Height}");

        if (hashes is { ModelHash: { Length: > 0 } modelHash }) settings.Add($"Model hash: {modelHash}");
        if (!string.IsNullOrWhiteSpace(data.Checkpoint)) settings.Add($"Model: {data.Checkpoint}");
        if (hashes is { } h) settings.Add($"Hashes: {BuildHashesJson(h)}");

        settings.Add("Version: ComfyUI");

        sb.Append('\n').Append(string.Join(", ", settings));
        return sb.ToString();
    }

    /// <summary>
    /// Maps a ComfyUI (sampler_name, scheduler) pair to the combined A1111 sampler label CivitAI
    /// recognizes (e.g. dpmpp_2m + karras -> "DPM++ 2M Karras"). Port of AI2Go's to_a1111_sampler.
    /// </summary>
    private static string ToA1111Sampler(string? samplerName, string? scheduler)
    {
        string @base = !string.IsNullOrEmpty(samplerName) && SamplerLabels.TryGetValue(samplerName, out var label)
            ? label
            : !string.IsNullOrEmpty(samplerName)
                ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(samplerName.Replace("_", " ").ToLowerInvariant())
                : "Unknown";

        if (string.Equals(scheduler, "karras", System.StringComparison.OrdinalIgnoreCase)) return $"{@base} Karras";
        if (string.Equals(scheduler, "exponential", System.StringComparison.OrdinalIgnoreCase)) return $"{@base} Exponential";
        if (string.IsNullOrEmpty(scheduler) || PlainSchedulers.Contains(scheduler)) return @base;
        return $"{@base} {scheduler}"; // unknown scheduler: append raw, lose nothing
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
