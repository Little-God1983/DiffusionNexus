using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.Civitai;

/// <summary>
/// Client interface for communicating with the Civitai REST API.
/// Provides strongly-typed methods that return deserialized DTOs.
/// </summary>
/// <remarks>
/// This interface is designed for dependency injection and testability.
/// All methods support cancellation and optionally accept an API key for authenticated requests.
/// </remarks>
public interface ICivitaiClient
{
    #region Models

    /// <summary>
    /// Gets a paginated list of models.
    /// </summary>
    /// <param name="query">Query parameters for filtering and pagination.</param>
    /// <param name="apiKey">Optional API key for authenticated requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paginated response containing models.</returns>
    Task<CivitaiPagedResponse<CivitaiModel>> GetModelsAsync(
        CivitaiModelsQuery? query = null,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a single model by its ID.
    /// </summary>
    /// <param name="modelId">The model ID.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model with all its versions.</returns>
    Task<CivitaiModel?> GetModelAsync(
        int modelId,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    #endregion

    #region Model Versions

    /// <summary>
    /// Gets a model version by its ID.
    /// </summary>
    /// <param name="modelVersionId">The model version ID.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model version.</returns>
    Task<CivitaiModelVersion?> GetModelVersionAsync(
        int modelVersionId,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a model version by file hash.
    /// Supports: AutoV1, AutoV2, SHA256, CRC32, BLAKE3.
    /// </summary>
    /// <param name="hash">The file hash.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The model version, or null if not found.</returns>
    Task<CivitaiModelVersion?> GetModelVersionByHashAsync(
        string hash,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    #endregion

    #region Images

    /// <summary>
    /// Gets a paginated list of images.
    /// </summary>
    /// <param name="query">Query parameters.</param>
    /// <param name="apiKey">Optional API key.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paginated response containing images.</returns>
    Task<CivitaiPagedResponse<CivitaiModelImage>> GetImagesAsync(
        CivitaiImagesQuery? query = null,
        string? apiKey = null,
        CancellationToken cancellationToken = default);

    #endregion

    #region Tags

    /// <summary>
    /// Gets a paginated list of tags.
    /// </summary>
    /// <param name="limit">Results per page (0 = all).</param>
    /// <param name="page">Page number.</param>
    /// <param name="query">Filter by tag name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paginated response containing tags.</returns>
    Task<CivitaiPagedResponse<CivitaiTag>> GetTagsAsync(
        int? limit = null,
        int? page = null,
        string? query = null,
        CancellationToken cancellationToken = default);

    #endregion

    #region Creators

    /// <summary>
    /// Gets a paginated list of creators.
    /// </summary>
    /// <param name="limit">Results per page.</param>
    /// <param name="page">Page number.</param>
    /// <param name="query">Filter by username.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A paginated response containing creators.</returns>
    Task<CivitaiPagedResponse<CivitaiCreatorInfo>> GetCreatorsAsync(
        int? limit = null,
        int? page = null,
        string? query = null,
        CancellationToken cancellationToken = default);

    #endregion
}

/// <summary>
/// Tag information from Civitai.
/// </summary>
public sealed record CivitaiTag
{
    public string Name { get; init; } = string.Empty;
    public int ModelCount { get; init; }
    public string? Link { get; init; }
}

/// <summary>
/// Creator information from the creators endpoint.
/// </summary>
public sealed record CivitaiCreatorInfo
{
    public string Username { get; init; } = string.Empty;
    public int ModelCount { get; init; }
    public string? Link { get; init; }
}
