using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using Serilog;
using System.Security.Cryptography;
using System.Text;
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
        var file = new FileInfo(filePath);
        var metadata = new ModelMetadata();

        string baseName = ExtractBaseName(file.Name);
        var dir = file.Directory ?? new DirectoryInfo(Path.GetDirectoryName(filePath)!);

        var civitai = dir.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
        var cmInfo = dir.GetFiles($"{baseName}.cm-info.json").FirstOrDefault();
        var json = dir.GetFiles($"{baseName}.json").FirstOrDefault();

        if (civitai != null)
            await LoadFromCivitaiInfo(civitai, metadata);
        else if (cmInfo != null)
            await LoadFromCmInfo(cmInfo, metadata);
        else if (json != null)
            await LoadFromJson(json, metadata);

        if (string.IsNullOrWhiteSpace(metadata.ModelVersionName))
            metadata.ModelVersionName = baseName;

        if (file.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
            metadata.SHA256Hash = await Task.Run(() => ComputeSHA256(filePath), cancellationToken);

        return metadata;
    }

    private static async Task LoadFromCivitaiInfo(FileInfo file, ModelMetadata metadata)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("baseModel", out var baseModel))
            metadata.BaseModel = baseModel.GetString() ?? metadata.BaseModel;

        if (root.TryGetProperty("model", out var model))
        {
            if (model.TryGetProperty("name", out var name))
                metadata.ModelVersionName = name.GetString() ?? metadata.ModelVersionName;
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
        if (root.TryGetProperty("sd version", out var sdver))
            metadata.BaseModel = sdver.GetString() ?? metadata.BaseModel;
    }

    private static async Task LoadFromJson(FileInfo file, ModelMetadata metadata)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("tags", out var tags))
            metadata.Tags = ParseTags(tags);
        if (root.TryGetProperty("sd version", out var sdver))
            metadata.BaseModel = sdver.GetString() ?? metadata.BaseModel;
    }

    private static string ExtractBaseName(string fileName)
    {
        var ext = StaticFileTypes.GeneralExtensions
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        return ext == null ? fileName : fileName[..^ext.Length];
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
        foreach (var element in tags.EnumerateArray())
        {
            var val = element.GetString();
            if (!string.IsNullOrWhiteSpace(val))
                list.Add(val);
        }
        return list;
    }

    private static string ComputeSHA256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(stream);
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
