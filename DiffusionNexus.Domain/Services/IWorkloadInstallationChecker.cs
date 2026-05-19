using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Disk-based installation check for an SDK workload.
///
/// <para>
/// Bridges feature readiness (Image Editor / Captioning / Batch Upscale) to the same
/// truth source the Installer Manager dialog uses — the SDK <c>InstallationConfiguration</c>
/// walked against the configured ComfyUI installation. When the Installer Manager would
/// show a workload as "Partial" or "None", this checker reports
/// <see cref="WorkloadCheckSummary.IsFullyInstalled"/> = <c>false</c>.
/// </para>
///
/// <para>
/// The implementation lives in the UI assembly (because it depends on the Installer SDK
/// types and on the user's installer-package repository); the interface lives in Domain so
/// the service layer can consume it without taking an SDK package reference.
/// </para>
/// </summary>
public interface IWorkloadInstallationChecker
{
    /// <summary>
    /// Runs the configuration checker for the given workload against the registered
    /// ComfyUI installation (the one flagged <c>IsDefault</c>, or the first ComfyUI install
    /// when none is marked default).
    /// </summary>
    /// <param name="workloadId">SDK <c>InstallationConfiguration.Id</c> to check.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A summary describing whether the workload is fully installed on disk plus the list
    /// of missing items. When the check could not run at all (workload not in the SDK DB,
    /// no ComfyUI installation registered, or the check threw), the returned summary has
    /// <see cref="WorkloadCheckSummary.IsFullyInstalled"/> = <c>false</c> and a single
    /// human-readable entry in <see cref="WorkloadCheckSummary.MissingItems"/> describing
    /// the reason — callers can treat these uniformly as "not ready".
    /// </returns>
    Task<WorkloadCheckSummary> CheckAsync(Guid workloadId, CancellationToken cancellationToken = default);
}
