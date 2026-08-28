using System.Net.Http.Headers;
using System.Text.Json;
using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.Civitai;

/// <summary>
/// HTTP client implementation for the Civitai REST API.
/// </summary>
/// <remarks>
/// Thread-safe and designed to be used as a singleton with HttpClientFactory.
/// </remarks>
public sealed class CivitaiClient : ICivitaiClient, IDisposable
{
    private const string BaseUrl = "https://civitai.com/api/v1/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Tolerant enum deserialization: Civitai periodically adds new values
        // (e.g. fp8 / nf4 / e4m3 on CivitaiFloatingPoint) and the default enum
        // converter throws on unknown strings, killing the entire response.
        // With this factory unknown values fall back to default(T) so the rest
        // of the page still deserializes.
        Converters = { new Models.TolerantEnumConverterFactory() }
    };

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly ICivitaiRateLimitObserver? _rateLimitObserver;

    /// <summary>
    /// Creates a new CivitaiClient with a default HttpClient.
    /// </summary>
    public CivitaiClient() : this(new HttpClient(), disposeHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a new CivitaiClient with a provided HttpClient.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use.</param>
    /// <param name="disposeHttpClient">Whether to dispose the HttpClient when this client is disposed.</param>
    /// <param name="rateLimitObserver">Told about every 429, including recovered ones.</param>
    public CivitaiClient(
        HttpClient httpClient,
        bool disposeHttpClient = false,
        ICivitaiRateLimitObserver? rateLimitObserver = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeHttpClient = disposeHttpClient;
        _rateLimitObserver = rateLimitObserver;

        // HttpClient throws InvalidOperationException if BaseAddress or DefaultRequestHeaders
        // are mutated after the first request has been sent. That happens when this ctor
        // runs more than once against the same instance (DI quirks, design-time activation,
        // or a caller passing in a pre-used client). Guard each mutation independently.
        TryConfigureBaseAddress(_httpClient);
        TryAddJsonAcceptHeader(_httpClient);
    }

    private static void TryConfigureBaseAddress(HttpClient client)
    {
        if (client.BaseAddress is not null) return;

        try
        {
            client.BaseAddress = new Uri(BaseUrl);
        }
        catch (InvalidOperationException)
        {
            // Client has already been used — caller is responsible for the BaseAddress.
        }
    }

    private static void TryAddJsonAcceptHeader(HttpClient client)
    {
        // Don't double-add if Accept already includes application/json.
        foreach (var existing in client.DefaultRequestHeaders.Accept)
        {
            if (string.Equals(existing.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        try
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }
        catch (InvalidOperationException)
        {
            // Client has already been used — header negotiation falls back to per-request Accept.
        }
    }

    #region Models

    public async Task<CivitaiPagedResponse<CivitaiModel>> GetModelsAsync(
        CivitaiModelsQuery? query = null,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var queryString = query?.ToQueryString() ?? string.Empty;
        var url = string.IsNullOrEmpty(queryString) ? "models" : $"models?{queryString}";

        return await GetAsync<CivitaiPagedResponse<CivitaiModel>>(url, apiKey, cancellationToken)
               ?? new CivitaiPagedResponse<CivitaiModel>();
    }

    public async Task<CivitaiModel?> GetModelAsync(
        int modelId,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        return await GetAsync<CivitaiModel>($"models/{modelId}", apiKey, cancellationToken);
    }

    #endregion

    #region Model Versions

    public async Task<CivitaiModelVersion?> GetModelVersionAsync(
        int modelVersionId,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        return await GetAsync<CivitaiModelVersion>($"model-versions/{modelVersionId}", apiKey, cancellationToken);
    }

    public async Task<CivitaiModelVersion?> GetModelVersionByHashAsync(
        string hash,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        return await GetAsync<CivitaiModelVersion>($"model-versions/by-hash/{hash}", apiKey, cancellationToken);
    }

    #endregion

    #region Images

    public async Task<CivitaiPagedResponse<CivitaiModelImage>> GetImagesAsync(
        CivitaiImagesQuery? query = null,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        var queryString = query?.ToQueryString() ?? string.Empty;
        var url = string.IsNullOrEmpty(queryString) ? "images" : $"images?{queryString}";

        return await GetAsync<CivitaiPagedResponse<CivitaiModelImage>>(url, apiKey, cancellationToken)
               ?? new CivitaiPagedResponse<CivitaiModelImage>();
    }

    #endregion

    #region Tags

    public async Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(
        int? limit = null,
        int? page = null,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (limit.HasValue) parts.Add($"limit={limit.Value}");
        if (page.HasValue) parts.Add($"page={page.Value}");
        if (!string.IsNullOrWhiteSpace(query)) parts.Add($"query={Uri.EscapeDataString(query)}");

        var queryString = parts.Count > 0 ? string.Join("&", parts) : string.Empty;
        var url = string.IsNullOrEmpty(queryString) ? "tags" : $"tags?{queryString}";

        return await GetAsync<CivitaiPagedResponse<CivitaiTag>>(url, null, cancellationToken)
               ?? new CivitaiPagedResponse<CivitaiTag>();
    }

    #endregion

    #region Creators

    public async Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(
        int? limit = null,
        int? page = null,
        string? query = null,
        CancellationToken cancellationToken = default)
    {
        var parts = new List<string>();
        if (limit.HasValue) parts.Add($"limit={limit.Value}");
        if (page.HasValue) parts.Add($"page={page.Value}");
        if (!string.IsNullOrWhiteSpace(query)) parts.Add($"query={Uri.EscapeDataString(query)}");

        var queryString = parts.Count > 0 ? string.Join("&", parts) : string.Empty;
        var url = string.IsNullOrEmpty(queryString) ? "creators" : $"creators?{queryString}";

        return await GetAsync<CivitaiPagedResponse<CivitaiCreatorInfo>>(url, null, cancellationToken)
               ?? new CivitaiPagedResponse<CivitaiCreatorInfo>();
    }

    #endregion

    #region HTTP Helpers

    /// <summary>
    /// Test seam: replaces the retry backoff so tests exercise the retry loop without
    /// sleeping. Receives the zero-based attempt number.
    /// </summary>
    internal Func<int, TimeSpan>? RetryDelayOverride { get; set; }

    /// <summary>
    /// Backoff before re-issuing a request that failed for a reason the next attempt
    /// might not hit: 1s, 2s, 4s. Deliberately shorter than the rate-limit backoff —
    /// a 502 from the CDN is not a quota, and callers are often waiting on a download.
    /// </summary>
    private TimeSpan TransientDelay(int attempt)
        => RetryDelayOverride?.Invoke(attempt) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));

    private TimeSpan RateLimitDelay(int attempt, TimeSpan? retryAfter)
        => RetryDelayOverride?.Invoke(attempt) ?? retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) * 5);

    /// <summary>
    /// Retry-After in either legal form. The delta form ("120") arrives pre-parsed as
    /// <c>Delta</c>; the HTTP-date form ("Wed, 21 Oct 2026 07:28:00 GMT") only ever populates
    /// <c>Date</c>, and reading Delta alone — which is what this client used to do — silently
    /// discarded it and fell back to a blind backoff.
    /// </summary>
    private static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    /// <summary>
    /// 5xx responses are the server having a moment, not an answer about the resource —
    /// Civitai fronts its API with a CDN that emits 502/503/504 under load. Everything
    /// else in the 4xx range is a real answer and is never retried.
    /// </summary>
    private static bool IsTransientStatus(System.Net.HttpStatusCode status)
        => (int)status >= 500;

    private async Task<T?> GetAsync<T>(string endpoint, string? apiKey, CancellationToken cancellationToken)
        where T : class
    {
        const int maxRetries = 3;

        // One immediate retry, not three. A 429 is a quota, and the gateway's shared cooldown is
        // what waits it out for everybody; three in-call retries meant a single call could sleep
        // 10 + 20 + 40 s while holding a download slot.
        const int maxRateLimitRetries = 1;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                // Build an absolute URL so we work regardless of whether BaseAddress
                // was successfully assigned (see TryConfigureBaseAddress).
                var requestUri = _httpClient.BaseAddress is null
                    ? new Uri(BaseUrl + endpoint)
                    : new Uri(_httpClient.BaseAddress, endpoint);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);

                if (!string.IsNullOrWhiteSpace(apiKey))
                {
                    // Civitai expects the "ApiKey" scheme, not "Bearer"
                    request.Headers.TryAddWithoutValidation("Authorization", $"ApiKey {apiKey}");
                }

                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }

                // Handle rate limiting with automatic retry
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var serverRetryAfter = ParseRetryAfter(response);

                    // Reported before the decision to retry: every other surface should stop
                    // now, not after this caller has finished being optimistic.
                    _rateLimitObserver?.OnRateLimited(serverRetryAfter);

                    if (attempt >= maxRateLimitRetries)
                    {
                        throw new CivitaiRateLimitedException(
                            $"Civitai API rate limited after {maxRateLimitRetries} retries for {endpoint}",
                            serverRetryAfter);
                    }

                    await Task.Delay(RateLimitDelay(attempt, serverRetryAfter), cancellationToken);
                    continue;
                }

                // Transient server-side failure: retry before giving up. Callers persist
                // freshly downloaded metadata off the back of this call, and a single 502
                // used to cost that model its entire metadata record.
                if (IsTransientStatus(response.StatusCode) && attempt < maxRetries)
                {
                    await Task.Delay(TransientDelay(attempt), cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                    throw new HttpRequestException(
                        $"Civitai API {(int)response.StatusCode} for {endpoint}: {errorBody}",
                        null,
                        response.StatusCode);
                }

                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return DeserializeOrThrow<T>(responseBody, endpoint);
            }
            // Connection reset / DNS blip / TLS failure: HttpRequestException with no
            // StatusCode. The ones this method throws itself always carry a status, so
            // this filter cannot swallow a real API error.
            catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < maxRetries)
            {
                await Task.Delay(TransientDelay(attempt), cancellationToken);
            }
            // HttpClient's own timeout surfaces as a cancellation with OUR token untouched.
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < maxRetries)
            {
                await Task.Delay(TransientDelay(attempt), cancellationToken);
            }
            finally
            {
                response?.Dispose();
            }
        }

        // Only reachable when the transient-retry budget is exhausted without a decisive answer.
        throw new HttpRequestException(
            $"Civitai API gave no usable response after {maxRetries} retries for {endpoint}",
            null,
            System.Net.HttpStatusCode.ServiceUnavailable);
    }

    /// <summary>
    /// Deserializes a successful response, or throws a <see cref="JsonException"/> carrying
    /// the offending payload. Never retried — a shape change does not fix itself.
    /// </summary>
    private static T? DeserializeOrThrow<T>(string responseBody, string endpoint)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(responseBody, JsonOptions);
        }
        catch (JsonException ex)
        {
            // Civitai periodically changes response shapes (see TolerantEnumConverterFactory
            // above); when that breaks deserialization of a field this client isn't tolerant
            // of yet, keep the offending payload so the failure is diagnosable from logs alone.
            const int maxBodyLength = 4000;
            string snippet;
            if (responseBody.Length > maxBodyLength)
            {
                // Back off one char when the cut would land between a surrogate pair —
                // model names/descriptions carry emoji, and slicing a pair in half leaves
                // a lone surrogate (an ill-formed string) in the diagnostic message.
                var cut = char.IsHighSurrogate(responseBody[maxBodyLength - 1])
                    ? maxBodyLength - 1
                    : maxBodyLength;
                snippet = responseBody[..cut] + "... (truncated)";
            }
            else
            {
                snippet = responseBody;
            }
            throw new JsonException(
                $"Failed to deserialize Civitai response for {endpoint} as {typeof(T).Name}: {ex.Message} Raw response: {snippet}",
                ex);
        }
    }

    #endregion

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }
}
