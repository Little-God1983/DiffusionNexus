using DiffusionNexus.Service.Classes;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelMover.Core.Metadata;

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
                    JsonValueKind.Number => modelId.GetInt64().ToString(),
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
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Model not found in API is a valid case - just mark it as no metadata
            modelClass.NoMetaData = true;
        }
        catch (JsonException)
        {
            // Invalid JSON is a critical error - propagate it
            throw;
        }
        catch (HttpRequestException)
        {
            // Network/API errors should be propagated
            throw;
        }
      
        return modelClass;
    }

    private static void ParseModelInfo(JsonElement root, ModelClass modelClass)
    {
        if (root.TryGetProperty("type", out var type))
            modelClass.ModelType = ModelMetadataUtils.ParseModelType(type.GetString());

        if (root.TryGetProperty("tags", out var tags))
            modelClass.Tags = ModelMetadataUtils.ParseTags(tags);
        modelClass.CivitaiCategory = MetaDataUtilService.GetCategoryFromTags(modelClass.Tags);
    }


}

