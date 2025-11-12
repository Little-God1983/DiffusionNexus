using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

public class LoraDownloadService
{
    private readonly ICivitaiApiClient _apiClient;
    private readonly HttpClient _httpClient;

    public LoraDownloadService(ICivitaiApiClient apiClient, HttpClient? httpClient = null)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<CivitaiModelInfo> ResolveAsync(string civitaiLink, string apiKey, CancellationToken cancellationToken = default)
    {
        if (!CivitaiLinkParser.TryParse(civitaiLink, out var linkInfo) || linkInfo is null)
        {
            throw new ArgumentException("Invalid Civitai link", nameof(civitaiLink));
        }

        var modelJson = await _apiClient.GetModelAsync(linkInfo.ModelId, apiKey);
        cancellationToken.ThrowIfCancellationRequested();

        var versionId = linkInfo.ModelVersionId ?? ExtractPreferredVersionId(modelJson)
            ?? throw new InvalidOperationException("Unable to determine model version id from Civitai response.");

        var versionJson = await _apiClient.GetModelVersionAsync(versionId, apiKey);
        cancellationToken.ThrowIfCancellationRequested();

        var file = ExtractPreferredFile(versionJson)
            ?? throw new InvalidOperationException("The selected version does not contain any downloadable files.");

        var downloadUrl = TryGetString(file, "downloadUrl")
            ?? throw new InvalidOperationException("Unable to determine download URL for the selected file.");
        var fileName = TryGetString(file, "name")
            ?? throw new InvalidOperationException("Unable to determine file name for the selected file.");

        var previewUrl = ExtractPreviewImage(versionJson);

        return new CivitaiModelInfo(linkInfo.ModelId, versionId, fileName, downloadUrl, versionJson, modelJson, previewUrl);
    }

    public async Task<LoraDownloadResult> DownloadAsync(
        CivitaiModelInfo modelInfo,
        string targetDirectory,
        IProgress<LoraDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (modelInfo is null)
        {
            throw new ArgumentNullException(nameof(modelInfo));
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("Target directory is required", nameof(targetDirectory));
        }

        Directory.CreateDirectory(targetDirectory);

        var destinationFile = Path.Combine(targetDirectory, modelInfo.FileName);
        if (File.Exists(destinationFile))
        {
            File.Delete(destinationFile);
        }
        await DownloadFileAsync(modelInfo.DownloadUrl, destinationFile, progress, cancellationToken);

        var baseName = Path.GetFileNameWithoutExtension(modelInfo.FileName);
        await WriteMetadataAsync(modelInfo, targetDirectory, baseName, cancellationToken);

        return new LoraDownloadResult(destinationFile, modelInfo.ModelId, modelInfo.ModelVersionId);
    }

    private async Task DownloadFileAsync(
        string downloadUrl,
        string destination,
        IProgress<LoraDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(destination);

        var buffer = new byte[81920];
        long totalRead = 0;
        var stopwatch = Stopwatch.StartNew();

        while (true)
        {
            var read = await contentStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            double? speed = null;
            if (stopwatch.Elapsed.TotalSeconds > 0)
            {
                speed = totalRead / stopwatch.Elapsed.TotalSeconds;
            }

            progress?.Report(new LoraDownloadProgress(totalRead, totalBytes, speed));
        }

        progress?.Report(new LoraDownloadProgress(totalRead, totalBytes, null));
    }

    private async Task WriteMetadataAsync(
        CivitaiModelInfo modelInfo,
        string folder,
        string baseName,
        CancellationToken cancellationToken)
    {
        var infoPath = Path.Combine(folder, baseName + ".civitai.info");
        if (File.Exists(infoPath))
        {
            File.Delete(infoPath);
        }
        await File.WriteAllTextAsync(infoPath, modelInfo.VersionJson, cancellationToken);

        if (!string.IsNullOrWhiteSpace(modelInfo.PreviewImageUrl))
        {
            try
            {
                var uri = new Uri(modelInfo.PreviewImageUrl);
                var ext = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(ext))
                {
                    var imagePath = Path.Combine(folder, baseName + ext);
                    if (File.Exists(imagePath))
                    {
                        File.Delete(imagePath);
                    }
                    using var previewRequest = new HttpRequestMessage(HttpMethod.Get, modelInfo.PreviewImageUrl);
                    using var previewResponse = await _httpClient.SendAsync(previewRequest, cancellationToken);
                    previewResponse.EnsureSuccessStatusCode();
                    await using var imageStream = await previewResponse.Content.ReadAsStreamAsync(cancellationToken);
                    await using var imageFile = File.Create(imagePath);
                    await imageStream.CopyToAsync(imageFile, cancellationToken);
                }
            }
            catch
            {
                // Ignore preview failures but keep metadata files.
            }
        }

        var modelJsonPath = Path.Combine(folder, baseName + ".json");
        if (File.Exists(modelJsonPath))
        {
            File.Delete(modelJsonPath);
        }
        await File.WriteAllTextAsync(modelJsonPath, modelInfo.ModelJson, cancellationToken);
    }

    private static JsonElement? ExtractPreferredFile(string versionJson)
    {
        using var doc = JsonDocument.Parse(versionJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? primary = null;
        JsonElement? safetensors = null;
        JsonElement? first = null;

        foreach (var file in files.EnumerateArray())
        {
            if (file.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            first ??= file;

            if (file.TryGetProperty("primary", out var primaryProp) && primaryProp.ValueKind == JsonValueKind.True)
            {
                primary = file;
                break;
            }

            var name = TryGetString(file, "name");
            if (safetensors == null && name != null && name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            {
                safetensors = file;
            }
        }

        return primary ?? safetensors ?? first;
    }

    private static string? ExtractPreferredVersionId(string modelJson)
    {
        using var doc = JsonDocument.Parse(modelJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("modelVersions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var version in versions.EnumerateArray())
        {
            if (version.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (version.TryGetProperty("id", out var idElement))
            {
                return idElement.ValueKind switch
                {
                    JsonValueKind.Number => idElement.GetInt64().ToString(),
                    JsonValueKind.String => idElement.GetString(),
                    _ => null
                };
            }
        }

        return null;
    }

    private static string? ExtractPreviewImage(string versionJson)
    {
        using var doc = JsonDocument.Parse(versionJson);
        var root = doc.RootElement;
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var image in images.EnumerateArray())
        {
            if (image.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = TryGetString(image, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }
        }

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetInt64().ToString(),
            _ => null
        };
    }
}
