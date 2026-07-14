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

        // SamplerCustom / SamplerCustomAdvanced keep sampler_name/scheduler/steps on separate
        // KSamplerSelect / BasicScheduler nodes wired into the sampler/sigmas link inputs — follow them
        // so these graphs don't come out with an empty sampler.
        samplerName ??= FollowLinkedString(graph, s, "sampler", "sampler_name");
        scheduler ??= FollowLinkedString(graph, s, "sigmas", "scheduler");
        steps ??= FollowLinkedInt(graph, s, "sigmas", "steps");

        // A "Sampler Selector"/KSamplerSelect wired into a plain KSampler's sampler_name input
        // (a link, not a literal) — follow it so the graph doesn't come out with an empty sampler.
        samplerName ??= FollowLinkedString(graph, s, "sampler_name", "sampler_name");

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
        if (samplerIds.Count == 1) return samplerIds[0]; // unambiguous — no need to trace a save node

        // Multi-sampler graph (e.g. several aspect-ratio branches sharing one model): anchor on the
        // node that saved/consumed the final image and BFS back to ITS sampler. Try the strongest
        // signal first — a core save class (SaveImage/PreviewImage) — then any node whose class name
        // looks like a saver (covers custom nodes such as AI2GoSaveCivitaiMetadata the core list
        // misses), then, as a last resort, any node consuming an image link.
        return FindSamplerViaImageConsumer(graph, cls => SaveClasses.Contains(cls))
            ?? FindSamplerViaImageConsumer(graph, IsSaveLikeName)
            ?? FindSamplerViaImageConsumer(graph, _ => true)
            // No image-consuming node traces to a sampler: best-effort first sampler rather than
            // leaving it unresolved (product decision 2026-07-13; differs from tracer.py).
            ?? samplerIds[0];
    }

    // Finds the first sampler reachable (backward) from the images input of a node whose class
    // satisfies <paramref name="classMatches"/>. Null when no such node traces to a sampler.
    private static string? FindSamplerViaImageConsumer(
        Dictionary<string, JsonElement> graph, Func<string, bool> classMatches)
    {
        foreach (var (_, node) in graph)
        {
            if (!node.TryGetProperty("class_type", out var c) || c.ValueKind != JsonValueKind.String) continue;
            if (!classMatches(c.GetString()!)) continue;
            if (!node.TryGetProperty("inputs", out var ins)) continue;
            if (!ins.TryGetProperty("images", out var imgs) || !IsLink(imgs)) continue;
            var found = BfsBackward(graph, OriginId(imgs), cls => SamplerClasses.Contains(cls));
            if (found is not null) return found;
        }
        return null;
    }

    private static bool IsSaveLikeName(string cls) =>
        cls.Contains("Save", StringComparison.OrdinalIgnoreCase) ||
        cls.Contains("Preview", StringComparison.OrdinalIgnoreCase) ||
        cls.Contains("Output", StringComparison.OrdinalIgnoreCase);

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

        if (IsLink(text) && TryNode(graph, OriginId(text), out var originCls, out var src))
        {
            // AI2Go Prompt Batch: the CLIPTextEncode's text link carries the output SLOT
            // (0 = positive, 1 = negative). Parse prompts_json + index and pick by slot, rather than
            // grabbing the first string input (which would be the whole prompts_json blob, identical
            // for positive and negative).
            if (originCls.Equals("AI2GoPromptBatch", StringComparison.OrdinalIgnoreCase))
                return ResolveBatchPrompt(src, LinkSlot(text));

            foreach (var prop in src.EnumerateObject())
                if (prop.Value.ValueKind == JsonValueKind.String)
                    return prop.Value.GetString();
        }
        return null;
    }

    // ── AI2Go Prompt Batch resolution ───────────────────────────────────────────
    // Port of prompt_batch_core.parse_prompts/select_prompt: the node holds a JSON array of prompts
    // (strings or {positive,negative} objects) plus an index; outputs are [positive(0), negative(1)].
    private static string? ResolveBatchPrompt(JsonElement batchInputs, int slot)
    {
        var promptsJson = ReadString(batchInputs, "prompts_json");
        if (promptsJson is null) return null;
        var index = ReadInt(batchInputs, "index") ?? 0;
        var selected = SelectBatchPrompt(promptsJson, index);
        if (selected is null) return null;
        return slot == 1 ? selected.Value.Negative : selected.Value.Positive;
    }

    private static (string Positive, string Negative)? SelectBatchPrompt(string promptsJson, int index)
    {
        try
        {
            using var doc = JsonDocument.Parse(promptsJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("prompts", out var wrapped) && wrapped.ValueKind == JsonValueKind.Array)
                root = wrapped;
            if (root.ValueKind != JsonValueKind.Array) return null;

            var list = new List<(string Positive, string Negative)>();
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    list.Add((entry.GetString() ?? "", ""));
                }
                else if (entry.ValueKind == JsonValueKind.Object)
                {
                    var pos = entry.TryGetProperty("positive", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()
                            : entry.TryGetProperty("prompt", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString()
                            : "";
                    var neg = entry.TryGetProperty("negative", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : "";
                    list.Add((pos ?? "", neg ?? ""));
                }
            }
            if (list.Count == 0) return null;
            var idx = Math.Clamp(index, 0, list.Count - 1);
            return list[idx];
        }
        catch
        {
            return null; // malformed JSON: treat as unresolved rather than guessing
        }
    }

    // ── SamplerCustom follow-through (KSamplerSelect / BasicScheduler) ───────────
    private static string? FollowLinkedString(Dictionary<string, JsonElement> graph, JsonElement node, string linkKey, string fieldKey)
    {
        var v = FollowLinkedField(graph, node, linkKey, fieldKey);
        return v is { ValueKind: JsonValueKind.String } ? v.Value.GetString() : null;
    }

    private static int? FollowLinkedInt(Dictionary<string, JsonElement> graph, JsonElement node, string linkKey, string fieldKey)
    {
        var v = FollowLinkedField(graph, node, linkKey, fieldKey);
        return v is { ValueKind: JsonValueKind.Number } && v.Value.TryGetInt32(out var i) ? i : null;
    }

    // Follows node[linkKey] (a link) backward to the nearest node carrying a literal fieldKey.
    private static JsonElement? FollowLinkedField(Dictionary<string, JsonElement> graph, JsonElement node, string linkKey, string fieldKey)
    {
        if (!node.TryGetProperty(linkKey, out var link) || !IsLink(link)) return null;

        var seen = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(OriginId(link));
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (!TryNode(graph, id, out _, out var ins)) continue;
            if (ins.TryGetProperty(fieldKey, out var val) && !IsLink(val)) return val;
            foreach (var prop in ins.EnumerateObject())
                if (IsLink(prop.Value)) queue.Enqueue(OriginId(prop.Value));
        }
        return null;
    }

    private static int LinkSlot(JsonElement link) => link[1].TryGetInt32(out var i) ? i : 0;

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
