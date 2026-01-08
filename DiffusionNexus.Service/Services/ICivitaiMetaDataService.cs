/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

using DiffusionNexus.Service.Enums;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Service interface for fetching and parsing Civitai model metadata from JSON responses.
/// </summary>
/// <remarks>
/// This interface provides methods for both fetching metadata from Civitai API
/// and parsing raw JSON responses. It's primarily used for processing safetensors
/// files and their associated Civitai metadata.
/// 
/// For new implementations, consider using <see cref="TypedCivitaiMetadataProvider"/>
/// which uses strongly-typed responses via <see cref="DiffusionNexus.Civitai.ICivitaiClient"/>.
/// </remarks>
public interface ICivitaiMetaDataService
{
    /// <summary>
    /// Retrieves model version information from Civitai using a SHA256 hash.
    /// </summary>
    /// <param name="sha256Hash">The SHA256 hash of the model file.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>A JSON string containing the model version information.</returns>
    Task<string> GetModelVersionInformationFromCivitaiAsync(string sha256Hash, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts model information from a safetensors file by parsing its embedded
    /// metadata to find the Civitai URL, then fetching full model data from the API.
    /// </summary>
    /// <param name="safetensorsFilePath">Full path to the safetensors file.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>A JSON string containing the model information from Civitai.</returns>
    Task<string> GetModelInformationAsync(string safetensorsFilePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches model information from Civitai by model ID.
    /// </summary>
    /// <param name="modelId">The Civitai model ID.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>A JSON string containing the model information.</returns>
    Task<string> GetModelInformationFromCivitaiAsync(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extracts the base model name from a Civitai API response.
    /// </summary>
    /// <param name="modelInfoApiResponse">The raw JSON response from Civitai API.</param>
    /// <returns>The base model name (e.g., "SD 1.5", "SDXL").</returns>
    string GetBaseModelName(string modelInfoApiResponse);

    /// <summary>
    /// Extracts the model ID from a Civitai API response.
    /// </summary>
    /// <param name="modelInfoApiResponse">The raw JSON response from Civitai API.</param>
    /// <returns>The model ID as a string.</returns>
    string GetModelId(string modelInfoApiResponse);

    /// <summary>
    /// Extracts tags from a Civitai model info API response.
    /// </summary>
    /// <param name="modelInfoApiResponse">The raw JSON response from Civitai API.</param>
    /// <returns>A list of tag strings.</returns>
    List<string> GetTagsFromModelInfo(string modelInfoApiResponse);

    /// <summary>
    /// Parses the model type from a Civitai API response.
    /// </summary>
    /// <param name="modelInfoApiResponse">The raw JSON response from Civitai API.</param>
    /// <returns>The parsed <see cref="DiffusionTypes"/> value.</returns>
    DiffusionTypes GetModelType(string modelInfoApiResponse);
}
