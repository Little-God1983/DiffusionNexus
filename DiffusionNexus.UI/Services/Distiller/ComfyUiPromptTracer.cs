using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiffusionNexus.UI.Models;

namespace DiffusionNexus.UI.Services.Distiller;

/// <summary>
/// Recovers generation parameters from a ComfyUI "prompt"-chunk graph (the resolved API prompt,
/// NOT the editor "workflow" graph). Faithful C# port of
/// ComfyUI-AI2Go-Utils/nodes/civitai_metadata/tracer.py; see comfyui-metadata-extraction-for-csharp.md.
/// Using the API prompt is what makes rgthree Power Lora / Lora Stack detection tractable.
/// </summary>
internal static class ComfyUiPromptTracer
{
    private static readonly HashSet<string> SamplerClasses = new(StringComparer.OrdinalIgnoreCase)
    { "KSampler", "KSamplerAdvanced", "SamplerCustom", "SamplerCustomAdvanced" };

    private static readonly HashSet<string> SaveClasses = new(StringComparer.OrdinalIgnoreCase)
    { "SaveImage", "PreviewImage", "SaveImageWebsocket" };

    // class_type -> (widget name holding the file, models subfolder)
    private static readonly Dictionary<string, (string Widget, string Folder)> ModelSources =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["CheckpointLoaderSimple"] = ("ckpt_name", "checkpoints"),
        ["CheckpointLoader"]       = ("ckpt_name", "checkpoints"),
        ["unCLIPCheckpointLoader"] = ("ckpt_name", "checkpoints"),
        ["UNETLoader"]             = ("unet_name", "diffusion_models"),
        ["UnetLoaderGGUF"]         = ("unet_name", "diffusion_models"),
        ["UnetLoaderGGUFAdvanced"] = ("unet_name", "diffusion_models"),
    };

    public static ImageGenerationData Trace(
        Dictionary<string, JsonElement> graph, string? fileName, int width, int height)
    {
        var startId = PickSamplerId(graph);
        if (startId is null || !TryNode(graph, startId, out _, out var s))
            return new ImageGenerationData { FileName = fileName, Width = width, Height = height, HasData = false };

        int? steps = ReadInt(s, "steps");
        double? cfg = ReadDouble(s, "cfg");
        long? seed = ReadLong(s, "seed") ?? ReadLong(s, "noise_seed");
        string? samplerName = ReadString(s, "sampler_name");
        string? scheduler = ReadString(s, "scheduler");
        double? denoise = ReadDouble(s, "denoise");

        string? positive = ResolvePrompt(graph, s, "positive");
        string? negative = ResolvePrompt(graph, s, "negative");

        var loras = new List<LoraInfo>();
        string? checkpoint = WalkModelChain(graph, s, loras);
        loras.Reverse(); // collected sampler-first; load order is checkpoint-first

        var hasData = positive is not null || negative is not null || checkpoint is not null
                      || samplerName is not null || loras.Count > 0;

        return new ImageGenerationData
        {
            FileName = fileName, Width = width, Height = height,
            PositivePrompt = positive, NegativePrompt = negative, Checkpoint = checkpoint,
            Loras = loras, SamplerName = samplerName, Scheduler = scheduler,
            Steps = steps, Seed = seed, Cfg = cfg, Denoise = denoise, HasData = hasData,
        };
    }

    // ── sampler selection (§3.1) ────────────────────────────────────────────────
    private static string? PickSamplerId(Dictionary<string, JsonElement> graph)
    {
        var samplerIds = graph.Where(kv => IsClass(kv.Value, SamplerClasses)).Select(kv => kv.Key).ToList();
        if (samplerIds.Count == 0) return null;

        // Prefer the sampler reachable from a save node's images input.
        foreach (var (_, node) in graph)
        {
            if (!IsClass(node, SaveClasses)) continue;
            if (!node.TryGetProperty("inputs", out var ins)) continue;
            if (!ins.TryGetProperty("images", out var imgs) || !IsLink(imgs)) continue;
            var found = BfsBackward(graph, OriginId(imgs), cls => SamplerClasses.Contains(cls));
            if (found is not null) return found;
        }

        // Single-sampler graphs resolve here. For multi-sampler graphs with no BFS-traceable save
        // node we deliberately return a best-effort first sampler rather than leaving it unresolved
        // (product decision 2026-07-13). This differs from tracer.py, which returns unresolved; the
        // trade-off is accepted because the common hires/refiner topology links SaveImage to the
        // final sampler and is resolved by the BFS above.
        return samplerIds[0];
    }

    // BFS over input links; checks the start node itself first.
    private static string? BfsBackward(Dictionary<string, JsonElement> graph, string startId, Func<string, bool> match)
    {
        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(startId);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (!TryNode(graph, id, out var cls, out var ins)) continue;
            if (match(cls)) return id;
            foreach (var prop in ins.EnumerateObject())
                if (IsLink(prop.Value)) queue.Enqueue(OriginId(prop.Value));
        }
        return null;
    }

    // ── prompts (§3.3) ──────────────────────────────────────────────────────────
    private static string? ResolvePrompt(Dictionary<string, JsonElement> graph, JsonElement sampler, string key)
    {
        if (!sampler.TryGetProperty(key, out var v) || !IsLink(v)) return null;

        var encId = BfsBackward(graph, OriginId(v),
            cls => cls.Contains("CLIPTextEncode", StringComparison.OrdinalIgnoreCase));
        if (encId is null || !TryNode(graph, encId, out _, out var enc)) return null;

        if (!enc.TryGetProperty("text", out var text)) return null;
        if (text.ValueKind == JsonValueKind.String) return text.GetString();

        if (IsLink(text) && TryNode(graph, OriginId(text), out _, out var src))
        {
            foreach (var prop in src.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
        }
        return null;
    }

    // ── model chain + LoRAs (§3.4–3.5) ──────────────────────────────────────────
    private static string? WalkModelChain(Dictionary<string, JsonElement> graph, JsonElement sampler, List<LoraInfo> loras)
    {
        if (!sampler.TryGetProperty("model", out var link) || !IsLink(link)) return null;

        string? current = OriginId(link);
        var seen = new HashSet<string>();
        while (current is not null && seen.Add(current))
        {
            if (!TryNode(graph, current, out var cls, out var ins)) break;

            if (cls.Equals("LoraLoader", StringComparison.OrdinalIgnoreCase) ||
                cls.Equals("LoraLoaderModelOnly", StringComparison.OrdinalIgnoreCase))
            {
                var name = ReadString(ins, "lora_name");
                if (name is { Length: > 0 } && name != "None")
                {
                    var str = ReadDouble(ins, "strength_model") ?? ReadDouble(ins, "strength") ?? 1.0;
                    loras.Add(new LoraInfo { Name = Stem(name), StrengthModel = str, StrengthClip = str, Source = "LoraLoader" });
                }
                current = NextModel(ins); continue;
            }

            if (cls.Equals("Power Lora Loader (rgthree)", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var e in Enumerable.Reverse(PowerLoraEntries(ins))) loras.Add(e);
                current = NextModel(ins); continue;
            }

            if (cls.Equals("Lora Loader Stack (rgthree)", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var e in Enumerable.Reverse(LoraStackEntries(ins))) loras.Add(e);
                current = NextModel(ins); continue;
            }

            if (ModelSources.TryGetValue(cls, out var srcDef))
            {
                var name = ReadString(ins, srcDef.Widget);
                return name is { Length: > 0 } ? Stem(name) : null;
            }

            current = NextModel(ins); // unknown pass-through node
        }
        return null;
    }

    private static string? NextModel(JsonElement ins) =>
        ins.TryGetProperty("model", out var m) && IsLink(m) ? OriginId(m) : null;

    private static List<LoraInfo> PowerLoraEntries(JsonElement ins)
    {
        var list = new List<LoraInfo>();
        foreach (var prop in ins.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Name.StartsWith("lora_", StringComparison.OrdinalIgnoreCase)) continue;

            var obj = prop.Value;
            if (obj.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.False) continue;

            var name = obj.TryGetProperty("lora", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            if (string.IsNullOrEmpty(name) || name == "None") continue;

            double? str = obj.TryGetProperty("strength", out var srE) && srE.ValueKind == JsonValueKind.Number ? srE.GetDouble() : null;
            list.Add(new LoraInfo { Name = Stem(name), StrengthModel = str ?? 1.0, StrengthClip = str ?? 1.0, Source = "Power Lora" });
        }
        return list;
    }

    private static List<LoraInfo> LoraStackEntries(JsonElement ins)
    {
        var nums = new List<string>();
        foreach (var prop in ins.EnumerateObject())
            if (prop.Name.StartsWith("lora_", StringComparison.OrdinalIgnoreCase)
                && prop.Name.Length > 5 && prop.Name[5..].All(char.IsDigit))
                nums.Add(prop.Name[5..]);

        var list = new List<LoraInfo>();
        foreach (var num in nums.OrderBy(int.Parse))
        {
            var name = ReadString(ins, "lora_" + num);
            if (string.IsNullOrEmpty(name) || name == "None") continue;

            double? str = ReadDouble(ins, "strength_" + num);
            if (str is 0) continue; // zero strength: rgthree won't load it

            list.Add(new LoraInfo { Name = Stem(name), StrengthModel = str ?? 1.0, StrengthClip = str ?? 1.0, Source = "Lora Stack" });
        }
        return list;
    }

    // ── low-level helpers (§2) ──────────────────────────────────────────────────
    private static bool IsLink(JsonElement v) =>
        v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 2 && v[1].ValueKind == JsonValueKind.Number;

    private static string OriginId(JsonElement link) => link[0].ToString();

    private static bool IsClass(JsonElement node, HashSet<string> set) =>
        node.TryGetProperty("class_type", out var c) && c.ValueKind == JsonValueKind.String && set.Contains(c.GetString()!);

    private static bool TryNode(Dictionary<string, JsonElement> graph, string id, out string classType, out JsonElement inputs)
    {
        classType = ""; inputs = default;
        if (!graph.TryGetValue(id, out var node)) return false;
        if (!node.TryGetProperty("class_type", out var c) || c.ValueKind != JsonValueKind.String) return false;
        classType = c.GetString() ?? "";
        return node.TryGetProperty("inputs", out inputs);
    }

    private static string? ReadString(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? ReadInt(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? ReadLong(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static double? ReadDouble(JsonElement ins, string key) =>
        ins.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static string Stem(string name)
    {
        var slash = name.LastIndexOfAny(['/', '\\']);
        if (slash >= 0) name = name[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
