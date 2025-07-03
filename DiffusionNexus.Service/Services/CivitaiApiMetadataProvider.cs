using DiffusionNexus.Service.Classes;
using Serilog;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using System.Collections.Generic;

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

    public async Task<ModelClass> GetModelMetadataAsync(string sha256Hash, CancellationToken cancellationToken = default)
    {
        var model = new ModelClass
        {
            SafeTensorFileName = sha256Hash,
            AssociatedFilesInfo = new List<FileInfo>()
        };

        var versionJson = await _apiClient.GetModelVersionByHashAsync(sha256Hash, _apiKey);
        using var versionDoc = JsonDocument.Parse(versionJson);
        var versionRoot = versionDoc.RootElement;

        string? modelId = null;
        if (versionRoot.TryGetProperty("modelId", out var modelIdEl))
            modelId = modelIdEl.GetRawText().Trim('"');
        if (versionRoot.TryGetProperty("baseModel", out var baseModel))
            model.DiffusionBaseModel = baseModel.GetString() ?? model.DiffusionBaseModel;
        if (versionRoot.TryGetProperty("name", out var versionName))
            model.ModelVersionName = versionName.GetString() ?? model.ModelVersionName;

        if (!string.IsNullOrEmpty(modelId))
        {
            var modelJson = await _apiClient.GetModelAsync(modelId, _apiKey);
            using var modelDoc = JsonDocument.Parse(modelJson);
            var modelRoot = modelDoc.RootElement;
            ParseModelInfo(modelRoot, model);
        }

        model.CivitaiCategory = GetCategoryFromTags(model.Tags);
        return model;
    }

    private static void ParseModelInfo(JsonElement root, ModelClass model)
    {
        if (root.TryGetProperty("type", out var type))
            model.ModelType = ParseModelType(type.GetString());
        if (root.TryGetProperty("tags", out var tags))
            model.Tags = ParseTags(tags);
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

    private static CivitaiBaseCategories GetCategoryFromTags(List<string> tags)
    {
        foreach (var tag in tags)
        {
            if (Enum.TryParse(tag.Replace(" ", "_").ToUpper(), out CivitaiBaseCategories category))
                return category;
        }
        return CivitaiBaseCategories.UNKNOWN;
    }
}
