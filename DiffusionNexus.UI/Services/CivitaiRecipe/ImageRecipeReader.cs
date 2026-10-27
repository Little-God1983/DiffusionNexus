using System.Globalization;
using System.Text.Json;
using DiffusionNexus.UI.Services.Distiller;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// Turns an original Civitai file's texts into the recipe view's generation data (#628):
/// <list type="number">
/// <item>prompt and settings from the ComfyUI graph trace when it succeeds, otherwise from the
/// A1111/Forge parameters text — the shared tracer only knows the core samplers, so a graph built on
/// a custom sampler (the example's ClownsharKSampler) falls back to the text the same file carries;</item>
/// <item>LoRA weights from every loader node in the graph (<see cref="ComfyLoraScan"/>), else from the
/// text's <c>&lt;lora:name:weight&gt;</c> tags; the graph wins when both have them;</item>
/// <item>each LoRA's hash from the text's <c>Lora hashes</c> (or <c>Hashes</c>) entry of the same name.</item>
/// </list>
/// </summary>
internal static class ImageRecipeReader
{
    private const string NotePrefix = "Read from the original image file";

    public static ImageRecipeData Read(string filePath)
    {
        try
        {
            return FromTexts(RecipeFileReader.Read(filePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ImageRecipeData.Empty(RecipeFileFormat.Unknown, $"The original image file could not be read ({ex.Message}).");
        }
    }

    public static ImageRecipeData FromTexts(RecipeFileText file)
    {
        string? readError = null;
        Dictionary<string, JsonElement>? graph = null;
        if (file.Texts.TryGetValue("prompt", out var graphJson) && !string.IsNullOrWhiteSpace(graphJson))
        {
            try
            {
                graph = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(graphJson);
            }
            catch (JsonException ex)
            {
                readError = $"The ComfyUI workflow in the file could not be read ({ex.Message}).";
            }
        }

        file.Texts.TryGetValue("parameters", out var parameters);
        if (string.IsNullOrWhiteSpace(parameters)) parameters = null;

        var facts = graph is null ? new ComfyGraphFacts([], null, null) : ComfyLoraScan.Scan(graph);
        var trace = graph is null ? null : ComfyUiPromptTracer.Trace(graph, null, 0, 0);
        var text = parameters is null ? null : ImageMetadataParser.ParseA1111Parameters(parameters, null, 0, 0);
        IReadOnlyDictionary<string, string> settings = parameters is null
            ? new Dictionary<string, string>()
            : ImageMetadataParser.ReadA1111Settings(parameters);
        settings.TryGetValue("VAE", out var textVae);

        var civitai = CivitaiResources(settings);
        var graphLoras = facts.Loras.Count > 0;
        var loras = graphLoras ? facts.Loras.Select(l => l with { Hash = HashFor(l.Name, settings) }).ToList()
            : civitai.Loras.Count > 0 ? civitai.Loras
            : TagLoras(parameters).Select(l => l with { Hash = HashFor(l.Name, settings) }).ToList();

        var data = new ImageRecipeData { Format = file.Format, Loras = loras, ReadError = readError };

        if (trace is { HasData: true })
        {
            return data with
            {
                Source = RecipeDataSource.ComfyUiWorkflow,
                SourceNote = $"{NotePrefix} (ComfyUI workflow embedded)",
                Prompt = trace.PositivePrompt,
                NegativePrompt = trace.NegativePrompt,
                // The tracer gives the Distiller's bare stem; the file as the graph wrote it reads better.
                Checkpoint = facts.Checkpoint ?? trace.Checkpoint,
                Vae = facts.Vae ?? textVae,
                Sampler = trace.SamplerName,
                Scheduler = trace.Scheduler,
                Steps = trace.Steps,
                Cfg = trace.Cfg,
                Seed = trace.Seed,
            };
        }

        if (text is not null && (text.HasData || settings.Count > 0))
        {
            return data with
            {
                Source = RecipeDataSource.SettingsText,
                SourceNote = graphLoras
                    ? $"{NotePrefix} (settings text; LoRA weights from the ComfyUI workflow)"
                    : $"{NotePrefix} (settings text)",
                Prompt = text.PositivePrompt,
                NegativePrompt = text.NegativePrompt,
                Checkpoint = text.Checkpoint ?? civitai.Checkpoint ?? facts.Checkpoint,
                Vae = textVae ?? facts.Vae,
                Sampler = text.SamplerName,
                Scheduler = text.Scheduler,
                Steps = text.Steps,
                Cfg = text.Cfg,
                Seed = text.Seed,
            };
        }

        if (graphLoras || facts.Checkpoint is not null)
        {
            return data with
            {
                Source = RecipeDataSource.ComfyUiWorkflow,
                SourceNote = $"{NotePrefix} (ComfyUI workflow embedded)",
                Checkpoint = facts.Checkpoint,
                Vae = facts.Vae,
            };
        }

        return data;
    }

    /// <summary>
    /// The text's LoRA tags, read here rather than from <c>ImageGenerationData.Loras</c>: that list
    /// gives a tag whose weight does not parse a strength of 1, and the recipe must say "unknown".
    /// </summary>
    private static IEnumerable<RecipeLoraUse> TagLoras(string? parameters)
    {
        if (parameters is null) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match match in ImageMetadataParser.LoraTagRegex().Matches(parameters))
        {
            var name = match.Groups[1].Value.Trim();
            if (name.Length == 0 || !seen.Add(name)) continue;
            var first = match.Groups[2].Value.Split(':')[0].Trim();
            double? weight = double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) ? w : null;
            yield return new RecipeLoraUse(name, null, weight, null);
        }
    }

