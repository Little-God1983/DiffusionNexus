using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Unified implementation that checks ComfyUI server connectivity, custom node availability,
/// and model presence for any registered <see cref="ComfyUIFeature"/>.
/// 
/// <para>
/// Delegates to <see cref="IComfyUIWrapperService"/> for live queries (<c>/system_stats</c>,
/// <c>/object_info</c>) and reads the feature requirements from <see cref="ComfyUIFeatureRegistry"/>.
/// </para>
/// </summary>
public sealed class ComfyUIReadinessService : IComfyUIReadinessService
{
    private static readonly ILogger Logger = Log.ForContext<ComfyUIReadinessService>();

    private readonly IComfyUIWrapperService _comfyUi;
    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    /// <param name="comfyUi">The ComfyUI wrapper for HTTP/WebSocket communication.</param>
    /// <param name="settingsService">Reads the configured ComfyUI server URL.</param>
    public ComfyUIReadinessService(IComfyUIWrapperService comfyUi, IAppSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(comfyUi);
        ArgumentNullException.ThrowIfNull(settingsService);
        _comfyUi = comfyUi;
        _settingsService = settingsService;
    }

    /// <inheritdoc />
    public ComfyUIFeatureRequirements? GetRequirements(ComfyUIFeature feature) =>
        ComfyUIFeatureRegistry.GetRequirements(feature);

    /// <inheritdoc />
    public async Task<bool> IsServerOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            var serverUrl = await GetServerUrlAsync(ct);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await httpClient.GetAsync($"{serverUrl}/system_stats", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<FeatureReadinessResult> CheckFeatureAsync(ComfyUIFeature feature, CancellationToken ct = default)
    {
        var requirements = ComfyUIFeatureRegistry.GetRequirements(feature);

        if (requirements is null)
        {
            Logger.Warning("No requirements registered for feature {Feature}", feature);
            return new FeatureReadinessResult
            {
                Feature = feature,
                IsServerOnline = false,
                AllNodesInstalled = false,
                AllModelsPresent = false,
                MissingRequirements = [$"Feature '{feature}' is not registered in the readiness system."],
                Warnings = []
            };
        }

        string serverUrl;
        try
        {
            serverUrl = await GetServerUrlAsync(ct);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to resolve ComfyUI server URL for feature {Feature}", feature);
            return FeatureReadinessResult.ServerOffline(feature, "(unknown)");
        }

        // 1. Health check
        bool isOnline;
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await httpClient.GetAsync($"{serverUrl}/system_stats", ct);
            isOnline = response.IsSuccessStatusCode;
        }
        catch
        {
            isOnline = false;
        }

        if (!isOnline)
        {
            Logger.Debug("ComfyUI server offline at {Url} for feature {Feature}", serverUrl, feature);
            return FeatureReadinessResult.ServerOffline(feature, serverUrl);
        }

        // 2. Check custom nodes
        var missingReqs = new List<string>();
        var allNodesInstalled = true;

        if (requirements.RequiredNodeTypes.Count > 0)
        {
            var missingNodes = await _comfyUi.CheckRequiredNodesAsync(requirements.RequiredNodeTypes, ct);

            if (missingNodes.Count > 0)
            {
                allNodesInstalled = false;
                foreach (var node in missingNodes)
                {
                    missingReqs.Add($"Missing custom node: {node}");
                }

                missingReqs.Add("Install via ComfyUI Manager or place into the custom_nodes folder, then restart ComfyUI.");

                Logger.Warning(
                    "Feature {Feature}: missing custom nodes: {MissingNodes}",
                    feature, string.Join(", ", missingNodes));
            }
        }

        // 3. Check models
        var allBlockingModelsPresent = true;
        var warnings = new List<string>();

        foreach (var model in requirements.RequiredModels)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var availableOptions = await _comfyUi.GetNodeInputOptionsAsync(
                    model.NodeType, model.InputName, ct);

                Logger.Debug(
                    "Feature {Feature}: {NodeType}.{Input} has {Count} option(s): {Options}",
                    feature, model.NodeType, model.InputName,
                    availableOptions.Count, string.Join(", ", availableOptions));

                var isPresent = availableOptions.Any(
                    o => o.Contains(model.ExpectedModelSubstring, StringComparison.OrdinalIgnoreCase));

                if (!isPresent)
                {
                    var sizeHint = string.IsNullOrEmpty(model.ApproximateSizeDescription)
                        ? ""
                        : $" ({model.ApproximateSizeDescription})";

                    if (model.AutoDownloads)
                    {
                        warnings.Add(
                            $"The model '{model.DisplayName}' is not yet downloaded{sizeHint}. " +
                            "It will be automatically downloaded on first run. This may take several minutes.");
                    }
                    else
                    {
                        allBlockingModelsPresent = false;
                        missingReqs.Add($"Missing model: {model.DisplayName}{sizeHint}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning(ex,
                    "Feature {Feature}: failed to query model info for {NodeType}.{Input}",
                    feature, model.NodeType, model.InputName);

                // Treat query failure as non-blocking if the model auto-downloads
                if (model.AutoDownloads)
                {
                    warnings.Add($"Could not verify model '{model.DisplayName}' — it may download automatically on first run.");
                }
                else
                {
                    allBlockingModelsPresent = false;
                    missingReqs.Add($"Could not verify model '{model.DisplayName}' — the {model.NodeType} node may not be installed.");
                }
            }
        }

        var result = new FeatureReadinessResult
        {
            Feature = feature,
            IsServerOnline = true,
            AllNodesInstalled = allNodesInstalled,
            AllModelsPresent = allBlockingModelsPresent,
            MissingRequirements = missingReqs,
            Warnings = warnings,
            ServerUrl = serverUrl
        };

        Logger.Information(
            "Readiness check for {Feature}: Ready={IsReady}, MissingReqs={MissingCount}, Warnings={WarnCount}",
            feature, result.IsReady, missingReqs.Count, warnings.Count);

        return result;
    }

    private async Task<string> GetServerUrlAsync(CancellationToken ct)
    {
        var settings = await _settingsService.GetSettingsAsync(ct);
        return settings.ComfyUiServerUrl.TrimEnd('/');
    }
}
