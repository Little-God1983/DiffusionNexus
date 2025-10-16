using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.IO;

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

    internal static (string? PreviewUrl, string? ModelId, string? ModelVersionId, string? Description, List<string> TrainedWords, bool? Nsfw) ParseInfoJson(string infoJson)
    {
        using var doc = JsonDocument.Parse(infoJson);
        var root = doc.RootElement;
        string? previewUrl = null;
        string? modelId = null;
        string? modelVersionId = null;
        string? description = null;
        var trainedWords = new List<string>();
        bool? nsfw = null;

        if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            var first = images[0];
            if (first.TryGetProperty("url", out var urlEl))
                previewUrl = urlEl.GetString();
        }

        if (root.TryGetProperty("modelId", out var modelIdEl))
            modelId = ConvertElementToString(modelIdEl);

        if (root.TryGetProperty("modelVersionId", out var versionIdEl))
            modelVersionId = ConvertElementToString(versionIdEl);

        if (root.TryGetProperty("trainedWords", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in wordsEl.EnumerateArray())
            {
                if (w.ValueKind == JsonValueKind.String)
                    trainedWords.Add(w.GetString()!);
            }
        }

        if (root.TryGetProperty("model", out var modelEl))
        {
            if (modelEl.TryGetProperty("nsfw", out var nsfwEl) && nsfwEl.ValueKind != JsonValueKind.Null)
            {
                if (nsfwEl.ValueKind == JsonValueKind.True || nsfwEl.ValueKind == JsonValueKind.False)
                    nsfw = nsfwEl.GetBoolean();
            }

            if (string.IsNullOrWhiteSpace(description) && modelEl.TryGetProperty("description", out var descriptionEl))
                description = ExtractDescription(descriptionEl) ?? description;
        }

        if (string.IsNullOrWhiteSpace(description) && root.TryGetProperty("description", out var descriptionRoot))
            description = ExtractDescription(descriptionRoot) ?? description;

        return (previewUrl, modelId, modelVersionId, description, trainedWords, nsfw);
    }

    public async Task<MetadataDownloadResult> EnsureMetadataAsync(ModelClass model, string apiKey)
    {
        var folder = model.AssociatedFilesInfo.FirstOrDefault()?.DirectoryName;
        if (folder == null)
            return new MetadataDownloadResult(MetadataDownloadResultType.Error, null, "No folder found");

        var baseName = model.SafeTensorFileName;
        var CivitaiInfoPath = Path.Combine(folder, baseName + ".civitai.info");

        string previewUrl = String.Empty;
        string modelId = String.Empty;
        string? modelVersionId = null;
        string? description = null;

        bool hasCivitaiInfo = false;
        if (File.Exists(CivitaiInfoPath))
        {
            hasCivitaiInfo = true;
            var CivitaiJson = await File.ReadAllTextAsync(CivitaiInfoPath);
            (previewUrl, string id, string? versionId, string? infoDescription, List<string> words, bool? infoNsfw) = ParseInfoJson(CivitaiJson);
            if (!string.IsNullOrWhiteSpace(id))
            {
                modelId = id;
                model.ModelId = id;
            }

            if (!string.IsNullOrWhiteSpace(versionId))
                model.ModelVersionId = versionId;

            if (!string.IsNullOrWhiteSpace(infoDescription))
                model.Description = infoDescription;

            if (words.Count > 0)
                model.TrainedWords = words;
            if (infoNsfw.HasValue)
                model.Nsfw = infoNsfw;
        }

        bool hasJson = HasJson(model);
        bool hasMedia = HasMedia(model);

        if (hasCivitaiInfo && hasJson && hasMedia)
            return new MetadataDownloadResult(MetadataDownloadResultType.AlreadyExists, model.ModelId);

        var tensor = model.AssociatedFilesInfo.FirstOrDefault(f =>
            f.Extension.Equals(".safetensors", StringComparison.OrdinalIgnoreCase) ||
            f.Extension.Equals(".pt", StringComparison.OrdinalIgnoreCase))?.FullName;
        if (tensor == null)
            return new MetadataDownloadResult(MetadataDownloadResultType.Error, null, "No tensor file found");

        if (!hasCivitaiInfo)
        {
            string hash = ComputeSHA256(tensor);
            string CivitaiInfoJson;
            try
            {
                CivitaiInfoJson = await _apiClient.GetModelVersionByHashAsync(hash, apiKey);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return new MetadataDownloadResult(MetadataDownloadResultType.NotFound, null, ex.Message);
            }
            catch (Exception ex)
            {
                return new MetadataDownloadResult(MetadataDownloadResultType.Error, null, ex.Message);
            }

            try
            {
                await File.WriteAllTextAsync(CivitaiInfoPath, CivitaiInfoJson);
            }
            catch (Exception ex)
            {
                return new MetadataDownloadResult(MetadataDownloadResultType.Error, null, ex.Message);
            }

            (previewUrl, modelId, modelVersionId, description, List<string> words2, bool? nsfw2) = ParseInfoJson(CivitaiInfoJson);
            if (!string.IsNullOrWhiteSpace(modelId))
                model.ModelId = modelId;
            if (!string.IsNullOrWhiteSpace(modelVersionId))
                model.ModelVersionId = modelVersionId;
            if (!string.IsNullOrWhiteSpace(description))
                model.Description = description;
            if (words2.Count > 0)
                model.TrainedWords = words2;
            if (nsfw2.HasValue)
                model.Nsfw = nsfw2;
        }

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
            catch (Exception ex)
            {
                return new MetadataDownloadResult(MetadataDownloadResultType.Error, null, ex.Message);
            }
        }

        if (!hasJson && !string.IsNullOrWhiteSpace(modelId))
        {
            try
            {
                var modelJson = await _apiClient.GetModelAsync(modelId, apiKey);
                var jsonPath = Path.Combine(folder, baseName + ".json");
                await File.WriteAllTextAsync(jsonPath, modelJson);
            }
            catch (Exception ex)
            {
                return new MetadataDownloadResult(MetadataDownloadResultType.Error, null, ex.Message);
            }
        }

        return new MetadataDownloadResult(MetadataDownloadResultType.Downloaded, modelId);
    }

    private static string? ConvertElementToString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.GetInt64().ToString(),
            JsonValueKind.String => element.GetString(),
            _ => null
        };
    }

    private static string? ExtractDescription(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Array:
                var segments = element.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                return segments.Length > 0 ? string.Join(Environment.NewLine + Environment.NewLine, segments!) : null;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var candidate = ExtractDescription(property.Value);
                    if (!string.IsNullOrWhiteSpace(candidate))
                        return candidate;
                }
                break;
        }

        return null;
    }
}
