using DiffusionNexus.Service.Classes;
using System.Text.Json;

namespace DiffusionNexus.Service.Services;

public class LoraMetadataDownloadService
{
    private readonly ICivitaiApiClient _apiClient;

    public LoraMetadataDownloadService(ICivitaiApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    internal static bool HasInfo(ModelClass model) =>
        model.AssociatedFilesInfo.Any(f => f.Name.EndsWith(".civitai.info", StringComparison.OrdinalIgnoreCase));

    internal static bool HasJson(ModelClass model) =>
        model.AssociatedFilesInfo.Any(f => f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) && !f.Name.EndsWith(".civitai.info", StringComparison.OrdinalIgnoreCase));

    internal static bool HasMedia(ModelClass model) =>
        model.AssociatedFilesInfo.Any(f => SupportedTypes.ImageTypesByPriority.Any(ext => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) ||
                                           SupportedTypes.VideoTypesByPriority.Any(ext => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));

    internal static string ComputeSHA256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return string.Concat(hash.Select(b => b.ToString("x2")));
    }

    internal static (string? PreviewUrl, string? ModelId) ParseInfoJson(string infoJson)
    {
        using var doc = JsonDocument.Parse(infoJson);
        var root = doc.RootElement;
        string? previewUrl = null;
        string? modelId = null;

        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            var first = images[0];
            if (first.TryGetProperty("url", out var urlEl))
                previewUrl = urlEl.GetString();
        }

        if (root.TryGetProperty("modelId", out var modelIdEl))
        {
            modelId = modelIdEl.ValueKind switch
            {
                JsonValueKind.Number => modelIdEl.GetInt64().ToString(),
                JsonValueKind.String => modelIdEl.GetString(),
                _ => null
            };
        }

        return (previewUrl, modelId);
    }

    public async Task<string?> EnsureMetadataAsync(ModelClass model, string apiKey)
    {
        var folder = model.AssociatedFilesInfo.FirstOrDefault()?.DirectoryName;
        if (folder == null)
            return null;

        var baseName = model.SafeTensorFileName;
        var infoPath = Path.Combine(folder, baseName + ".civitai.info");

        if (File.Exists(infoPath))
        {
            var json = await File.ReadAllTextAsync(infoPath);
            var (_, id) = ParseInfoJson(json);
            if (!string.IsNullOrWhiteSpace(id))
            {
                model.ModelId = id;
                return id;
            }
        }

        bool hasInfo = HasInfo(model);
        bool hasJson = HasJson(model);
        bool hasMedia = HasMedia(model);

        if (hasInfo && hasJson && hasMedia)
            return model.ModelId;

        var tensor = model.AssociatedFilesInfo.FirstOrDefault(f =>
            f.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            f.Extension.Equals(".pt", StringComparison.OrdinalIgnoreCase))?.FullName;
        if (tensor == null)
            return null;

        string hash = ComputeSHA256(tensor);
        string infoJson;
        try
        {
            infoJson = await _apiClient.GetModelVersionByHashAsync(hash, apiKey);
        }
        catch
        {
            return null;
        }

        await File.WriteAllTextAsync(infoPath, infoJson);

        var (previewUrl, modelId) = ParseInfoJson(infoJson);
        if (!string.IsNullOrWhiteSpace(modelId))
            model.ModelId = modelId;

        if (!hasMedia && !string.IsNullOrWhiteSpace(previewUrl))
        {
            try
            {
                var ext = Path.GetExtension(new Uri(previewUrl).AbsolutePath);
                var outPath = Path.Combine(folder, baseName + ext);
                using var http = new HttpClient();
                var bytes = await http.GetByteArrayAsync(previewUrl);
                await File.WriteAllBytesAsync(outPath, bytes);
            }
            catch { }
        }

        if (!hasJson && !string.IsNullOrWhiteSpace(modelId))
        {
            try
            {
                var modelJson = await _apiClient.GetModelAsync(modelId, apiKey);
                var jsonPath = Path.Combine(folder, baseName + ".json");
                await File.WriteAllTextAsync(jsonPath, modelJson);
            }
            catch { }
        }

        return modelId;
    }
}
