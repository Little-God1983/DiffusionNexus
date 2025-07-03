using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using Serilog;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using System.Collections.Generic;

namespace DiffusionNexus.Service.Services;

public class LocalFileMetadataProvider : IModelMetadataProvider
{
    public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(File.Exists(identifier));
    }

    public async Task<ModelClass> GetModelMetadataAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        var model = new ModelClass
        {
            SafeTensorFileName = ExtractBaseName(file.Name),
            AssociatedFilesInfo = new List<FileInfo>()
        };

        string baseName = ExtractBaseName(file.Name);
        var dir = file.Directory ?? new DirectoryInfo(Path.GetDirectoryName(filePath)!);

        var civitai = dir.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
        var cmInfo = dir.GetFiles($"{baseName}.cm-info.json").FirstOrDefault();
        var json = dir.GetFiles($"{baseName}.json").FirstOrDefault();

        if (civitai != null)
        {
            model.AssociatedFilesInfo.Add(civitai);
            await LoadFromCivitaiInfo(civitai, model);
        }
        else if (cmInfo != null)
        {
            model.AssociatedFilesInfo.Add(cmInfo);
            await LoadFromCmInfo(cmInfo, model);
        }
        else if (json != null)
        {
            model.AssociatedFilesInfo.Add(json);
            await LoadFromJson(json, model);
        }

        model.AssociatedFilesInfo.Add(file);

        if (string.IsNullOrWhiteSpace(model.ModelVersionName))
            model.ModelVersionName = baseName;

        if (file.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase))
        {
            string _ = await Task.Run(() => ComputeSHA256(filePath), cancellationToken);
        }
        return model;
    }

    private static async Task LoadFromCivitaiInfo(FileInfo file, ModelClass model)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("baseModel", out var baseModel))
            model.DiffusionBaseModel = baseModel.GetString() ?? model.DiffusionBaseModel;

        if (root.TryGetProperty("model", out var modelElement))
        {
            if (modelElement.TryGetProperty("name", out var name))
                model.ModelVersionName = name.GetString() ?? model.ModelVersionName;
            if (modelElement.TryGetProperty("type", out var type))
                model.ModelType = ParseModelType(type.GetString());
            if (modelElement.TryGetProperty("tags", out var tags))
                model.Tags = ParseTags(tags);
        }
    }

    private static async Task LoadFromCmInfo(FileInfo file, ModelClass model)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("Tags", out var tags))
            model.Tags = ParseTags(tags);
        if (root.TryGetProperty("sd version", out var sdver))
            model.DiffusionBaseModel = sdver.GetString() ?? model.DiffusionBaseModel;
    }

    private static async Task LoadFromJson(FileInfo file, ModelClass model)
    {
        var json = await File.ReadAllTextAsync(file.FullName);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("tags", out var tags))
            model.Tags = ParseTags(tags);
        if (root.TryGetProperty("sd version", out var sdver))
            model.DiffusionBaseModel = sdver.GetString() ?? model.DiffusionBaseModel;
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
