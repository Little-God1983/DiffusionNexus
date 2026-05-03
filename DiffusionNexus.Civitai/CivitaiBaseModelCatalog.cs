using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Default <see cref="ICivitaiBaseModelCatalog"/> implementation.
/// </summary>
/// <remarks>
/// Source priority: in-memory cache → on-disk cache (TTL) → live fetch from the
/// civitai/civitai GitHub repo → bundled fallback. The live fetch tries
/// <c>src/shared/constants/basemodel.constants.ts</c> first (new layout, parses
/// <c>baseModelRecords</c>) and then falls back to the legacy
/// <c>src/server/common/constants.ts</c> (parses the flat <c>baseModels</c>
/// array or the <c>baseModelLicenses</c> Record keys). Because file paths and
/// shapes can change upstream, the catalog is intentionally defensive and
/// silently falls back to the bundled snapshot on any error.
/// TODO: Linux Implementation for Task X — the cache directory uses
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which already
/// resolves correctly on Linux/macOS, but the bundled snapshot is the only
/// guaranteed-offline source on those platforms today.
/// </remarks>
public sealed class CivitaiBaseModelCatalog : ICivitaiBaseModelCatalog
{
    /// <summary>
    /// Candidate raw-GitHub URLs to try, in order. Civitai split the base-model
    /// definitions out of <c>src/server/common/constants.ts</c> in 2026 into a
    /// dedicated <c>src/shared/constants/basemodel.constants.ts</c>; we try the
    /// new path first and fall back to the legacy file for older branches/forks.
    /// </summary>
    private static readonly string[] CandidateUrls =
    {
        "https://raw.githubusercontent.com/civitai/civitai/main/src/shared/constants/basemodel.constants.ts",
        "https://raw.githubusercontent.com/civitai/civitai/main/src/server/common/constants.ts",
    };

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// Bundled snapshot of Civitai's base model labels (Option 3 fallback). Kept
    /// in sync with <c>baseModelRecords</c> in
    /// <c>src/shared/constants/basemodel.constants.ts</c> as of 2026-05.
    /// Order roughly matches the on-site dropdown so the UI feels familiar.
    /// </summary>
    private static readonly IReadOnlyList<string> BundledSnapshot = new[]
    {
        "Anima",
        "AuraFlow",
        "Chroma",
        "CogVideoX",
        "Ernie",
        "Flux.1 S",
        "Flux.1 D",
        "Flux.1 Krea",
        "Flux.1 Kontext",
        "Flux.2 D",
        "Flux.2 Klein 9B",
        "Flux.2 Klein 9B-base",
        "Flux.2 Klein 4B",
        "Flux.2 Klein 4B-base",
        "Grok",
        "HappyHorse",
        "HiDream",
        "Hunyuan 1",
        "Hunyuan Video",
        "Illustrious",
        "Imagen4",
        "Kolors",
        "LTXV",
        "LTXV2",
        "LTXV 2.3",
        "Lumina",
        "Mochi",
        "Nano Banana",
        "NoobAI",
        "ODOR",
        "OpenAI",
        "Other",
        "PixArt a",
        "PixArt E",
        "Playground v2",
        "Pony",
        "Pony V7",
        "Qwen",
        "Qwen 2",
        "Stable Cascade",
        "SD 1.4",
        "SD 1.5",
        "SD 1.5 LCM",
        "SD 1.5 Hyper",
        "SD 2.0",
        "SD 2.0 768",
        "SD 2.1",
        "SD 2.1 768",
        "SD 2.1 Unclip",
        "SD 3",
        "SD 3.5",
        "SD 3.5 Large",
        "SD 3.5 Large Turbo",
        "SD 3.5 Medium",
        "SDXL 0.9",
        "SDXL 1.0",
        "SDXL 1.0 LCM",
        "SDXL Distilled",
        "SDXL Hyper",
        "SDXL Lightning",
        "SDXL Turbo",
        "Seedream",
        "Seedance",
        "Sora 2",
        "SVD",
        "SVD XT",
        "Veo 3",
        "Vidu Q1",
        "Hailuo by MiniMax",
        "Kling",
        "Wan Video",
        "Wan Video 1.3B t2v",
        "Wan Video 14B t2v",
        "Wan Video 14B i2v 480p",
        "Wan Video 14B i2v 720p",
        "Wan Video 2.2 TI2V-5B",
        "Wan Video 2.2 T2V-A14B",
        "Wan Video 2.2 I2V-A14B",
        "Wan Video 2.5 T2V",
        "Wan Video 2.5 I2V",
        "Wan Image 2.7",
        "Wan Video 2.7",
        "ZImageTurbo",
        "ZImageBase",
        "ACE Audio",
        "Upscaler",
    };

