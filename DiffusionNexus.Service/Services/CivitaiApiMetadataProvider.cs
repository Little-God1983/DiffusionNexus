using DiffusionNexus.Service.Classes;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
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

    private static string ComputeSHA256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }

    public async Task<ModelClass> GetModelMetadataAsync(string filePath, CancellationToken cancellationToken = default, ModelClass modelClass = null)
    {
        string SHA256Hash = await Task.Run(() => ComputeSHA256(filePath), cancellationToken);
        if (modelClass == null)
            modelClass = new();
        
        modelClass.SHA256Hash = SHA256Hash;

        //calculateHash here
        try
        {
            string versionJson = await _apiClient.GetModelVersionByHashAsync(modelClass.SHA256Hash, _apiKey);
            using JsonDocument versionDoc = JsonDocument.Parse(versionJson);
            JsonElement versionRoot = versionDoc.RootElement;

            if (versionRoot.TryGetProperty("modelId", out var modelId))
            {
                modelClass.ModelId = modelId.ValueKind switch
                {
                    JsonValueKind.String => modelId.GetString(),
                    JsonValueKind.Number => modelId.GetInt64().ToString(),   // or GetInt32/GetUInt64…
                    _ => null
                };
                var modelJson = await _apiClient.GetModelAsync(modelClass.ModelId, _apiKey);
                using var modelDoc = JsonDocument.Parse(modelJson);
                ParseModelInfo(modelDoc.RootElement, modelClass);
            }

            if (versionRoot.TryGetProperty("baseModel", out var baseModel))
                modelClass.DiffusionBaseModel = baseModel.GetString();

            if (versionRoot.TryGetProperty("name", out var versionName))
                modelClass.ModelVersionName = versionName.GetString();
            modelClass.NoMetaData = !modelClass.HasAnyMetadata;

        }
        catch (Exception ex)
        {
            //TODO:LOG Not Found
            
        }
      
        return modelClass;
    }

    private static void ParseModelInfo(JsonElement root, ModelClass modelClass)
    {
        if (root.TryGetProperty("type", out var type))
            modelClass.ModelType = ParseModelType(type.GetString());

        if (root.TryGetProperty("tags", out var tags))
            modelClass.Tags = ParseTags(tags);
        modelClass.CivitaiCategory = MetaDataUtilService.GetCategoryFromTags(modelClass.Tags);
    }

    private static DiffusionTypes ParseModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return DiffusionTypes.UNASSIGNED;
        if (Enum.TryParse(type.Replace(" ", string.Empty), true, out DiffusionTypes dt))
            return dt;
        return DiffusionTypes.UNASSIGNED;
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

