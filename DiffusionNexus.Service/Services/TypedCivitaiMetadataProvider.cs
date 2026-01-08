/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */

using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Enums;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Metadata provider that uses the strongly-typed Civitai client.
/// Fetches model metadata from the Civitai API using SHA256 hash lookup.
/// </summary>
/// <remarks>
/// This is the recommended provider for new code. It uses <see cref="ICivitaiClient"/>
/// which provides strongly-typed responses, eliminating manual JSON parsing.
/// </remarks>
public partial class TypedCivitaiMetadataProvider : IModelMetadataProvider
{
    private readonly ICivitaiClient _civitaiClient;
    private readonly string? _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="TypedCivitaiMetadataProvider"/> class.
    /// </summary>
    /// <param name="civitaiClient">The Civitai client for API communication.</param>
    /// <param name="apiKey">Optional API key for authenticated requests.</param>
    public TypedCivitaiMetadataProvider(ICivitaiClient civitaiClient, string? apiKey = null)
    {
        _civitaiClient = civitaiClient ?? throw new ArgumentNullException(nameof(civitaiClient));
        _apiKey = apiKey;
    }

    /// <inheritdoc/>
    public Task<bool> CanHandleAsync(string identifier, CancellationToken cancellationToken = default)
    {
        // This provider handles SHA256 hashes (64 hex characters)
        return Task.FromResult(identifier.Length == 64 && Sha256Regex().IsMatch(identifier));
    }

    /// <inheritdoc/>
    public async Task<ModelClass> GetModelMetadataAsync(
        string filePath,
        CancellationToken cancellationToken = default,
        ModelClass? modelClass = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        modelClass ??= new ModelClass();

        // Compute SHA256 hash of the file
        var hash = await ComputeSha256Async(filePath, cancellationToken);
        modelClass.SHA256Hash = hash;

        try
        {
            // Fetch model version by hash from Civitai API
            var modelVersion = await _civitaiClient.GetModelVersionByHashAsync(hash, _apiKey, cancellationToken);

            if (modelVersion is null)
            {
                modelClass.NoMetaData = true;
                return modelClass;
            }

            // Populate model class from version data
            PopulateFromModelVersion(modelVersion, modelClass);

            // Fetch full model data if we have a model ID
            if (modelVersion.ModelId > 0)
            {
                var model = await _civitaiClient.GetModelAsync(modelVersion.ModelId, _apiKey, cancellationToken);
                if (model is not null)
                {
                    PopulateFromModel(model, modelClass);
                }
            }

            modelClass.NoMetaData = !modelClass.HasAnyMetadata;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Model not found in API - mark as no metadata
            modelClass.NoMetaData = true;
        }

        return modelClass;
    }

    /// <summary>
    /// Populates model class fields from a Civitai model version response.
    /// </summary>
    private static void PopulateFromModelVersion(CivitaiModelVersion version, ModelClass modelClass)
    {
        modelClass.ModelId = version.ModelId > 0 ? version.ModelId.ToString() : null;
        modelClass.DiffusionBaseModel = version.BaseModel ?? modelClass.DiffusionBaseModel;
        modelClass.ModelVersionName = version.Name ?? modelClass.ModelVersionName;
        modelClass.TrainedWords = version.TrainedWords?.ToList() ?? modelClass.TrainedWords;
    }

    /// <summary>
    /// Populates model class fields from a Civitai model response.
    /// </summary>
    private static void PopulateFromModel(CivitaiModel model, ModelClass modelClass)
    {
        modelClass.ModelType = MapModelType(model.Type);
        modelClass.Tags = model.Tags?.ToList() ?? modelClass.Tags;
        modelClass.CivitaiCategory = MetaDataUtilService.GetCategoryFromTags(modelClass.Tags);
        modelClass.Nsfw = model.Nsfw;
    }

    /// <summary>
    /// Maps Civitai model type to service layer DiffusionTypes.
    /// </summary>
    private static DiffusionTypes MapModelType(CivitaiModelType civitaiType) => civitaiType switch
    {
        CivitaiModelType.Checkpoint => DiffusionTypes.CHECKPOINT,
        CivitaiModelType.LORA => DiffusionTypes.LORA,
        CivitaiModelType.DoRA => DiffusionTypes.DORA,
        CivitaiModelType.LoCon => DiffusionTypes.LOCON,
        CivitaiModelType.Hypernetwork => DiffusionTypes.HYPERNETWORK,
        CivitaiModelType.Controlnet => DiffusionTypes.CONTROLNET,
        CivitaiModelType.VAE => DiffusionTypes.VAE,
        CivitaiModelType.TextualInversion => DiffusionTypes.TEXTUALINVERSION,
        CivitaiModelType.AestheticGradient => DiffusionTypes.AESTHETICGRADIENT,
        CivitaiModelType.Poses => DiffusionTypes.POSES,
        CivitaiModelType.Upscaler => DiffusionTypes.UPSCALER,
        CivitaiModelType.MotionModule => DiffusionTypes.MOTION,
        CivitaiModelType.Wildcards => DiffusionTypes.WILDCARDS,
        CivitaiModelType.Workflows => DiffusionTypes.WORKFLOWS,
        CivitaiModelType.Other => DiffusionTypes.OTHER,
        _ => DiffusionTypes.UNASSIGNED
    };

    /// <summary>
    /// Computes the SHA256 hash of a file asynchronously.
    /// </summary>
    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hashBytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hashBytes);
    }

    [GeneratedRegex("^[a-fA-F0-9]+$")]
    private static partial Regex Sha256Regex();
}
