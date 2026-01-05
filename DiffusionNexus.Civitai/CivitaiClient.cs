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
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

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
    public CivitaiClient(HttpClient httpClient, bool disposeHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeHttpClient = disposeHttpClient;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(BaseUrl);
        }

        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
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

    private async Task<T?> GetAsync<T>(string endpoint, string? apiKey, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
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
