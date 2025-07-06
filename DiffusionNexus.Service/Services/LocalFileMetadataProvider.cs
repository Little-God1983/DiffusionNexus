using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using System.Security.Cryptography;
using System.Text.Json;

using ModelMover.Core.Metadata;
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
                meta.ModelType = ModelMetadataUtils.ParseModelType(type.GetString());
                meta.Tags = ModelMetadataUtils.ParseTags(tags);
            meta.ModelType = ModelMetadataUtils.ParseModelType(rootType.GetString());
            meta.Tags = ModelMetadataUtils.ParseTags(rootTags);

            meta.ModelType = ModelMetadataUtils.ParseModelType(type.GetString());
            meta.Tags = ModelMetadataUtils.ParseTags(tags);


        meta.NoMetaData = !meta.HasAnyMetadata;
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

    private static DiffusionTypes ParseModelType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
            return DiffusionTypes.UNASSIGNED;
        if (Enum.TryParse(type.Replace(" ", string.Empty), true, out DiffusionTypes dt))
            return dt;
        return DiffusionTypes.UNASSIGNED;
    }

    private static string ExtractBaseName(string fileName)
    {
        var known = StaticFileTypes.GeneralExtensions
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return known != null ? fileName[..^known.Length] : fileName;
    }
}

