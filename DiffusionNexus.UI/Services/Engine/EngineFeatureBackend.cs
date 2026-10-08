using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>
/// Readiness for features that run on the Diffusion Nexus Engine: is the Engine installed, and does
/// its own folder hold every node pack and model the feature's catalog workloads declare? Unlike the
/// ComfyUI backend it never looks at other ComfyUI installs on the machine, and it never starts the
/// Engine — Generate does that.
/// </summary>
public sealed class EngineFeatureBackend : IFeatureBackend
{
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineFeatureBackend>();

    private readonly IEngineRootResolver _rootResolver;
    private readonly ICatalog _catalog;
    private readonly IConfigurationCheckerService _checker;
    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly Func<string?, bool> _looksInstalled;

    public EngineFeatureBackend(
        IEngineRootResolver rootResolver,
        ICatalog catalog,
        IConfigurationCheckerService checker,
        IUnifiedLogger? unifiedLogger = null,
        Func<string?, bool>? looksInstalled = null)
    {
        ArgumentNullException.ThrowIfNull(rootResolver);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(checker);
        _rootResolver = rootResolver;
        _catalog = catalog;
        _checker = checker;
        _unifiedLogger = unifiedLogger;
        _looksInstalled = looksInstalled ?? ManagedEngineLocator.LooksInstalled;
    }

    public BackendKind Kind => BackendKind.Engine;

    public string DisplayName => "Diffusion Nexus Engine";

    public async Task<FeatureReadinessResult> CheckFeatureAsync(Feature feature, CancellationToken ct = default)
    {
        var row = EngineFeatureCatalog.ForAppFeature(feature);
        if (row is null)
        {
            var label = feature == Feature.OutpaintVision ? "Outpaint Vision" : feature.ToString();
            return NotReady(feature, isOnline: true, [$"{label} is not available on the Diffusion Nexus Engine yet"]);
        }

        var root = await _rootResolver.ResolveAsync(ct);
        if (!_looksInstalled(root))
        {
            Emit($"{feature}: Engine not installed.");
            return NotReady(feature, isOnline: false, ["Diffusion Nexus Engine is not installed"]);
        }

        var missing = new List<string>();
        try
        {
            foreach (var workloadId in EngineFeatureCatalog.Get(row.Value).WorkloadIds)
            {
                var configuration = await _catalog.GetWorkloadAsync(workloadId, ct);
                if (configuration is null)
                {
                    missing.Add($"Workload {workloadId} is missing from the catalog. Update the catalog and try again.");
                    continue;
                }

                var check = await _checker.CheckConfigurationAsync(configuration, root!, options: null, ct);
                missing.AddRange(check.CustomNodeResults.Where(n => !n.IsInstalled)
                    .Select(n => $"Custom node missing on the Engine: {n.Name}"));
                missing.AddRange(check.ModelResults.Where(m => !m.IsInstalled)
                    .Select(m => $"Model missing on the Engine: {m.Name}"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Returned as a result, not thrown: a throw makes the readiness line drop the backend
            // name and its "change" link, the one way out to Custom URL.
            var reason = $"Engine readiness check failed: {ex.Message}";
            Logger.Warning(ex, "Engine readiness: {Feature}: {Reason}", feature, reason);
            _unifiedLogger?.Warn(LogCategory.Configuration, LogSource, $"{feature}: {reason}", ex.ToString());
            return NotReady(feature, isOnline: true, [reason]);
        }

        Emit(missing.Count == 0
            ? $"{feature}: ready on the Engine ({root})."
            : $"{feature}: {missing.Count} item(s) missing on the Engine ({root}).");

        return new FeatureReadinessResult
        {
            Feature = feature,
            Backend = BackendKind.Engine,
            IsBackendOnline = true,
            IsReady = missing.Count == 0,
            ActiveBackendName = DisplayName,
            MissingRequirements = missing,
            Warnings = [],
            Endpoint = root
        };
    }

    private FeatureReadinessResult NotReady(Feature feature, bool isOnline, IReadOnlyList<string> missing) => new()
    {
        Feature = feature,
        Backend = BackendKind.Engine,
        IsBackendOnline = isOnline,
        IsReady = false,
        ActiveBackendName = DisplayName,
        MissingRequirements = missing,
        Warnings = []
    };

    private void Emit(string message)
    {
        Logger.Information("Engine readiness: {Message}", message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message);
    }
}
