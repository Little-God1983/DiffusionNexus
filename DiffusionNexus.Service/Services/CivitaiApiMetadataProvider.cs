using DiffusionNexus.Service.Classes;
using Serilog;
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
        bool match = identifier.Length == 64 && Regex.IsMatch(identifier, "^[a-fA-F0-9]+$");
        return Task.FromResult(match);
    }

    public async Task<ModelMetadata> GetModelMetadataAsync(string sha256Hash, CancellationToken cancellationToken = default)
    {
        var metadata = new ModelMetadata { SHA256Hash = sha256Hash };

        var versionJson = await _apiClient.GetModelVersionByHashAsync(sha256Hash, _apiKey);
        using var versionDoc = JsonDocument.Parse(versionJson);
        var versionRoot = versionDoc.RootElement;

        if (versionRoot.TryGetProperty("modelId", out var modelIdEl))
            metadata.ModelId = modelIdEl.GetRawText().Trim('"');
        if (versionRoot.TryGetProperty("baseModel", out var baseModel))
            metadata.BaseModel = baseModel.GetString() ?? metadata.BaseModel;
        if (versionRoot.TryGetProperty("name", out var versionName))
            metadata.ModelVersionName = versionName.GetString() ?? metadata.ModelVersionName;

        if (!string.IsNullOrEmpty(metadata.ModelId))
        {
            var modelJson = await _apiClient.GetModelAsync(metadata.ModelId, _apiKey);
            using var modelDoc = JsonDocument.Parse(modelJson);
            var modelRoot = modelDoc.RootElement;
            ParseModelInfo(modelRoot, metadata);
        }

        return metadata;
    }

    private static void ParseModelInfo(JsonElement root, ModelMetadata metadata)
    {
        if (root.TryGetProperty("type", out var type))
            metadata.ModelType = ParseModelType(type.GetString());
        if (root.TryGetProperty("tags", out var tags))
            metadata.Tags = ParseTags(tags);
    }

    private static DiffusionTypes ParseModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return DiffusionTypes.OTHER;
        var normalized = type.Replace(" ", string.Empty);
        return Enum.TryParse<DiffusionTypes>(normalized, true, out var result) ? result : DiffusionTypes.OTHER;
    }

    private static List<string> ParseTags(JsonElement tags)
    {
        var list = new List<string>();
        foreach (var el in tags.EnumerateArray())
        {
            var val = el.GetString();
            if (!string.IsNullOrWhiteSpace(val))
                list.Add(val);
        }
        return list;
    }
}