    // Legacy: matches `baseModels = [ '...', "...", ... ] as const;` (or `baseModels: [ ... ]`)
    private static readonly Regex BaseModelsArrayRegex = new(
        @"baseModels\s*[:=]\s*\[(?<body>[^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // New (2026): matches the body of `baseModelRecords: BaseModelRecord[] = [ ... ];`
    // We then harvest each record's `name: '...'` literal.
    private static readonly Regex BaseModelRecordsBlockRegex = new(
        @"baseModelRecords\s*:\s*BaseModelRecord\[\]\s*=\s*\[(?<body>.*?)\]\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex RecordNameRegex = new(
        @"name\s*:\s*(?:'(?<v>[^']+)'|""(?<v>[^""]+)"")",
        RegexOptions.Compiled);

    // Fallback (legacy `constants.ts`): harvest keys of
    // `baseModelLicenses: Record<BaseModel, ...> = { 'SD 1.4': ..., ... };`
    private static readonly Regex BaseModelLicensesBlockRegex = new(
        @"baseModelLicenses\s*:\s*Record[^=]*=\s*\{(?<body>.*?)\}\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    // Object-literal keys: `'SD 1.5':`, `"Flux.1 D":`, or bare `Pony:` / `SVD:` / `Flux2Klein_4B:`.
    private static readonly Regex RecordKeyRegex = new(
        @"(?:^|,|\{)\s*(?:'(?<v>[^']+)'|""(?<v>[^""]+)""|(?<v>[A-Za-z_][\w]*))\s*:",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex StringLiteralRegex = new(
        "\"(?<v>[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*)\"|'(?<v>[^'\\\\]*(?:\\\\.[^'\\\\]*)*)'",
        RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IReadOnlyList<string>? _memoryCache;

    /// <inheritdoc/>
    public string CacheFilePath => _cacheFilePath;

    /// <inheritdoc/>
    public DateTime? CacheTimestampUtc
    {
        get
        {
            try
            {
                if (!File.Exists(_cacheFilePath)) return null;
                return new FileInfo(_cacheFilePath).LastWriteTimeUtc;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <inheritdoc/>
    public event EventHandler<CivitaiBaseModelCatalogEventArgs>? StatusChanged;

    private void Raise(CivitaiBaseModelCatalogEventKind kind, int count = 0, string? message = null, Exception? ex = null)
    {
        try
        {
            StatusChanged?.Invoke(this, new CivitaiBaseModelCatalogEventArgs
            {
                Kind = kind,
                Count = count,
                Message = message,
                Exception = ex,
                CacheTimestampUtc = CacheTimestampUtc,
            });
        }
        catch
        {
            // Subscriber failures must never break the catalog.
        }
    }

    public CivitaiBaseModelCatalog()
        : this(httpClient: null, cacheDirectory: null)
    {
    }

    public CivitaiBaseModelCatalog(HttpClient? httpClient, string? cacheDirectory = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            _ownsHttpClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }

        cacheDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus",
            "Cache");
        _cacheFilePath = Path.Combine(cacheDirectory, "civitai-base-models.json");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetBaseModelsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh && _memoryCache is { Count: > 0 } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _memoryCache is { Count: > 0 } cached2)
            {
                return cached2;
            }

            // 1) On-disk cache (skip when a refresh is forced)
            if (!forceRefresh && TryReadDiskCache(out var diskList))
            {
                _memoryCache = diskList;
                Raise(CivitaiBaseModelCatalogEventKind.UsedDiskCache, diskList.Count,
                    $"Loaded {diskList.Count} base models from disk cache.");
                return diskList;
            }

            // 2) Live fetch from GitHub (try each candidate URL in order)
            Raise(CivitaiBaseModelCatalogEventKind.FetchStarted, 0,
                $"Fetching Civitai base model list from {CandidateUrls[0]}");
            try
            {
                IReadOnlyList<string> fetched = Array.Empty<string>();
                Exception? lastError = null;

                foreach (var url in CandidateUrls)
                {
                    try
                    {
                        var fromUrl = await FetchFromGitHubAsync(url, cancellationToken).ConfigureAwait(false);
                        if (fromUrl is { Count: > 0 })
                        {
                            fetched = fromUrl;
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = ex;
                    }
                }

                if (fetched is { Count: > 0 })
                {
                    TryWriteDiskCache(fetched);
                    _memoryCache = fetched;
                    Raise(CivitaiBaseModelCatalogEventKind.FetchSucceeded, fetched.Count,
                        $"Fetched {fetched.Count} base models from GitHub.");
                    return fetched;
                }

                Raise(CivitaiBaseModelCatalogEventKind.FetchFailed, 0,
                    lastError is null
                        ? "GitHub fetch returned no parseable base models."
                        : $"GitHub fetch failed: {lastError.Message}",
                    lastError);
            }
            catch (Exception ex)
            {
                Raise(CivitaiBaseModelCatalogEventKind.FetchFailed, 0,
                    $"GitHub fetch failed: {ex.Message}", ex);
            }

            // 3) Bundled fallback (always available)
            _memoryCache = BundledSnapshot;
            Raise(CivitaiBaseModelCatalogEventKind.UsedBundledFallback, BundledSnapshot.Count,
                $"Using bundled snapshot ({BundledSnapshot.Count} base models).");
            return BundledSnapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryReadDiskCache(out IReadOnlyList<string> result)
    {
        result = Array.Empty<string>();
        try
        {
            if (!File.Exists(_cacheFilePath)) return false;

            var info = new FileInfo(_cacheFilePath);
            if (DateTime.UtcNow - info.LastWriteTimeUtc > CacheTtl) return false;

            using var stream = File.OpenRead(_cacheFilePath);
            var payload = JsonSerializer.Deserialize<DiskCachePayload>(stream, JsonOptions);
            if (payload?.BaseModels is { Length: > 0 } arr)
            {
                result = arr;
                return true;
            }
        }
        catch
        {
            // Treat any cache read failure as a miss.
        }
        return false;
    }

    private void TryWriteDiskCache(IReadOnlyList<string> values)
    {
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var payload = new DiskCachePayload
            {
                FetchedAtUtc = DateTime.UtcNow,
                BaseModels = values.ToArray(),
            };

            using var stream = File.Create(_cacheFilePath);
            JsonSerializer.Serialize(stream, payload, JsonOptions);
        }
        catch
        {
            // Cache writes are best-effort.
        }
    }

    private async Task<IReadOnlyList<string>> FetchFromGitHubAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub raw works without auth; a UA header is courteous and avoids odd 403s.
        request.Headers.UserAgent.ParseAdd("DiffusionNexus/1.0 (+https://github.com/Little-God1983/DiffusionNexus)");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseBaseModels(text);
    }

    /// <summary>
    /// Extracts base-model labels from a Civitai TypeScript constants file.
    /// Supports three layouts (tried in order):
    ///   1. The new <c>baseModelRecords: BaseModelRecord[] = [ ... ]</c> in
    ///      <c>src/shared/constants/basemodel.constants.ts</c> — harvest each
    ///      record's <c>name: '...'</c> literal.
    ///   2. The legacy flat <c>baseModels = [ '...', ... ]</c> array.
    ///   3. The legacy <c>baseModelLicenses: Record&lt;BaseModel, ...&gt;</c>
    ///      object — harvest its keys (works on the still-existing
    ///      <c>src/server/common/constants.ts</c>).
    /// Returns an empty list when none of the strategies match.
    /// </summary>
    internal static IReadOnlyList<string> ParseBaseModels(string typescriptSource)
    {
        if (string.IsNullOrWhiteSpace(typescriptSource)) return Array.Empty<string>();

        // Strategy 1: baseModelRecords (new file)
        var recordsMatch = BaseModelRecordsBlockRegex.Match(typescriptSource);
        if (recordsMatch.Success)
        {
            var harvested = HarvestUnique(RecordNameRegex.Matches(recordsMatch.Groups["body"].Value));
            if (harvested.Count > 0) return harvested;
        }

        // Strategy 2: flat baseModels array (legacy)
        var arrayMatch = BaseModelsArrayRegex.Match(typescriptSource);
        if (arrayMatch.Success)
        {
            var harvested = HarvestUnique(StringLiteralRegex.Matches(arrayMatch.Groups["body"].Value));
            if (harvested.Count > 0) return harvested;
        }

        // Strategy 3: baseModelLicenses Record keys (legacy fallback)
        var licensesMatch = BaseModelLicensesBlockRegex.Match(typescriptSource);
        if (licensesMatch.Success)
        {
            var harvested = HarvestUnique(RecordKeyRegex.Matches(licensesMatch.Groups["body"].Value));
            if (harvested.Count > 0) return harvested;
        }

        return Array.Empty<string>();
    }

    private static IReadOnlyList<string> HarvestUnique(MatchCollection matches)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();
        foreach (Match m in matches)
        {
            var v = m.Groups["v"].Value.Trim();
            if (v.Length == 0) continue;
            if (seen.Add(v)) values.Add(v);
        }
        return values;
    }

    private sealed class DiskCachePayload
    {
        public DateTime FetchedAtUtc { get; set; }
        public string[] BaseModels { get; set; } = Array.Empty<string>();
    }

    /// <summary>
    /// Releases the owned <see cref="HttpClient"/> if one was created internally.
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        _gate.Dispose();
    }
}
