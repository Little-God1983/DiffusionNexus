using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Default implementation that resolves download metadata via the Civitai API and streams
/// the resulting file to disk while reporting progress updates.
/// </summary>
public sealed class CivitaiLoraDownloader : ILoraDownloader
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);
    private readonly ICivitaiApiClient _apiClient;
    private readonly HttpClient _httpClient;

    public CivitaiLoraDownloader(ICivitaiApiClient apiClient, HttpClient httpClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<LoraDownloadPlan> PrepareAsync(int modelId, int? modelVersionId, string? apiKey, CancellationToken cancellationToken)
    {
        if (modelId <= 0)
            throw new ArgumentOutOfRangeException(nameof(modelId), "Model id must be greater than zero.");

        JsonElement versionElement;
        if (modelVersionId.HasValue)
        {
            versionElement = await LoadModelVersionAsync(modelVersionId.Value, apiKey, cancellationToken);
        }
        else
        {
            versionElement = await ResolveLatestVersionAsync(modelId, apiKey, cancellationToken);
        }

        var (fileName, downloadUri, totalBytes) = ExtractFileMetadata(versionElement);

        var sanitizedName = SanitizeFileName(fileName);
        return new LoraDownloadPlan(modelId, versionElement.GetProperty("id").GetInt32(), sanitizedName, downloadUri, totalBytes);
    }

    public async Task<LoraDownloadResult> DownloadAsync(
        LoraDownloadPlan plan,
        string targetFilePath,
        IProgress<LoraDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (plan is null)
            throw new ArgumentNullException(nameof(plan));

        if (string.IsNullOrWhiteSpace(targetFilePath))
            throw new ArgumentException("Target path is required.", nameof(targetFilePath));

        var fullTarget = Path.GetFullPath(targetFilePath);
        var targetDirectory = Path.GetDirectoryName(fullTarget) ?? throw new InvalidOperationException("Target path must include a directory.");
        Directory.CreateDirectory(targetDirectory);

        // Prevent path traversal outside of the target directory.
        var normalizedDirectory = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid target path.");
        }

        var tempPath = fullTarget + ".partial";

        using var request = new HttpRequestMessage(HttpMethod.Get, plan.DownloadUri);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var totalBytes = plan.TotalBytes ?? response.Content.Headers.ContentLength;
        var buffer = new byte[81920];
        long totalRead = 0;
        long lastReported = 0;
        var stopwatch = Stopwatch.StartNew();
        var lastReportTime = TimeSpan.Zero;

        try
        {
            while (true)
            {
                var bytesRead = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead == 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                totalRead += bytesRead;

                var elapsed = stopwatch.Elapsed;
                if (elapsed - lastReportTime >= ProgressInterval || totalBytes.HasValue && totalRead >= totalBytes.Value)
                {
                    var deltaBytes = totalRead - lastReported;
                    var deltaSeconds = Math.Max((elapsed - lastReportTime).TotalSeconds, 1e-6);
                    var speedMbps = deltaBytes / deltaSeconds / (1024 * 1024);
                    double? percent = totalBytes.HasValue && totalBytes.Value > 0 ? totalRead * 100.0 / totalBytes.Value : null;

                    TimeSpan? eta = null;
                    if (totalBytes.HasValue && speedMbps > 0)
                    {
                        var remainingBytes = totalBytes.Value - totalRead;
                        var secondsRemaining = remainingBytes / (speedMbps * 1024 * 1024);
                        eta = TimeSpan.FromSeconds(Math.Max(secondsRemaining, 0));
                    }

                    progress?.Report(new LoraDownloadProgress(totalRead, totalBytes, percent, speedMbps, eta));
                    lastReportTime = elapsed;
                    lastReported = totalRead;
                }
            }

            await fileStream.FlushAsync(cancellationToken);
        }
        catch
        {
            // On error or cancellation ensure partial file is cleaned up.
            await SafeDeleteAsync(tempPath);
            throw;
        }

        if (File.Exists(fullTarget))
        {
            File.Delete(fullTarget);
        }

        File.Move(tempPath, fullTarget);
        progress?.Report(new LoraDownloadProgress(totalRead, totalBytes, 100, 0, TimeSpan.Zero));
        return new LoraDownloadResult(true, fullTarget);
    }

    private async Task<JsonElement> LoadModelVersionAsync(int modelVersionId, string? apiKey, CancellationToken cancellationToken)
    {
        var json = await _apiClient.GetModelVersionAsync(modelVersionId.ToString(), apiKey ?? string.Empty);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private async Task<JsonElement> ResolveLatestVersionAsync(int modelId, string? apiKey, CancellationToken cancellationToken)
    {
        var json = await _apiClient.GetModelAsync(modelId.ToString(), apiKey ?? string.Empty);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("modelVersions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("No model versions available for this model.");
        }

        var enumerator = versions.EnumerateArray();
        if (!enumerator.MoveNext())
        {
            throw new InvalidOperationException("No model versions available for this model.");
        }

        return enumerator.Current.Clone();
    }

    private static (string FileName, Uri DownloadUri, long? TotalBytes) ExtractFileMetadata(JsonElement versionElement)
    {
        if (!versionElement.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Model version does not contain downloadable files.");
        }

        JsonElement? selected = null;
        foreach (var file in filesElement.EnumerateArray())
        {
            if (file.TryGetProperty("primary", out var primaryProp) && primaryProp.ValueKind == JsonValueKind.True)
            {
                selected = file;
                break;
            }
        }

        selected ??= filesElement.EnumerateArray().FirstOrDefault();
        if (selected is null)
        {
            throw new InvalidOperationException("Model version does not contain downloadable files.");
        }

        var name = selected.Value.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
            ? nameProp.GetString() ?? "model.safetensors"
            : "model.safetensors";

        if (!selected.Value.TryGetProperty("downloadUrl", out var urlProp) || urlProp.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("Download URL not provided by Civitai.");
        }

        var downloadUrl = urlProp.GetString();
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException("Download URL not provided by Civitai.");
        }

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Download URL is invalid.");
        }

        long? totalBytes = null;
        if (selected.Value.TryGetProperty("sizeKB", out var sizeProp))
        {
            try
            {
                var sizeKb = sizeProp.ValueKind switch
                {
                    JsonValueKind.Number => sizeProp.GetDouble(),
                    JsonValueKind.String when double.TryParse(sizeProp.GetString(), out var parsed) => parsed,
                    _ => (double?)null
                };

                if (sizeKb.HasValue)
                {
                    totalBytes = (long)Math.Round(sizeKb.Value * 1024);
                }
            }
            catch
            {
                totalBytes = null;
            }
        }

        return (name, uri, totalBytes);
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        sanitized = sanitized.Replace("..", ".");
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "model.safetensors";
        }

        return sanitized.Trim();
    }

    private static async Task SafeDeleteAsync(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            await Task.CompletedTask;
        }
    }
}
