using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>Metadata resolved for one on-disk file outside the DB.</summary>
public sealed record ResolvedLoraMetadata(string? BaseModelRaw, int? CivitaiVersionId, string Sha256)
{
    /// <summary>
    /// Tag names carried by the file's <c>.civitai.info</c> sidecar (<c>model.tags</c> or a
    /// top-level <c>tags</c> array), empty when it has none or the metadata came from the
    /// by-hash API — that endpoint returns no tags. Callers feed these to
    /// <see cref="SorterCategoryResolver"/> so a properly downloaded LoRA in a browsed
    /// folder can still land in its category folder instead of being forced to Unknown.
    /// </summary>
    public IReadOnlyList<string> Tags { get; init; } = [];
}

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

    /// <summary>Memoized API key read — see <see cref="ResetApiKeyCache"/>.</summary>
    private Task<string?>? _apiKeyTask;

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

    /// <summary>
    /// Drops the memoized API key so the next resolution pass re-reads it. Callers that
    /// keep a resolver alive across passes (the LoRA Sorter view model does) must call
    /// this once per pass, so a key the user changed in Settings meanwhile is picked up.
    /// </summary>
    public void ResetApiKeyCache() => _apiKeyTask = null;

    /// <summary>Resolution chain per spec §3: local .civitai.info sidecar → per-hash disk
    /// cache → Civitai by-hash API (result cached). Never throws for a 404/offline, an
    /// unreadable file or a Civitai shape change — returns metadata with null
    /// BaseModelRaw so the file sorts into Unknown.</summary>
    public async Task<ResolvedLoraMetadata> ResolveAsync(string filePath, CancellationToken ct = default)
    {
        if (TryReadSidecar(filePath, out var sidecarMeta))
            return sidecarMeta!;

        string sha;
        try
        {
            sha = _hashFile(filePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A .safetensors currently open in ComfyUI/A1111, a denied ACL, or a file that
            // vanished between enumeration and use. One bad file out of 3000 must not kill
            // the pass — it just sorts as unresolved.
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not hash {filePath}: {ex.Message}");
            return new ResolvedLoraMetadata(null, null, string.Empty);
        }

        if (TryReadCache(sha, out var cached))
            return cached! with { Sha256 = sha };

        if (_civitaiClient is null)
            return new ResolvedLoraMetadata(null, null, sha);

        CivitaiModelVersion? version;
        try
        {
            version = await _civitaiClient.GetModelVersionByHashAsync(sha, await GetApiKeyAsync(), ct);
        }
        // JsonException: CivitaiClient.DeserializeOrThrow raises it on a response-shape
        // change, and this repo has been bitten by exactly that twice (allowCommercialUse
        // string→array, null stats counters). A shape change must degrade to "unresolved",
        // not abort a 3000-file preview.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
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

    /// <summary>
    /// One key read per resolution pass, not per file: in production the provider opens a
    /// DI scope, resolves IAppSettingsService and queries the DB, so a folder of 1000
    /// unresolved LoRAs meant 1000 scopes, 1000 DbContexts and 1000 queries for a value
    /// that cannot change mid-pass. The task itself is cached, so overlapping callers share
    /// the single read; it never faults, because a missing key just means an anonymous
    /// by-hash request.
    /// </summary>
    private Task<string?> GetApiKeyAsync() => _apiKeyTask ??= LoadApiKeyAsync();

    private async Task<string?> LoadApiKeyAsync()
    {
        try
        {
            return await _apiKeyProvider();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.Network, LogSource,
                $"Could not read the Civitai API key; continuing unauthenticated: {ex.Message}");
            return null;
        }
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

            // Valid JSON with neither field present/non-null isn't a usable hit —
            // fall through to hash/cache/API instead of returning an empty result.
            if (baseModel is null && id is null)
                return false;

            metadata = new ResolvedLoraMetadata(baseModel, id, Sha256: "") { Tags = ReadTags(root) };
            return true;
        }
        catch (JsonException ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Malformed .civitai.info sidecar at {sidecarPath}: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not read .civitai.info sidecar at {sidecarPath}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Tag names from a <c>.civitai.info</c> document: <c>model.tags</c> (what the download
    /// pipeline writes) or a top-level <c>tags</c> array. Entries may be plain strings or
    /// objects carrying a <c>name</c> — both shapes occur in the wild.
    /// </summary>
    private static IReadOnlyList<string> ReadTags(JsonElement root)
    {
        if (root.TryGetProperty("model", out var model)
            && model.ValueKind == JsonValueKind.Object
            && model.TryGetProperty("tags", out var modelTags)
            && modelTags.ValueKind == JsonValueKind.Array)
        {
            return ToTagNames(modelTags);
        }

        return root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? ToTagNames(tags)
            : [];
    }

    private static IReadOnlyList<string> ToTagNames(JsonElement array)
    {
        var names = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            var name = item.ValueKind switch
            {
                JsonValueKind.String => item.GetString(),
                JsonValueKind.Object when item.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String => n.GetString(),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
        }
        return names;
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not read cache entry at {cachePath}: {ex.Message}");
            return false;
        }
    }

    private void WriteCache(string sha, CacheEntry entry)
    {
        var cachePath = Path.Combine(_cacheDirectory, $"{sha}.json");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            File.WriteAllText(cachePath, JsonSerializer.Serialize(entry, CacheJsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not write cache entry at {cachePath}: {ex.Message}");
        }
    }

    private sealed record CacheEntry(string? BaseModel, int? VersionId);
}
