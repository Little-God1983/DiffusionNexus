using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Helper utilities for parsing Civitai URLs and retrieving model metadata.
/// </summary>
public class CivitaiModelService
{
    private readonly ICivitaiApiClient _apiClient;

    public CivitaiModelService(ICivitaiApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public bool TryParseModelUrl(string url, out CivitaiModelReference reference)
    {
        reference = new CivitaiModelReference(null, null);
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (!string.Equals(uri.Host, "civitai.com", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Host, "www.civitai.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length >= 2 && string.Equals(segments[0], "models", StringComparison.OrdinalIgnoreCase))
        {
            if (int.TryParse(segments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var modelId))
            {
                int? versionId = null;
                if (!string.IsNullOrEmpty(uri.Query))
                {
                    var parameters = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var parameter in parameters)
                    {
                        var parts = parameter.Split('=', 2, StringSplitOptions.TrimEntries);
                        if (parts.Length == 2 && string.Equals(parts[0], "modelVersionId", StringComparison.OrdinalIgnoreCase))
                        {
                            var value = Uri.UnescapeDataString(parts[1]);
                            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedVersion))
                            {
                                versionId = parsedVersion;
                                break;
                            }
                        }
                    }
                }

                reference = new CivitaiModelReference(modelId, versionId);
                return true;
            }

            return false;
        }

        if (segments.Length >= 3 && string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], "download", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[2], "models", StringComparison.OrdinalIgnoreCase))
        {
            if (segments.Length >= 4 && int.TryParse(segments[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var versionId))
            {
                reference = new CivitaiModelReference(null, versionId);
                return true;
            }
        }

        return false;
    }

    public async Task<CivitaiModelVersionInfo> GetModelVersionInfoAsync(CivitaiModelReference reference, string apiKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (reference.ModelVersionId is null)
        {
            if (reference.ModelId is null)
            {
                throw new InvalidOperationException("Model version id or model id must be provided.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var modelJson = await _apiClient.GetModelAsync(reference.ModelId.Value.ToString(CultureInfo.InvariantCulture), apiKey);
            using var modelDoc = JsonDocument.Parse(modelJson);
            if (!modelDoc.RootElement.TryGetProperty("modelVersions", out var versionsElement) || versionsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Model does not contain any versions.");
            }

            var selectedVersion = versionsElement.EnumerateArray()
                .OrderByDescending(v => TryGetDateTime(v, "publishedAt"))
                .ThenByDescending(v => TryGetDateTime(v, "createdAt"))
                .FirstOrDefault();

            if (selectedVersion.ValueKind == JsonValueKind.Undefined)
            {
                throw new InvalidOperationException("Model does not contain any versions.");
            }

            reference = reference with { ModelVersionId = selectedVersion.GetProperty("id").GetInt32() };
        }

        cancellationToken.ThrowIfCancellationRequested();
        var versionJson = await _apiClient.GetModelVersionAsync(reference.ModelVersionId.Value.ToString(CultureInfo.InvariantCulture), apiKey);
        using var versionDoc = JsonDocument.Parse(versionJson);
        var root = versionDoc.RootElement;

        var modelId = root.TryGetProperty("modelId", out var modelIdElement) && modelIdElement.ValueKind == JsonValueKind.Number
            ? modelIdElement.GetInt32()
            : reference.ModelId ?? throw new InvalidOperationException("Unable to determine model id for the selected version.");

        var versionName = root.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? $"Version {reference.ModelVersionId.Value}"
            : $"Version {reference.ModelVersionId.Value}";

        string? baseModel = null;
        if (root.TryGetProperty("baseModel", out var baseModelElement) && baseModelElement.ValueKind == JsonValueKind.String)
        {
            baseModel = baseModelElement.GetString();
        }

        string? modelType = null;
        if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.Object &&
            modelElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            modelType = typeElement.GetString();
        }

        var trainedWords = new List<string>();
        if (root.TryGetProperty("trainedWords", out var wordsElement) && wordsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var wordElement in wordsElement.EnumerateArray())
            {
                if (wordElement.ValueKind == JsonValueKind.String)
                {
                    var word = wordElement.GetString();
                    if (!string.IsNullOrWhiteSpace(word))
                    {
                        trainedWords.Add(word);
                    }
                }
            }
        }

        string? description = null;
        if (root.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String)
        {
            description = descriptionElement.GetString();
        }

        var files = new List<CivitaiModelFileInfo>();
        if (root.TryGetProperty("files", out var filesElement) && filesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileElement in filesElement.EnumerateArray())
            {
                if (fileElement.ValueKind != JsonValueKind.Object)
                    continue;

                var downloadUrl = fileElement.TryGetProperty("downloadUrl", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
                    ? urlElement.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(downloadUrl))
                    continue;

                var name = fileElement.TryGetProperty("name", out var fileNameElement) && fileNameElement.ValueKind == JsonValueKind.String
                    ? fileNameElement.GetString() ?? string.Empty
                    : string.Empty;

                var type = fileElement.TryGetProperty("type", out var typeElement2) && typeElement2.ValueKind == JsonValueKind.String
                    ? typeElement2.GetString() ?? string.Empty
                    : string.Empty;

                bool isPrimary = fileElement.TryGetProperty("primary", out var primaryElement) && primaryElement.ValueKind == JsonValueKind.True;

                string? format = null;
                if (fileElement.TryGetProperty("metadata", out var metadataElement) && metadataElement.ValueKind == JsonValueKind.Object &&
                    metadataElement.TryGetProperty("format", out var formatElement) && formatElement.ValueKind == JsonValueKind.String)
                {
                    format = formatElement.GetString();
                }
                else if (fileElement.TryGetProperty("format", out var formatElement2) && formatElement2.ValueKind == JsonValueKind.String)
                {
                    format = formatElement2.GetString();
                }

                long? sizeBytes = null;
                if (fileElement.TryGetProperty("sizeKB", out var sizeElement) && sizeElement.ValueKind is JsonValueKind.Number)
                {
                    if (sizeElement.TryGetDouble(out var sizeKb))
                    {
                        sizeBytes = (long)Math.Round(sizeKb * 1024);
                    }
                }

                string? sha256 = null;
                if (fileElement.TryGetProperty("hashes", out var hashesElement) && hashesElement.ValueKind == JsonValueKind.Object &&
                    hashesElement.TryGetProperty("SHA256", out var shaElement) && shaElement.ValueKind == JsonValueKind.String)
                {
                    sha256 = shaElement.GetString();
                }

                files.Add(new CivitaiModelFileInfo(
                    name,
                    type,
                    format,
                    isPrimary,
                    downloadUrl!,
                    sizeBytes,
                    sha256));
            }
        }

        return new CivitaiModelVersionInfo(
            modelId,
            reference.ModelVersionId!.Value,
            versionName,
            baseModel,
            modelType,
            trainedWords,
            files,
            description);
    }

    private static DateTime TryGetDateTime(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            if (DateTime.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                return date;
            }
        }

        return DateTime.MinValue;
    }
}
