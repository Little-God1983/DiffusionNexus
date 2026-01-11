using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Enums;
using DiffusionNexus.Service.Helper;
using DiffusionNexus.Service.Metadata;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiffusionNexus.Service.Services;

public class LocalFileMetadataProvider : IModelMetadataProvider
{
    public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(identifier));
    }

    public async Task<ModelClass> GetModelMetadataAsync(string filePath, CancellationToken cancellationToken = default, ModelClass? model = null)
    {
        model ??= new ModelClass(); // Fixes CS0841 by ensuring 'model' is initialized before usage.

        var fileInfo = new FileInfo(filePath);
        model.SafeTensorFileName = Path.GetFileNameWithoutExtension(filePath);

        var baseName = ModelMetadataUtils.ExtractBaseName(fileInfo.Name);
        var directory = fileInfo.Directory;
        var civitaiInfoFile = directory?.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
        var jsonFile = directory?.GetFiles($"{baseName}.json").FirstOrDefault();

        if (civitaiInfoFile != null)
        {
            await LoadFromCivitaiInfo(civitaiInfoFile, model);
        }
        else if (jsonFile != null)
        {
            await LoadFromJson(jsonFile, model);
        }
        else
        {
            model.NoMetaData = true;
        }

        model.NoMetaData = !model.HasAnyMetadata;
        return model;
    }

    private static async Task LoadFromCivitaiInfo(FileInfo file, ModelClass meta)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("modelId", out var modelId))
        {
            meta.ModelId = modelId.ValueKind switch
            {
                JsonValueKind.String => modelId.GetString(),
                JsonValueKind.Number => modelId.GetInt64().ToString(),   // or GetInt32/GetUInt64
                _ => null
            };
        }

        if (root.TryGetProperty("baseModel", out var baseModel))
            meta.DiffusionBaseModel = baseModel.GetString() ?? meta.DiffusionBaseModel;

        if (root.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == JsonValueKind.Array)
        {
            meta.TrainedWords = trained.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();
        }

        if (root.TryGetProperty("model", out var model))
        {
            if (model.TryGetProperty("name", out var name))
                meta.ModelVersionName = name.GetString() ?? meta.ModelVersionName;
            if (model.TryGetProperty("type", out var type))
                meta.ModelType = ModelMetadataUtils.ParseModelType(type.GetString());
            if (model.TryGetProperty("tags", out var tags))
                meta.Tags = ModelMetadataUtils.ParseTags(tags);
            if (model.TryGetProperty("nsfw", out var nsfw) && nsfw.ValueKind != JsonValueKind.Null)
                meta.Nsfw = nsfw.ValueKind == JsonValueKind.True || nsfw.ValueKind == JsonValueKind.False ? nsfw.GetBoolean() : null;
        }

        if (meta.ModelType == DiffusionTypes.UNASSIGNED && root.TryGetProperty("type", out var rootType))
            meta.ModelType = ModelMetadataUtils.ParseModelType(rootType.GetString());

        if (meta.Tags.Count == 0 && root.TryGetProperty("tags", out var rootTags))
            meta.Tags = ModelMetadataUtils.ParseTags(rootTags);

        meta.NoMetaData = !meta.HasAnyMetadata;
    }

    private static async Task LoadFromJson(FileInfo file, ModelClass meta)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("sd version", out var ver))
            meta.DiffusionBaseModel = ver.GetString() ?? meta.DiffusionBaseModel;
        if (root.TryGetProperty("type", out var type))
            meta.ModelType = ModelMetadataUtils.ParseModelType(type.GetString());
        if (root.TryGetProperty("tags", out var tags))
            meta.Tags = ModelMetadataUtils.ParseTags(tags);

        meta.NoMetaData = !meta.HasAnyMetadata;
    }


}
