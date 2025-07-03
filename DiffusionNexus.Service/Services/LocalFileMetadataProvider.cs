using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using Serilog;
using System.Security.Cryptography;
using System.Text.Json;

namespace DiffusionNexus.Service.Services;

public class LocalFileMetadataProvider : IModelMetadataProvider
{
    public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(identifier));
    }

    public async Task<ModelMetadata> GetModelMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var metadata = new ModelMetadata();
        var fileInfo = new FileInfo(filePath);
        var baseName = ExtractBaseName(fileInfo.Name);

        var directory = fileInfo.Directory ?? new DirectoryInfo(Path.GetDirectoryName(filePath)!);
        var civitaiInfo = directory.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
        var cmInfo = directory.GetFiles($"{baseName}.cm-info.json").FirstOrDefault();
        var jsonInfo = directory.GetFiles($"{baseName}.json").FirstOrDefault();

        if (civitaiInfo != null)
        {
            await LoadFromCivitaiInfo(civitaiInfo, metadata);
        }
        else if (cmInfo != null)
        {
            await LoadFromCmInfo(cmInfo, metadata);
        }
        else if (jsonInfo != null)
        {
            await LoadFromJson(jsonInfo, metadata);
        }

        if (fileInfo.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            metadata.SHA256Hash = await Task.Run(() => ComputeSHA256(fileInfo.FullName), cancellationToken);
        }

        return metadata;
    }

    private static string ExtractBaseName(string fileName)
    {
        var known = StaticFileTypes.GeneralExtensions.OrderByDescending(e => e.Length);
        foreach (var ext in known)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return fileName[..^ext.Length];
        }
        return Path.GetFileNameWithoutExtension(fileName);
    }

    private static string ComputeSHA256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
    }

    private static async Task LoadFromCivitaiInfo(FileInfo file, ModelMetadata metadata)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("baseModel", out var baseModel))
            metadata.BaseModel = baseModel.GetString();

        if (root.TryGetProperty("model", out var model))
        {
            if (model.TryGetProperty("name", out var name))
                metadata.ModelVersionName = name.GetString();
            if (model.TryGetProperty("type", out var type))
                metadata.ModelType = ParseModelType(type.GetString());
            if (model.TryGetProperty("tags", out var tags))
                metadata.Tags = ParseTags(tags);
        }
    }

    private static async Task LoadFromCmInfo(FileInfo file, ModelMetadata metadata)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("Tags", out var tags))
            metadata.Tags = ParseTags(tags);
    }

    private static async Task LoadFromJson(FileInfo file, ModelMetadata metadata)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("sd version", out var version))
            metadata.BaseModel = version.GetString();
        if (root.TryGetProperty("tags", out var tags))
            metadata.Tags = ParseTags(tags);
    }

    private static List<string> ParseTags(JsonElement element)
    {
        var list = new List<string>();
        foreach (var tag in element.EnumerateArray())
        {
            if (tag.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
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
