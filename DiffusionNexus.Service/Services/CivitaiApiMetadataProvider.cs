using DiffusionNexus.Service.Classes;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiffusionNexus.Service.Services;

public class CivitaiApiMetadataProvider : IModelMetadataProvider
{
    private readonly ICivitaiApiClient _apiClient;
    private readonly string _apiKey;

    public CivitaiApiMetadataProvider(ICivitaiApiClient apiClient, string apiKey)
    {
        _apiClient = apiClient;
        _apiKey = apiKey;
    }

    public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(identifier.Length == 64 && Regex.IsMatch(identifier, "^[a-fA-F0-9]+$"));
    }

    public async Task<ModelClass> GetModelMetadataAsync(string sha256Hash, CancellationToken cancellationToken = default)
    {
        var meta = new ModelClass
        {
            SHA256Hash = sha256Hash
        };

        var versionJson = await _apiClient.GetModelVersionByHashAsync(sha256Hash, _apiKey);
        using var versionDoc = JsonDocument.Parse(versionJson);
        var versionRoot = versionDoc.RootElement;

        if (versionRoot.TryGetProperty("modelId", out var modelId))
        {
            meta.ModelId = modelId.GetString();
            var modelJson = await _apiClient.GetModelAsync(meta.ModelId, _apiKey);
            using var modelDoc = JsonDocument.Parse(modelJson);
            ParseModelInfo(modelDoc.RootElement, meta);
        }

        if (versionRoot.TryGetProperty("baseModel", out var baseModel))
            meta.DiffusionBaseModel = baseModel.GetString();

        if (versionRoot.TryGetProperty("name", out var versionName))
            meta.ModelVersionName = versionName.GetString();
        meta.NoMetaData = !meta.HasAnyMetadata;
        return meta;
    }

    private static void ParseModelInfo(JsonElement root, ModelClass meta)
    {
        if (root.TryGetProperty("type", out var type))
            meta.ModelType = ParseModelType(type.GetString());

        if (root.TryGetProperty("tags", out var tags))
            meta.Tags = ParseTags(tags);
    }

    private static DiffusionTypes ParseModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return DiffusionTypes.OTHER;
        if (Enum.TryParse(type.Replace(" ", string.Empty), true, out DiffusionTypes dt))
            return dt;
        return DiffusionTypes.OTHER;
    }

    private static List<string> ParseTags(JsonElement tags)
    {
        var result = new List<string>();
        foreach (var t in tags.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    result.Add(s);
            }
        }
        return result;
    }
}

