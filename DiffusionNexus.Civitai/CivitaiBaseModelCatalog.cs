using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Default <see cref="ICivitaiBaseModelCatalog"/> implementation.
/// </summary>
/// <remarks>
/// Source priority: in-memory cache → on-disk cache (TTL) → live fetch from the
/// civitai/civitai GitHub repo → bundled fallback. The live fetch parses the
/// <c>baseModels</c> string array out of <c>src/server/common/constants.ts</c>;
/// because that file's path/shape can change, the catalog is intentionally
/// defensive and silently falls back to the bundled snapshot on any error.
/// TODO: Linux Implementation for Task X — the cache directory uses
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, which already
/// resolves correctly on Linux/macOS, but the bundled snapshot is the only
/// guaranteed-offline source on those platforms today.
/// </remarks>
public sealed class CivitaiBaseModelCatalog : ICivitaiBaseModelCatalog
{
    private const string ConstantsUrl =
        "https://raw.githubusercontent.com/civitai/civitai/main/src/server/common/constants.ts";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// Bundled snapshot of Civitai's base model labels (Option 3 fallback). Kept
    /// in sync with the names exposed by Civitai's filter dropdown as of 2025-01.
    /// Order roughly matches the on-site dropdown so the UI feels familiar.
    /// </summary>
    private static readonly IReadOnlyList<string> BundledSnapshot = new[]
    {
        "Anima",
        "AuraFlow",
        "Chroma",
        "CogVideoX",
        "Flux.1 D",
        "Flux.1 S",
        "Flux.1 Kontext",
        "Hunyuan 1",
        "Hunyuan Video",
        "Illustrious",
        "Lumina",
        "Mochi",
        "NoobAI",
        "ODOR",
        "Other",
        "PixArt a",
        "PixArt E",
        "Playground v2",
        "Pony",
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
        "Stable Cascade",
        "SVD",
        "SVD XT",
        "Wan Video 1.3B t2v",
        "Wan Video 14B t2v",
        "Wan Video 14B i2v 480p",
        "Wan Video 14B i2v 720p",
        "Wan Video 2.2 TI2V-5B",
        "Wan Video 2.2 T2V-A14B",
        "Wan Video 2.2 I2V-A14B",
    };

    // Matches:  baseModels = [ '...', "...", ... ] as const;   (or  baseModels: [ ... ])
    private static readonly Regex BaseModelsArrayRegex = new(
        @"baseModels\s*[:=]\s*\[(?<body>[^\]]*)\]",
        RegexOptions.Compiled | RegexOptions.Singleline);

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
                return diskList;
            }

            // 2) Live fetch from GitHub
            try
            {
                var fetched = await FetchFromGitHubAsync(cancellationToken).ConfigureAwait(false);
                if (fetched is { Count: > 0 })
                {
                    TryWriteDiskCache(fetched);
                    _memoryCache = fetched;
                    return fetched;
                }
            }
            catch (Exception)
            {
                // Network/parse failures fall through to the bundled snapshot.
            }

            // 3) Bundled fallback (always available)
            _memoryCache = BundledSnapshot;
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

    private async Task<IReadOnlyList<string>> FetchFromGitHubAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ConstantsUrl);
        // GitHub raw works without auth; a UA header is courteous and avoids odd 403s.
        request.Headers.UserAgent.ParseAdd("DiffusionNexus/1.0 (+https://github.com/Little-God1983/DiffusionNexus)");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParseBaseModels(text);
    }

    /// <summary>
    /// Extracts the <c>baseModels</c> string array from a TypeScript constants
    /// file. Returns an empty list when the array can't be located or parsed.
    /// </summary>
    internal static IReadOnlyList<string> ParseBaseModels(string typescriptSource)
    {
        if (string.IsNullOrWhiteSpace(typescriptSource)) return Array.Empty<string>();

        var match = BaseModelsArrayRegex.Match(typescriptSource);
        if (!match.Success) return Array.Empty<string>();

        var body = match.Groups["body"].Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var values = new List<string>();
        foreach (Match m in StringLiteralRegex.Matches(body))
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
