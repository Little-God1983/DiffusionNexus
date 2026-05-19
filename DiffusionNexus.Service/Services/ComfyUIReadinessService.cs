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
/// When a feature's <see cref="ComfyUIFeatureRequirements.WorkloadConfigurationId"/> is set,
/// the model/node check is delegated to <see cref="IWorkloadInstallationChecker"/> — the same
/// disk-walking logic the Installer Manager workload dialog uses. This keeps "Ready" in the
/// feature panel consistent with the workload status the user sees in the Installer Manager.
/// </para>
///
/// <para>
/// Features without a workload id fall back to the legacy <c>/object_info</c>-driven check.
/// </para>
/// </summary>
public sealed class ComfyUIReadinessService : IComfyUIReadinessService
{
    private static readonly ILogger Logger = Log.ForContext<ComfyUIReadinessService>();

    private readonly IComfyUIWrapperService _comfyUi;
    private readonly IAppSettingsService _settingsService;
    private readonly IWorkloadInstallationChecker? _workloadChecker;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    /// <param name="comfyUi">The ComfyUI wrapper for HTTP/WebSocket communication.</param>
    /// <param name="settingsService">Reads the configured ComfyUI server URL.</param>
    /// <param name="workloadChecker">
    /// Optional disk-based workload checker. When supplied, features that declare a
    /// <see cref="ComfyUIFeatureRequirements.WorkloadConfigurationId"/> use it as the
    /// authoritative source for node/model presence. When <c>null</c>, every feature falls back
    /// to the legacy <c>/object_info</c> probe (tests / standalone consumers).
    /// </param>
    public ComfyUIReadinessService(
        IComfyUIWrapperService comfyUi,
        IAppSettingsService settingsService,
        IWorkloadInstallationChecker? workloadChecker = null)
    {
        ArgumentNullException.ThrowIfNull(comfyUi);
        ArgumentNullException.ThrowIfNull(settingsService);
        _comfyUi = comfyUi;
        _settingsService = settingsService;
        _workloadChecker = workloadChecker;
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

        // 1. Health check — same regardless of which check strategy we use below.
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

        // 2. Pick the check strategy. When the feature is backed by an SDK workload AND we
        // have a workload checker, use the same disk-walking logic the Installer Manager
        // uses so both views agree. Guid.Empty is treated as "no workload set" — it would
        // never match a real SDK row, and a confusing "workload not found" error is worse
        // than falling back to the legacy probe.
        if (requirements.WorkloadConfigurationId is { } workloadId
            && workloadId != Guid.Empty
            && _workloadChecker is not null)
        {
            return await CheckViaWorkloadAsync(feature, requirements, workloadId, serverUrl, ct);
        }

        return await CheckViaObjectInfoAsync(feature, requirements, serverUrl, ct);
    }

    /// <summary>
    /// Disk-based check via <see cref="IWorkloadInstallationChecker"/>. Maps the workload's
    /// installation status (Full / Partial / None, in Installer-Manager terms) onto the
    /// feature readiness model. Anything short of "fully installed" is reported as blocking.
    /// </summary>
    private async Task<FeatureReadinessResult> CheckViaWorkloadAsync(
        ComfyUIFeature feature,
        ComfyUIFeatureRequirements requirements,
        Guid workloadId,
        string serverUrl,
        CancellationToken ct)
    {
        var summary = await _workloadChecker!.CheckAsync(workloadId, ct);

        var missingReqs = new List<string>();

        if (!summary.IsFullyInstalled)
        {
            missingReqs.AddRange(summary.MissingItems);

            // Action hint — the workload dialog is where the user installs missing pieces.
            missingReqs.Add(
                $"Open Installer Manager → '{summary.WorkloadName}' to install the missing items.");
        }

        var result = new FeatureReadinessResult
        {
            Feature = feature,
            IsServerOnline = true,
            // The workload check is holistic: it covers both nodes and models in one disk
            // walk, so AllNodesInstalled and AllModelsPresent move together.
            AllNodesInstalled = summary.IsFullyInstalled,
            AllModelsPresent = summary.IsFullyInstalled,
            MissingRequirements = missingReqs,
            Warnings = [],
            ServerUrl = serverUrl
        };

        Logger.Information(
            "Readiness check for {Feature} (workload {WorkloadId} @ {Path}): Ready={IsReady}, MissingCount={MissingCount}",
            feature, workloadId, summary.CheckedAgainstPath ?? "(unresolved)", result.IsReady, missingReqs.Count);

        return result;
    }

    /// <summary>
    /// Legacy <c>/object_info</c>-driven check, kept for features that don't yet have an
    /// SDK workload id and for tests that don't wire <see cref="IWorkloadInstallationChecker"/>.
    /// </summary>
    private async Task<FeatureReadinessResult> CheckViaObjectInfoAsync(
        ComfyUIFeature feature,
        ComfyUIFeatureRequirements requirements,
        string serverUrl,
        CancellationToken ct)
    {
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
