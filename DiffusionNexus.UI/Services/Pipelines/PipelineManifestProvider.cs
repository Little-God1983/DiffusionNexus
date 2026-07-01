using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;
using DiffusionNexus.UI.Models.Pipelines;
using Serilog;

namespace DiffusionNexus.UI.Services.Pipelines;

/// <summary>
/// Loads pipeline manifests from embedded <c>avares://</c> JSON resources. Parsing happens
/// once, lazily, on first access.
/// </summary>
public sealed class PipelineManifestProvider : IPipelineManifestProvider
{
    private static readonly ILogger Logger = Log.ForContext<PipelineManifestProvider>();

    // Manifest ids to load. Add a new pipeline by dropping <id>.json under Assets/Pipelines/
    // and listing the id here.
    private static readonly string[] ManifestIds = ["anime-to-real", "qwen-image-2512", "image-to-image"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private readonly Lazy<IReadOnlyList<PipelineManifest>> _manifests = new(LoadAll);

    /// <inheritdoc />
    public IReadOnlyList<PipelineManifest> All() => _manifests.Value;

    /// <inheritdoc />
    public PipelineManifest? Get(string pipelineId) =>
        _manifests.Value.FirstOrDefault(m => string.Equals(m.Id, pipelineId, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<PipelineManifest> LoadAll()
    {
        var result = new List<PipelineManifest>();
        foreach (var id in ManifestIds)
        {
            var uri = new Uri($"avares://DiffusionNexus.UI/Assets/Pipelines/{id}.json");
            try
            {
                if (!AssetLoader.Exists(uri))
                {
                    Logger.Warning("Pipeline manifest resource not found: {Uri}", uri);
                    continue;
                }

                using var stream = AssetLoader.Open(uri);
                var manifest = JsonSerializer.Deserialize<PipelineManifest>(stream, JsonOptions);
                if (manifest is null || string.IsNullOrWhiteSpace(manifest.Id))
                {
                    Logger.Warning("Pipeline manifest {Uri} deserialized to null/empty.", uri);
                    continue;
                }

                result.Add(manifest);
                Logger.Information("Loaded pipeline manifest '{Id}' with {Count} asset(s).", manifest.Id, manifest.Assets.Count);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to load pipeline manifest {Uri}.", uri);
            }
        }

        return result;
    }
}
