using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using SerilogILogger = Serilog.ILogger;

namespace DiffusionNexus.UI.Services.ConfigurationChecker;

/// <summary>
/// Bridges <see cref="IWorkloadInstallationChecker"/> (consumed by the Service layer) to the
/// existing UI-side <see cref="IConfigurationCheckerService"/> + Installer SDK repositories.
///
/// <para>
/// Walks every registered ComfyUI installation and reports a node/model as installed if it's
/// present in <em>any</em> of them. Users frequently keep multiple ComfyUI installs side by
/// side (each with its own <c>extra_model_paths.yaml</c>), so checking only the
/// <c>IsDefault</c> install would mark workloads as Partial whenever the running install
/// isn't the one flagged default.
/// </para>
///
/// <para>
/// Implemented as a singleton that resolves its dependencies (the scoped <see cref="IUnitOfWork"/>,
/// the singleton <see cref="ICatalog"/>) per call via <see cref="IServiceProvider"/>.
/// </para>
/// </summary>
public sealed class WorkloadInstallationCheckerAdapter : IWorkloadInstallationChecker
{
    private const string LogSource = "WorkloadCheck";
    private static readonly SerilogILogger SerilogLogger = Log.ForContext<WorkloadInstallationCheckerAdapter>();

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationCheckerService _configurationChecker;
    private readonly IUnifiedLogger? _unifiedLogger;

