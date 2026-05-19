using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DiffusionNexus.UI.Services.ConfigurationChecker;

/// <summary>
/// Bridges <see cref="IWorkloadInstallationChecker"/> (consumed by the Service layer) to the
/// existing UI-side <see cref="IConfigurationCheckerService"/> + Installer SDK repositories.
///
/// <para>
/// Picks the active ComfyUI installation from the user's installer-package list (the
/// <c>IsDefault</c> entry; first entry when none is marked default), loads the requested
/// workload from the SDK database, and runs the same disk-walking check the Installer
/// Manager workload dialog runs. The resulting <see cref="WorkloadCheckSummary"/> is what
/// the feature readiness panel uses to decide if Generate / Generate (Vision) / Upscale
/// can be invoked.
/// </para>
///
/// <para>
/// Implemented as a singleton that resolves scoped dependencies (<see cref="IUnitOfWork"/>,
/// <see cref="IConfigurationRepository"/>) per call via <see cref="IServiceProvider"/>.
/// </para>
/// </summary>
public sealed class WorkloadInstallationCheckerAdapter : IWorkloadInstallationChecker
{
    private static readonly ILogger Logger = Log.ForContext<WorkloadInstallationCheckerAdapter>();

    private readonly IServiceProvider _serviceProvider;
    private readonly IConfigurationCheckerService _configurationChecker;

    public WorkloadInstallationCheckerAdapter(
        IServiceProvider serviceProvider,
        IConfigurationCheckerService configurationChecker)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configurationChecker);
        _serviceProvider = serviceProvider;
        _configurationChecker = configurationChecker;
    }

    /// <inheritdoc />
    public async Task<WorkloadCheckSummary> CheckAsync(Guid workloadId, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var sp = scope.ServiceProvider;

            var configurationRepository = sp.GetRequiredService<IConfigurationRepository>();
            var configuration = await configurationRepository.GetByIdAsync(workloadId, cancellationToken);

            if (configuration is null)
            {
                Logger.Warning("Workload {WorkloadId} not found in SDK database", workloadId);
                return new WorkloadCheckSummary
                {
                    WorkloadId = workloadId,
                    WorkloadName = workloadId.ToString(),
                    IsFullyInstalled = false,
                    MissingItems = [$"Workload {workloadId} is not in the Installer SDK database."],
                };
            }

            var comfyUIRootPath = await ResolveComfyUIRootPathAsync(sp, cancellationToken);
            if (comfyUIRootPath is null)
            {
                Logger.Warning(
                    "No ComfyUI installation registered — cannot check workload '{WorkloadName}'",
                    configuration.Name);
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

            var checkResult = await _configurationChecker.CheckConfigurationAsync(
                configuration,
                comfyUIRootPath,
                options: null,
                cancellationToken);

            var missingItems = BuildMissingItemsList(checkResult);

            return new WorkloadCheckSummary
            {
                WorkloadId = workloadId,
                WorkloadName = configuration.Name,
                IsFullyInstalled = checkResult.OverallStatus == ConfigurationStatus.Full,
                MissingItems = missingItems,
                CheckedAgainstPath = comfyUIRootPath
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Workload installation check failed for {WorkloadId}", workloadId);
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
    /// Builds a human-readable list of every workload item that's not installed on disk.
    /// Placeholder models are counted as installed (matching the dialog's behaviour) so they
    /// are never reported as missing here.
    /// </summary>
    private static List<string> BuildMissingItemsList(ConfigurationCheckResult result)
    {
        var missing = new List<string>();

        foreach (var node in result.CustomNodeResults)
        {
            if (!node.IsInstalled)
            {
                missing.Add($"Missing custom node: {node.Name}");
            }
        }

        foreach (var model in result.ModelResults)
        {
            if (!model.IsInstalled)
            {
                missing.Add($"Missing model: {model.Name}");
            }
        }

        return missing;
    }

    /// <summary>
    /// Picks the active ComfyUI installation path. Default install wins; otherwise the first
    /// ComfyUI installer-package entry in the user's list. <c>null</c> when none registered.
    /// </summary>
    private static async Task<string?> ResolveComfyUIRootPathAsync(
        IServiceProvider sp,
        CancellationToken ct)
    {
        var unitOfWork = sp.GetRequiredService<IUnitOfWork>();
        var packages = await unitOfWork.InstallerPackages.GetAllAsync(ct);

        var comfy = packages
            .Where(p => p.Type == InstallerType.ComfyUI)
            .OrderByDescending(p => p.IsDefault)
            .ThenBy(p => p.Name)
            .FirstOrDefault();

        return string.IsNullOrWhiteSpace(comfy?.InstallationPath) ? null : comfy.InstallationPath;
    }
}
