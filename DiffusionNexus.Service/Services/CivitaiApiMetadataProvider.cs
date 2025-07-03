using DiffusionNexus.Service.Classes;
using System.Text.Json;

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
        var valid = identifier.Length == 64 && System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[a-fA-F0-9]+$");
        return Task.FromResult(valid);
    }

    public async Task<ModelMetadata> GetModelMetadataAsync(string sha256Hash, CancellationToken cancellationToken = default)
    {
        var metadata = new ModelMetadata { SHA256Hash = sha256Hash };

        var versionJson = await _apiClient.GetModelVersionByHashAsync(sha256Hash, _apiKey);
        using var versionDoc = JsonDocument.Parse(versionJson);
        var versionRoot = versionDoc.RootElement;

        if (versionRoot.TryGetProperty("modelId", out var modelIdElement))
        {
            metadata.ModelId = modelIdElement.GetString();
            var modelJson = await _apiClient.GetModelAsync(metadata.ModelId, _apiKey);
            using var modelDoc = JsonDocument.Parse(modelJson);
            var modelRoot = modelDoc.RootElement;
            ParseModelInfo(modelRoot, metadata);
        }

        if (versionRoot.TryGetProperty("baseModel", out var baseModel))
            metadata.BaseModel = baseModel.GetString();
        if (versionRoot.TryGetProperty("name", out var versionName))
            metadata.ModelVersionName = versionName.GetString();

        return metadata;
    }

    private static void ParseModelInfo(JsonElement root, ModelMetadata metadata)
    {
        if (root.TryGetProperty("type", out var type))
            metadata.ModelType = ParseModelType(type.GetString());
        if (root.TryGetProperty("tags", out var tags))
            metadata.Tags = ParseTags(tags);
    }

    private static List<string> ParseTags(JsonElement element)
    {
        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
        return list;
    }

    private static DiffusionTypes ParseModelType(string? type)
    {
        if (type != null && Enum.TryParse(type.Replace(" ", string.Empty), true, out DiffusionTypes t))
            return t;
        return DiffusionTypes.OTHER;
    }
}