    public WorkloadInstallationCheckerAdapter(
        IServiceProvider serviceProvider,
        IConfigurationCheckerService configurationChecker,
        IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configurationChecker);
        _serviceProvider = serviceProvider;
        _configurationChecker = configurationChecker;
        _unifiedLogger = unifiedLogger;
    }

    private void LogInfo(string message)
    {
        SerilogLogger.Information(message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message);
    }

    private void LogDebug(string message)
    {
        SerilogLogger.Debug(message);
        _unifiedLogger?.Debug(LogCategory.Configuration, LogSource, message);
    }

    private void LogWarn(string message)
    {
        SerilogLogger.Warning(message);
        _unifiedLogger?.Warn(LogCategory.Configuration, LogSource, message);
    }

    private void LogError(string message, Exception? ex = null)
    {
        if (ex is null) SerilogLogger.Error(message);
        else SerilogLogger.Error(ex, message);
        _unifiedLogger?.Error(LogCategory.Configuration, LogSource, message, ex);
    }

    /// <inheritdoc />
    public async Task<WorkloadCheckSummary> CheckAsync(Guid workloadId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var sp = scope.ServiceProvider;

            var catalog = sp.GetRequiredService<ICatalog>();
            var configuration = await catalog.GetWorkloadAsync(workloadId, cancellationToken);

            if (configuration is null)
            {
                LogWarn($"Workload {workloadId} not found in the workload catalog");
                return new WorkloadCheckSummary
                {
                    WorkloadId = workloadId,
                    WorkloadName = workloadId.ToString(),
                    IsFullyInstalled = false,
                    MissingItems = [$"Workload {workloadId} is not in the workload catalog."],
                };
            }

            var comfyInstallPaths = await ResolveAllComfyUIRootPathsAsync(sp, cancellationToken);
            if (comfyInstallPaths.Count == 0)
            {
                LogWarn($"No ComfyUI installation registered — cannot check workload '{configuration.Name}'");
                return new WorkloadCheckSummary
                {
                    WorkloadId = workloadId,
                    WorkloadName = configuration.Name,
                    IsFullyInstalled = false,
                    MissingItems = [
                        "No ComfyUI installation is registered. Add one in Installer Manager so workloads can be checked against disk."
                    ],
                };
            }

            // Run the disk check against every ComfyUI install in turn and OR the per-item
            // results — a model that lives in one install but not another still counts as
            // installed for the user's purposes.
            var nodeInstalled = new Dictionary<Guid, (bool installed, string name)>();
            var modelInstalled = new Dictionary<Guid, (bool installed, string name)>();
            var modelPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            LogInfo(
                $"Workload '{configuration.Name}': checking against {comfyInstallPaths.Count} ComfyUI install(s): " +
                $"{string.Join(" | ", comfyInstallPaths)}");

            foreach (var path in comfyInstallPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                ConfigurationCheckResult result;
                try
                {
                    result = await _configurationChecker.CheckConfigurationAsync(
                        configuration, path, options: null, cancellationToken);
                }
                catch (Exception ex)
                {
                    LogWarn(
                        $"Workload '{configuration.Name}' check against {path} failed " +
                        $"({ex.Message}); continuing with remaining installs");
                    continue;
                }

                LogInfo(
                    $"Workload '{configuration.Name}' @ {path}: status={result.OverallStatus} " +
                    $"({result.InstalledCustomNodes}/{result.TotalCustomNodes} nodes, " +
                    $"{result.InstalledModels}/{result.TotalModels} models)");

                foreach (var node in result.CustomNodeResults)
                {
                    LogDebug($"  node '{node.Name}' installed={node.IsInstalled} at {node.ExpectedPath}");
                    if (!nodeInstalled.TryGetValue(node.Id, out var prev) || !prev.installed)
                    {
                        nodeInstalled[node.Id] = (node.IsInstalled, node.Name);
                    }
                }

                foreach (var model in result.ModelResults)
                {
                    if (model.IsInstalled && !string.IsNullOrEmpty(model.FoundAtPath))
                        modelPaths.TryAdd(model.Name, model.FoundAtPath);
                    LogDebug(
                        $"  model '{model.Name}' installed={model.IsInstalled} " +
                        $"placeholder={model.IsPlaceholder} " +
                        $"foundAt={(string.IsNullOrEmpty(model.FoundAtPath) ? "(not found)" : model.FoundAtPath)}");
                    if (!modelInstalled.TryGetValue(model.Id, out var prev) || !prev.installed)
                    {
                        // model.IsInstalled is already true for placeholders
                        // (see ConfigurationCheckerService — IsInstalled = IsPlaceholder || file-found)
                        modelInstalled[model.Id] = (model.IsInstalled, model.Name);
                    }
                }
            }

            var missingItems = new List<string>();
            foreach (var (installed, name) in nodeInstalled.Values)
            {
                if (!installed) missingItems.Add($"Missing custom node: {name}");
            }
            foreach (var (installed, name) in modelInstalled.Values)
            {
                if (!installed) missingItems.Add($"Missing model: {name}");
            }

            var isFullyInstalled = missingItems.Count == 0;
            LogInfo(
                $"Workload '{configuration.Name}' check: Full={isFullyInstalled}, " +
                $"checked {comfyInstallPaths.Count} install(s): {string.Join(" | ", comfyInstallPaths)}");

            return new WorkloadCheckSummary
            {
                WorkloadId = workloadId,
                WorkloadName = configuration.Name,
                IsFullyInstalled = isFullyInstalled,
                MissingItems = missingItems,
                CheckedAgainstPath = string.Join(" | ", comfyInstallPaths),
                ModelPaths = modelPaths
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogError($"Workload installation check failed for {workloadId}", ex);
            return new WorkloadCheckSummary
            {
                WorkloadId = workloadId,
                WorkloadName = workloadId.ToString(),
                IsFullyInstalled = false,
                MissingItems = [$"Workload check failed: {ex.Message}"],
            };
        }
    }

    /// <summary>
    /// Returns every registered ComfyUI installation path, default(s) first.
    /// The list is deduplicated case-insensitively so symlinked or repeated entries don't
    /// double-walk the same folder.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ResolveAllComfyUIRootPathsAsync(
        IServiceProvider sp,
        CancellationToken ct)
    {
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var packages = await unitOfWork.InstallerPackages.GetAllAsync(ct);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();

        foreach (var pkg in packages
            .Where(p => p.Type == InstallerType.ComfyUI && !string.IsNullOrWhiteSpace(p.InstallationPath))
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name))
        {
            if (seen.Add(pkg.InstallationPath))
            {
                paths.Add(pkg.InstallationPath);
            }
        }

        return paths;
    }
}
