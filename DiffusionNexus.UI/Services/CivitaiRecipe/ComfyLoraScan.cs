using System.Text.Json;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// What <see cref="ComfyLoraScan"/> found in a graph. <paramref name="NegativeZeroedOut"/>: every
/// sampler's negative is a ConditioningZeroOut, so the image has no negative prompt (#670).
/// </summary>
internal sealed record ComfyGraphFacts(IReadOnlyList<RecipeLoraUse> Loras, string? Checkpoint, string? Vae, bool NegativeZeroedOut = false);

/// <summary>
/// Collects LoRAs with their weights from every loader node of a ComfyUI "prompt"-chunk graph
/// (#628). This graph is the executed one, so muted and bypassed nodes are already absent. Unlike
/// <see cref="Distiller.ComfyUiPromptTracer"/> it does not start from a sampler: the example image
/// samples with RES4LYF's ClownsharKSampler, which the shared tracer does not know, and the
/// tracer stays as it is for the Distiller and Batch Upscale.
/// </summary>
internal static class ComfyLoraScan
{
    private static readonly HashSet<string> CoreLoraLoaders = new(StringComparer.OrdinalIgnoreCase)
    { "LoraLoader", "LoraLoaderModelOnly" };

    // class_type -> widget holding the model file
    private static readonly Dictionary<string, string> ModelLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CheckpointLoaderSimple"] = "ckpt_name",
        ["CheckpointLoader"] = "ckpt_name",
        ["UNETLoader"] = "unet_name",
        ["UnetLoaderGGUF"] = "unet_name",
    };

    public static ComfyGraphFacts Scan(IReadOnlyDictionary<string, JsonElement> graph)
    {
        var loras = new List<RecipeLoraUse>();
        var models = new List<string>();
        var vaes = new List<string>();
        var classes = new Dictionary<string, string>();
        var negatives = new List<string>();

        // Node ids are numbers as strings; going in id order keeps the list in the graph's build order.
        foreach (var (nodeId, node) in graph.OrderBy(kv => int.TryParse(kv.Key, out var id) ? id : int.MaxValue))
        {
            if (node.ValueKind != JsonValueKind.Object
                || !node.TryGetProperty("class_type", out var classElement)
                || classElement.ValueKind != JsonValueKind.String
                || !node.TryGetProperty("inputs", out var inputs)
                || inputs.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var classType = classElement.GetString()!;
            classes[nodeId] = classType;
            // Any sampler, core or custom, names its negative conditioning input "negative".
            if (inputs.TryGetProperty("negative", out var negative) && LinkTarget(negative) is { } source)
            {
                negatives.Add(source);
            }

            if (CoreLoraLoaders.Contains(classType))
            {
                if (String(inputs, "lora_name") is { } file) loras.Add(Use(file, Number(inputs, "strength_model")));
            }
            else if (classType.Contains("Power Lora Loader", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var input in inputs.EnumerateObject())
                {
                    var entry = input.Value;
                    if (entry.ValueKind != JsonValueKind.Object || String(entry, "lora") is not { } file) continue;
                    // rgthree keeps every row it ever had; only the switched-on ones were applied.
                    if (!entry.TryGetProperty("on", out var on) || on.ValueKind != JsonValueKind.True) continue;
                    loras.Add(Use(file, Number(entry, "strength")));
                }
            }
            else if (ModelLoaders.TryGetValue(classType, out var widget))
            {
                if (String(inputs, widget) is { } file) models.Add(file);
            }
            else if (classType.Equals("VAELoader", StringComparison.OrdinalIgnoreCase))
            {
                if (String(inputs, "vae_name") is { } file) vaes.Add(file);
            }
        }

        // With several loaders the file alone does not say which one made the image.
        return new ComfyGraphFacts(
            Merge(loras),
            models.Count == 1 ? models[0] : null,
            vaes.Count == 1 ? vaes[0] : null,
            negatives.Count > 0 && negatives.All(id =>
                classes.TryGetValue(id, out var type) && type.Equals("ConditioningZeroOut", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The source node id of a link input (<c>["12", 0]</c>); null for a literal.</summary>
    private static string? LinkTarget(JsonElement input) =>
        input.ValueKind == JsonValueKind.Array && input.GetArrayLength() == 2 && input[0].ValueKind == JsonValueKind.String
            ? input[0].GetString()
            : null;

    /// <summary>
    /// One use per LoRA file. Wan 2.2 graphs load the same LoRA on the high- and low-noise chains; two
    /// uses of one file would leave the second unmatched and read as "not linked on Civitai". Different
    /// weights are all kept in <see cref="RecipeLoraUse.AllWeights"/>.
    /// </summary>
    private static List<RecipeLoraUse> Merge(List<RecipeLoraUse> loras) => loras
        .GroupBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var weights = group.Select(l => l.Weight).ToList();
            return weights.Distinct().Count() > 1 ? group.First() with { AllWeights = weights } : group.First();
        })
        .ToList();

    internal static string StemOf(string file) =>
        Path.GetFileNameWithoutExtension(file.Replace('\\', '/').Split('/')[^1]);

    private static RecipeLoraUse Use(string file, double? weight) => new(StemOf(file), file, weight, null);

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    /// <summary>A literal number; a link (<c>["12", 0]</c>) or anything else is unknown.</summary>
    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
