using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Serilog;
using SerilogILogger = Serilog.ILogger;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// <see cref="IFeatureBackend"/> for features that execute on a ComfyUI server. Readiness is
/// determined by combining a live server health check with the same disk-walking workload
/// checker the Installer Manager workload dialog uses, so both surfaces always agree.
/// </summary>
public sealed class ComfyUIFeatureBackend : IFeatureBackend
{
    private const string LogSource = "Readiness";
    private static readonly SerilogILogger SerilogLogger = Log.ForContext<ComfyUIFeatureBackend>();

    private readonly IComfyUIWrapperService _comfyUi;
    private readonly IAppSettingsService _settingsService;
    private readonly IWorkloadInstallationChecker _workloadChecker;
    private readonly IUnifiedLogger? _unifiedLogger;

    public ComfyUIFeatureBackend(
        IComfyUIWrapperService comfyUi,
        IAppSettingsService settingsService,
        IWorkloadInstallationChecker workloadChecker,
        IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(comfyUi);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(workloadChecker);
        _comfyUi = comfyUi;
        _settingsService = settingsService;
        _workloadChecker = workloadChecker;
        _unifiedLogger = unifiedLogger;
    }

    /// <inheritdoc />
    public BackendKind Kind => BackendKind.ComfyUI;

    /// <inheritdoc />
    public string DisplayName => "ComfyUI";

    /// <inheritdoc />
    public async Task<FeatureReadinessResult> CheckFeatureAsync(Feature feature, CancellationToken ct = default)
    {
        var requirements = FeatureRegistry.GetRequirements(feature);

        LogInfo($"CheckFeatureAsync({feature}) — start");

        if (requirements is null)
        {
            LogWarn($"No requirements registered for feature {feature}");
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = BackendKind.ComfyUI,
                IsBackendOnline = false,
                IsReady = false,
                ActiveBackendName = DisplayName,
                MissingRequirements = [$"Feature '{feature}' is not registered in the readiness system."],
                Warnings = []
            };
        }

        string serverUrl;
        try
        {
            serverUrl = await GetServerUrlAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the caller's business, not a backend availability problem - do not
            // report it via MissingRequirements. Matches LocalInferenceFeatureBackend (#434).
            throw;
        }
        catch (Exception ex)
        {
            SerilogLogger.Debug(ex, "Failed to resolve ComfyUI server URL for feature {Feature}", feature);
            LogWarn($"{feature}: failed to resolve ComfyUI server URL — {ex.Message}");
            return FeatureReadinessResult.BackendOffline(
                feature, BackendKind.ComfyUI, DisplayName,
                "Could not resolve ComfyUI server URL.",
                endpoint: "(unknown)");
        }

        if (!await IsServerOnlineAsync(serverUrl, ct))
        {
            LogWarn($"{feature}: ComfyUI server offline at {serverUrl}");
            return FeatureReadinessResult.BackendOffline(
                feature, BackendKind.ComfyUI, DisplayName,
                $"ComfyUI server not reachable at {serverUrl}",
                endpoint: serverUrl);
        }

        // Without a workload id the backend has nothing authoritative to check against. The
        // /object_info fallback used to live here but disagreed with the Installer Manager
        // (see issue #356), so we now hard-require a workload id.
        if (requirements.WorkloadConfigurationId is not { } workloadId || workloadId == Guid.Empty)
        {
            LogWarn($"{feature}: no WorkloadConfigurationId registered — cannot check installation");
            return new FeatureReadinessResult
            {
                Feature = feature,
                Backend = BackendKind.ComfyUI,
                IsBackendOnline = true,
                IsReady = false,
                ActiveBackendName = DisplayName,
                MissingRequirements =
                [
                    $"Feature '{feature}' has no SDK workload registered. " +
                    "Add a WorkloadConfigurationId in FeatureRegistry."
                ],
                Warnings = [],
                Endpoint = serverUrl
            };
        }

        return await CheckViaWorkloadAsync(feature, workloadId, serverUrl, ct);
    }

    /// <summary>
    /// Disk-based check via <see cref="IWorkloadInstallationChecker"/>. Maps the workload's
    /// installation status onto the feature readiness model. Anything short of "fully installed"
    /// is reported as blocking — matching the Installer Manager workload dialog exactly.
    /// </summary>
    private async Task<FeatureReadinessResult> CheckViaWorkloadAsync(
        Feature feature,
        Guid workloadId,
        string serverUrl,
        CancellationToken ct)
    {
        var summary = await _workloadChecker.CheckAsync(workloadId, ct);

        var missingReqs = new List<string>();

        if (!summary.IsFullyInstalled)
        {
            missingReqs.AddRange(summary.MissingItems);
            missingReqs.Add(
                $"Open Installer Manager → '{summary.WorkloadName}' to install the missing items.");
        }

        var result = new FeatureReadinessResult
        {
            Feature = feature,
            Backend = BackendKind.ComfyUI,
            IsBackendOnline = true,
            IsReady = summary.IsFullyInstalled,
            ActiveBackendName = DisplayName,
            MissingRequirements = missingReqs,
            Warnings = [],
            Endpoint = serverUrl
        };

        LogInfo(
            $"Readiness check for {feature} (workload {workloadId} @ {summary.CheckedAgainstPath ?? "(unresolved)"}): " +
            $"Ready={result.IsReady}, MissingCount={missingReqs.Count}");

        return result;
    }

    private async Task<bool> IsServerOnlineAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await httpClient.GetAsync($"{serverUrl}/system_stats", ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            // NOTE: HttpClient.Timeout also throws OperationCanceledException (a TaskCanceledException
            // whose CancellationToken is an internal linked token, not necessarily `ct`), so a slow
            // ComfyUI server can now surface as an unhandled cancellation instead of "server not
            // reachable". LocalInferenceFeatureBackend rethrows every OperationCanceledException
            // unconditionally with no such distinction, so this mirrors that discipline for
            // consistency across IFeatureBackend implementations (#434) rather than special-casing
            // the timeout here.
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> GetServerUrlAsync(CancellationToken ct)
    {
        var settings = await _settingsService.GetSettingsAsync(ct);
        return settings.ComfyUiServerUrl.TrimEnd('/');
    }

    /// <summary>Emits to both Serilog (file) and the in-app unified console when available.</summary>
    private void LogInfo(string message)
    {
        SerilogLogger.Information(message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message);
    }

    private void LogWarn(string message)
    {
        SerilogLogger.Warning(message);
        _unifiedLogger?.Warn(LogCategory.Configuration, LogSource, message);
    }
}
