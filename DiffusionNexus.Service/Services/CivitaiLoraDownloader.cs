using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.Service.Services;

public class CivitaiLoraDownloader : ILoraDownloader
{
    private readonly ICivitaiApiClient _apiClient;
    private readonly HttpClient _httpClient;

    public CivitaiLoraDownloader(ICivitaiApiClient apiClient, HttpClient httpClient)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<LoraDownloadResult> DownloadAsync(LoraDownloadRequest request, IProgress<LoraDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        if (string.IsNullOrWhiteSpace(request.TargetDirectory))
            throw new ArgumentException("Target directory must be provided.", nameof(request));

        Directory.CreateDirectory(request.TargetDirectory);

        var version = await ResolveVersionAsync(request, cancellationToken).ConfigureAwait(false);
        var file = SelectFile(version);

        if (file == null)
            throw new InvalidOperationException("No downloadable file was found for this model version.");

        var fileName = SanitizeFileName(file.Name, request.ModelId, version.Id);
        var targetPath = EnsureTargetWithinDirectory(Path.Combine(request.TargetDirectory, fileName), request.TargetDirectory);

        var tempPath = targetPath + ".partial";

        if (File.Exists(targetPath))
        {
            var resolution = request.ConflictResolver != null
                ? await request.ConflictResolver(new LoraDownloadConflictContext(targetPath, fileName, new FileInfo(targetPath).Length)).ConfigureAwait(false)
                : new LoraDownloadConflictResolution(LoraDownloadConflictResolutionType.Skip);

            switch (resolution.Type)
            {
                case LoraDownloadConflictResolutionType.Skip:
                    return new LoraDownloadResult(LoraDownloadResultStatus.Skipped, targetPath);
                case LoraDownloadConflictResolutionType.Rename:
                    if (string.IsNullOrWhiteSpace(resolution.FileName))
                        throw new InvalidOperationException("A file name must be supplied when renaming.");
                    fileName = SanitizeFileName(resolution.FileName, request.ModelId, version.Id);
                    targetPath = EnsureTargetWithinDirectory(Path.Combine(request.TargetDirectory, fileName), request.TargetDirectory);
                    tempPath = targetPath + ".partial";
                    break;
                case LoraDownloadConflictResolutionType.Overwrite:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        progress?.Report(new LoraDownloadProgress(0, file.TotalBytes));

        var requestMessage = new HttpRequestMessage(HttpMethod.Get, file.DownloadUrl);
        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", request.ApiKey);
        }

        using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? file.TotalBytes;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            long totalRead = 0;
            while (true)
            {
                var read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalRead += read;
                progress?.Report(new LoraDownloadProgress(totalRead, totalBytes));
            }

            await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (File.Exists(targetPath))
        {
            File.Delete(targetPath);
        }

        File.Move(tempPath, targetPath, overwrite: true);

        return new LoraDownloadResult(LoraDownloadResultStatus.Completed, targetPath);
    }

    private async Task<ModelVersionDto> ResolveVersionAsync(LoraDownloadRequest request, CancellationToken cancellationToken)
    {
        if (request.ModelVersionId.HasValue)
        {
            var json = await _apiClient.GetModelVersionAsync(request.ModelVersionId.Value.ToString(CultureInfo.InvariantCulture), request.ApiKey ?? string.Empty).ConfigureAwait(false);
            return ParseVersion(JsonDocument.Parse(json).RootElement);
        }

        var modelJson = await _apiClient.GetModelAsync(request.ModelId.ToString(CultureInfo.InvariantCulture), request.ApiKey ?? string.Empty).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(modelJson);
        if (doc.RootElement.TryGetProperty("modelVersions", out var versions) && versions.ValueKind == JsonValueKind.Array)
        {
            var first = versions.EnumerateArray().OrderByDescending(v => v.TryGetProperty("createdAt", out var created) && created.ValueKind == JsonValueKind.String ? DateTime.TryParse(created.GetString(), out var dt) ? dt : DateTime.MinValue : DateTime.MinValue).FirstOrDefault();
            if (first.ValueKind != JsonValueKind.Undefined)
            {
                return ParseVersion(first);
            }
        }

        throw new InvalidOperationException("No model versions were found for this model.");
    }

    private static ModelVersionDto ParseVersion(JsonElement element)
    {
        if (!element.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException("Invalid model version payload.");

        var id = idEl.GetInt32();
        var files = element.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array
            ? filesEl.EnumerateArray().Select(ParseFile).Where(f => f != null).Cast<ModelFileDto>().ToList()
            : new List<ModelFileDto>();

        return new ModelVersionDto(id, files);
    }

    private static ModelFileDto? ParseFile(JsonElement element)
    {
        if (!element.TryGetProperty("downloadUrl", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return null;

        var name = element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
            ? nameEl.GetString()
            : null;

        long? size = null;
        if (element.TryGetProperty("sizeKB", out var sizeEl) && sizeEl.ValueKind is JsonValueKind.Number)
        {
            var kb = sizeEl.GetDouble();
            size = (long)(kb * 1024);
        }

        string? format = null;
        if (element.TryGetProperty("metadata", out var metadataEl) && metadataEl.ValueKind == JsonValueKind.Object && metadataEl.TryGetProperty("format", out var formatEl) && formatEl.ValueKind == JsonValueKind.String)
        {
            format = formatEl.GetString();
        }

        var type = element.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
            ? typeEl.GetString()
            : null;

        return new ModelFileDto(name, urlEl.GetString()!, size, type, format);
    }

    private static ModelFileDto? SelectFile(ModelVersionDto version)
    {
        static bool IsPreferred(ModelFileDto file) =>
            string.Equals(file.Format, "safetensor", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(file.Type, "model", StringComparison.OrdinalIgnoreCase) &&
             file.Name != null && file.Name.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase));

        return version.Files.FirstOrDefault(IsPreferred) ?? version.Files.FirstOrDefault();
    }

    private static string SanitizeFileName(string? name, int modelId, int versionId)
    {
        var candidate = string.IsNullOrWhiteSpace(name)
            ? $"model-{modelId}-{versionId}.safetensors"
            : Path.GetFileName(name);

        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = $"model-{modelId}-{versionId}.safetensors";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            candidate = candidate.Replace(invalid, '_');
        }

        return candidate;
    }

    private static string EnsureTargetWithinDirectory(string targetPath, string baseDirectory)
    {
        var fullTarget = Path.GetFullPath(targetPath);
        var fullBase = Path.GetFullPath(baseDirectory);
        if (!fullTarget.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved file path escapes the target directory.");
        return fullTarget;
    }

    private sealed record ModelVersionDto(int Id, List<ModelFileDto> Files);

    private sealed record ModelFileDto(string? Name, string DownloadUrl, long? TotalBytes, string? Type, string? Format);
}
