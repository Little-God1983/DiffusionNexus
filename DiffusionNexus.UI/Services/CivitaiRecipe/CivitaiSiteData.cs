using System.Net.Http.Headers;
using System.Text.Json;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>One resource as Civitai's image page knows it; <paramref name="Strength"/> is the LoRA weight.</summary>
public sealed record CivitaiSiteResource(int VersionId, double? Strength, string? Name, string? ModelType);

/// <summary>What Civitai's image page shows for an image: its resources and, when Civitai has it, the generation data.</summary>
public sealed record CivitaiSiteData(IReadOnlyList<CivitaiSiteResource> Resources, ImageRecipeData? Meta);

/// <summary>
/// Reads <c>image.getGenerationData</c> — the endpoint Civitai's own image page uses — with the user's
/// API key (owner's decision, 2026-10-28). The public API can leave <c>modelVersionIds</c> empty while the
/// page lists six resources (image 146878246), and it never sends <c>meta</c>; this endpoint answers both,
/// with each LoRA's weight. Anonymous calls get 401, so it is only asked when a key is stored. It is not
/// a public API: any failure or change of shape yields null and the recipe stays as the public API made it.
/// </summary>
internal static class CivitaiSiteDataReader
{
    private const string SiteNote = "Read from Civitai's data for this image (the file itself carries none)";

    public static async Task<CivitaiSiteData?> GetAsync(HttpClient http, long imageId, string apiKey, CancellationToken ct)
    {
        var input = Uri.EscapeDataString($"{{\"json\":{{\"id\":{imageId}}}}}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://civitai.com/api/trpc/image.getGenerationData?input={input}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    internal static CivitaiSiteData? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!TryGet(doc.RootElement, out var data, "result", "data", "json") || data.ValueKind != JsonValueKind.Object) return null;

            var resources = new List<CivitaiSiteResource>();
            if (data.TryGetProperty("resources", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object || Int(entry, "modelVersionId") is not int versionId) continue;
                    resources.Add(new CivitaiSiteResource(versionId, Number(entry, "strength"), Text(entry, "modelName"), Text(entry, "modelType")));
                }
            }

            ImageRecipeData? meta = null;
            if (data.TryGetProperty("meta", out var m) && m.ValueKind == JsonValueKind.Object)
            {
                var candidate = new ImageRecipeData
                {
                    Source = RecipeDataSource.CivitaiSite,
                    SourceNote = SiteNote,
                    Prompt = Text(m, "prompt"),
                    NegativePrompt = Text(m, "negativePrompt"),
                    Checkpoint = Text(m, "Model"),
                    Vae = Text(m, "VAE"),
                    Sampler = Text(m, "sampler"),
                    Scheduler = Text(m, "scheduler"),
                    Steps = Int(m, "steps"),
                    Cfg = Number(m, "cfgScale"),
                    Seed = m.TryGetProperty("seed", out var seed) && seed.ValueKind == JsonValueKind.Number && seed.TryGetInt64(out var s) ? s : null,
                };
                if (candidate.Prompt is not null || candidate.Steps is not null || candidate.Sampler is not null || candidate.Seed is not null)
                {
                    meta = candidate;
                }
            }

            return new CivitaiSiteData(resources, meta);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGet(JsonElement element, out JsonElement found, params string[] path)
    {
        found = element;
        foreach (var name in path)
        {
            if (found.ValueKind != JsonValueKind.Object || !found.TryGetProperty(name, out found)) return false;
        }
        return true;
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int? Int(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}
