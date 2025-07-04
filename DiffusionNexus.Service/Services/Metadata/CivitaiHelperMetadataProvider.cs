using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using Serilog;
using System.Text.Json;

namespace DiffusionNexus.Service.Services.Metadata
{
    public class CivitaiHelperMetadataProvider : IModelMetadataProvider
    {
        public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(File.Exists(identifier));
        }

        public async Task<ModelClass?> GetModelMetadataAsync(string filePath, CancellationToken cancellationToken = default)
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
            {
                return null;
            }

            var metadata = new ModelClass
            {
                SafeTensorFileName = ExtractBaseName(info.Name),
                AssociatedFilesInfo = new List<FileInfo>()
            };

            var directory = info.Directory;
            if (directory == null) return metadata;

            var baseName = ExtractBaseName(info.Name);
            var civitaiInfoFile = directory.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
            var jsonFile = directory.GetFiles($"{baseName}.json").FirstOrDefault();
            var cmInfoFile = directory.GetFiles($"{baseName}.cm-info.json").FirstOrDefault();

            if (civitaiInfoFile != null)
            {
                await LoadFromCivitaiInfo(civitaiInfoFile, metadata, cancellationToken);
            }
            else if (jsonFile != null)
            {
                await LoadFromJson(jsonFile, metadata, cancellationToken);
            }

            if (cmInfoFile != null)
            {
                await LoadTagsFromJson(cmInfoFile, "Tags", metadata, cancellationToken);
            }

            metadata.AssociatedFilesInfo = directory.GetFiles($"{baseName}*").ToList();
            return metadata;
        }

        private static async Task LoadFromCivitaiInfo(FileInfo file, ModelClass metadata, CancellationToken token)
        {
            var json = await File.ReadAllTextAsync(file.FullName, token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("baseModel", out var baseModel))
                metadata.DiffusionBaseModel = baseModel.GetString() ?? metadata.DiffusionBaseModel;

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

        private static async Task LoadFromJson(FileInfo file, ModelClass metadata, CancellationToken token)
        {
            var json = await File.ReadAllTextAsync(file.FullName, token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("sd version", out var version))
                metadata.DiffusionBaseModel = version.GetString() ?? metadata.DiffusionBaseModel;
        }

        private static async Task LoadTagsFromJson(FileInfo file, string property, ModelClass metadata, CancellationToken token)
        {
            var json = await File.ReadAllTextAsync(file.FullName, token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty(property, out var tagElement))
            {
                metadata.Tags = ParseTags(tagElement);
            }
            else if (root.TryGetProperty("model", out var model) && model.TryGetProperty(property, out var nested))
            {
                metadata.Tags = ParseTags(nested);
            }
        }

        private static List<string> ParseTags(JsonElement tags)
        {
            var list = new List<string>();
            foreach (var t in tags.EnumerateArray())
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
            return list;
        }

        private static DiffusionTypes ParseModelType(string? type)
        {
            if (string.IsNullOrWhiteSpace(type))
                return DiffusionTypes.OTHER;
            var cleaned = type.Replace(" ", string.Empty).ToUpperInvariant();
            return Enum.TryParse(cleaned, true, out DiffusionTypes result) ? result : DiffusionTypes.OTHER;
        }

        private static string ExtractBaseName(string fileName)
        {
            var extensions = StaticFileTypes.GeneralExtensions.OrderByDescending(e => e.Length);
            foreach (var ext in extensions)
            {
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return fileName.Substring(0, fileName.Length - ext.Length);
            }
            return Path.GetFileNameWithoutExtension(fileName);
        }
    }
}