    /// <summary>
    /// Civitai's own generator writes no LoRA tags; its settings carry <c>Civitai resources: [{"type":"lora",
    /// "weight":0.9,"modelVersionId":…,"modelName":…}, {"type":"checkpoint",…}]</c> — weights keyed by version id
    /// (image 147042061, 2026-10-27). A value that is not that JSON gives nothing.
    /// </summary>
    private static (List<RecipeLoraUse> Loras, string? Checkpoint) CivitaiResources(IReadOnlyDictionary<string, string> settings)
    {
        var loras = new List<RecipeLoraUse>();
        string? checkpoint = null;
        if (!settings.TryGetValue("Civitai resources", out var json)) return (loras, null);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return (loras, null);
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var type = Text(entry, "type");
                var name = Text(entry, "modelName");
                int? versionId = entry.TryGetProperty("modelVersionId", out var id) && id.TryGetInt32(out var v) ? v : null;
                if (string.Equals(type, "checkpoint", StringComparison.OrdinalIgnoreCase) && name is not null)
                {
                    checkpoint ??= Text(entry, "modelVersionName") is { } version ? $"{name} ({version})" : name;
                }
                else if (type is not null
                         && (type.Contains("lora", StringComparison.OrdinalIgnoreCase)
                             || type.Equals("locon", StringComparison.OrdinalIgnoreCase)
                             || type.Equals("dora", StringComparison.OrdinalIgnoreCase)))
                {
                    double? weight = entry.TryGetProperty("weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetDouble() : null;
                    loras.Add(new RecipeLoraUse(name ?? $"Version {versionId}", null, weight, null, versionId));
                }
            }
        }
        catch (JsonException)
        {
            loras.Clear();
            checkpoint = null;
        }
        return (loras, checkpoint);

        static string? Text(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    /// <summary><c>Lora hashes: "name: hash, name: hash"</c> first, then the <c>Hashes</c> JSON's <c>lora:name</c>.</summary>
    private static string? HashFor(string name, IReadOnlyDictionary<string, string> settings)
    {
        if (settings.TryGetValue("Lora hashes", out var list))
        {
            foreach (var pair in list.Split(','))
            {
                var colon = pair.LastIndexOf(':');
                if (colon <= 0) continue;
                if (pair[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    var hash = pair[(colon + 1)..].Trim();
                    if (hash.Length > 0) return hash;
                }
            }
        }

        if (settings.TryGetValue("Hashes", out var json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (property.Name.Equals($"lora:{name}", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                }
            }
            catch (JsonException)
            {
                // A Hashes value that is not JSON gives no hash; the name match still applies.
            }
        }

        return null;
    }
}
