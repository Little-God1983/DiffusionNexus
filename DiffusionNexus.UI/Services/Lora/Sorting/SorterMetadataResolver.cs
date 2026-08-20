using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>Metadata resolved for one on-disk file outside the DB.</summary>
public sealed record ResolvedLoraMetadata(string? BaseModelRaw, int? CivitaiVersionId, string Sha256);

/// <summary>
/// Resolves base-model/version metadata for a file the DB doesn't know about
/// (spec §3): a local <c>.civitai.info</c> sidecar wins outright (no hashing
/// needed), otherwise the file is hashed and looked up in a per-hash disk
/// cache, falling back to the Civitai by-hash API and caching that result
/// (including negative results, so a 404 stays resolved offline). Never
/// throws for a 404 or offline API — callers get metadata with a null
/// <see cref="ResolvedLoraMetadata.BaseModelRaw"/> so the file sorts into
/// Unknown.
/// </summary>
public sealed class SorterMetadataResolver
{
    private const string LogSource = "LoraSorter";

    private static readonly JsonSerializerOptions CacheJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ICivitaiClient? _civitaiClient;
    private readonly Func<Task<string?>> _apiKeyProvider;
    private readonly string _cacheDirectory;
    private readonly Func<string, string> _hashFile;
    private readonly IUnifiedLogger? _logger;

    /// <param name="cacheDirectory">Injected for tests; production uses
    /// Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    ///     "DiffusionNexus", "SorterCache").</param>
    /// <param name="hashFile">SHA256 (lowercase hex) of a file — injected for tests.</param>
    public SorterMetadataResolver(
        ICivitaiClient? civitaiClient,
        Func<Task<string?>> apiKeyProvider,
        string cacheDirectory,
        Func<string, string> hashFile,
        IUnifiedLogger? logger)
    {
        _civitaiClient = civitaiClient;
        _apiKeyProvider = apiKeyProvider;
        _cacheDirectory = cacheDirectory;
        _hashFile = hashFile;
        _logger = logger;
    }

    public static string DefaultCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "SorterCache");

    /// <summary>Resolution chain per spec §3: local .civitai.info sidecar → per-hash disk
    /// cache → Civitai by-hash API (result cached). Never throws for a 404/offline —
    /// returns metadata with null BaseModelRaw so the file sorts into Unknown.</summary>
    public async Task<ResolvedLoraMetadata> ResolveAsync(string filePath, CancellationToken ct = default)
    {
        if (TryReadSidecar(filePath, out var sidecarMeta))
            return sidecarMeta!;

        var sha = _hashFile(filePath);

        if (TryReadCache(sha, out var cached))
            return cached! with { Sha256 = sha };

        if (_civitaiClient is null)
            return new ResolvedLoraMetadata(null, null, sha);

        CivitaiModelVersion? version;
        try
        {
            version = await _civitaiClient.GetModelVersionByHashAsync(sha, await _apiKeyProvider(), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.Warn(LogCategory.Network, LogSource,
                $"Civitai by-hash lookup failed for {filePath}: {ex.Message}");
            return new ResolvedLoraMetadata(null, null, sha);
        }

        if (version is null)
        {
            WriteCache(sha, new CacheEntry(null, null));
            return new ResolvedLoraMetadata(null, null, sha);
        }

        WriteCache(sha, new CacheEntry(version.BaseModel, version.Id));
        return new ResolvedLoraMetadata(version.BaseModel, version.Id, sha);
    }

    private bool TryReadSidecar(string filePath, out ResolvedLoraMetadata? metadata)
    {
        metadata = null;
        var sidecarPath = Path.Combine(
            Path.GetDirectoryName(filePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(filePath) + ".civitai.info");

        if (!File.Exists(sidecarPath))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecarPath));
            var root = doc.RootElement;
            string? baseModel = root.TryGetProperty("baseModel", out var bmProp) && bmProp.ValueKind == JsonValueKind.String
                ? bmProp.GetString()
                : null;
            int? id = root.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt32()
                : null;

            metadata = new ResolvedLoraMetadata(baseModel, id, Sha256: "");
            return true;
        }
        catch (JsonException ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Malformed .civitai.info sidecar at {sidecarPath}: {ex.Message}");
            return false;
        }
    }

    private bool TryReadCache(string sha, out ResolvedLoraMetadata? metadata)
    {
        metadata = null;
        var cachePath = Path.Combine(_cacheDirectory, $"{sha}.json");
        if (!File.Exists(cachePath))
            return false;

        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(cachePath), CacheJsonOptions);
            if (entry is null)
                throw new JsonException("Deserialized to null.");

            metadata = new ResolvedLoraMetadata(entry.BaseModel, entry.VersionId, sha);
            return true;
        }
        catch (JsonException ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Malformed cache entry at {cachePath}, discarding: {ex.Message}");
            try { File.Delete(cachePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    private void WriteCache(string sha, CacheEntry entry)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var cachePath = Path.Combine(_cacheDirectory, $"{sha}.json");
        File.WriteAllText(cachePath, JsonSerializer.Serialize(entry, CacheJsonOptions));
    }

    private sealed record CacheEntry(string? BaseModel, int? VersionId);
}
