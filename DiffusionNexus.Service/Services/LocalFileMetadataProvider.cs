using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiffusionNexus.Service.Services;

public class LocalFileMetadataProvider : IModelMetadataProvider
{
    public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(identifier));
    }

    public async Task<ModelClass> GetModelMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fileInfo = new FileInfo(filePath);
        var meta = new ModelClass
        {
            SafeTensorFileName = Path.GetFileNameWithoutExtension(filePath)
        };

        var baseName = ExtractBaseName(fileInfo.Name);
        var directory = fileInfo.Directory;
        var civitaiInfoFile = directory?.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
        var jsonFile = directory?.GetFiles($"{baseName}.json").FirstOrDefault();

        if (civitaiInfoFile != null)
        {
            await LoadFromCivitaiInfo(civitaiInfoFile, meta);
        }
        else if (jsonFile != null)
        {
            await LoadFromJson(jsonFile, meta);
        }
        else
        {
            meta.NoMetaData = true;
        }

        meta.NoMetaData = !meta.HasAnyMetadata;
        return meta;
    }

    private static async Task LoadFromCivitaiInfo(FileInfo file, ModelClass meta)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("baseModel", out var baseModel))
            meta.DiffusionBaseModel = baseModel.GetString();

        if (root.TryGetProperty("model", out var model))
        {
            if (model.TryGetProperty("name", out var name))
                meta.ModelVersionName = name.GetString();
            if (model.TryGetProperty("type", out var type))
                meta.ModelType = ParseModelType(type.GetString());
            if (model.TryGetProperty("tags", out var tags))
                meta.Tags = ParseTags(tags);
        }

        if (meta.ModelType == DiffusionTypes.OTHER && root.TryGetProperty("type", out var rootType))
            meta.ModelType = ParseModelType(rootType.GetString());

        if (meta.Tags.Count == 0 && root.TryGetProperty("tags", out var rootTags))
            meta.Tags = ParseTags(rootTags);

        meta.NoMetaData = !meta.HasAnyMetadata;
    }

    private static async Task LoadFromJson(FileInfo file, ModelClass meta)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("sd version", out var ver))
            meta.DiffusionBaseModel = ver.GetString();
        if (root.TryGetProperty("type", out var type))
            meta.ModelType = ParseModelType(type.GetString());
        if (root.TryGetProperty("tags", out var tags))
            meta.Tags = ParseTags(tags);

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
            return DiffusionTypes.OTHER;
        if (Enum.TryParse(type.Replace(" ", string.Empty), true, out DiffusionTypes dt))
            return dt;
        return DiffusionTypes.OTHER;
    }

    private static string ExtractBaseName(string fileName)
    {
        var known = StaticFileTypes.GeneralExtensions
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return known != null ? fileName[..^known.Length] : fileName;
    }
}

