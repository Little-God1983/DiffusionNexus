namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Disk-state summary for a single SDK workload, produced by
/// <see cref="Services.IWorkloadInstallationChecker"/>.
/// Mirrors what the Installer Manager workload dialog reports for the same workload.
/// </summary>
public sealed record WorkloadCheckSummary
{
    /// <summary>The workload's SDK configuration id that was checked.</summary>
    public required Guid WorkloadId { get; init; }

    /// <summary>Workload name (for log/UI messages).</summary>
    public required string WorkloadName { get; init; }

    /// <summary>
    /// <c>true</c> when every required custom node and model for the workload is present
    /// on disk (placeholders count as present). Equivalent to the Installer Manager's
    /// "Full" status.
    /// </summary>
    public required bool IsFullyInstalled { get; init; }

    /// <summary>
    /// Human-readable descriptions of items that are not installed
    /// (custom node folder missing, model file absent, etc.). Empty when
    /// <see cref="IsFullyInstalled"/> is <c>true</c>.
    /// </summary>
    public required IReadOnlyList<string> MissingItems { get; init; }

    /// <summary>
    /// Disk path against which the check was performed (the resolved ComfyUI root).
    /// <c>null</c> when no ComfyUI installation could be located.
    /// </summary>
    public string? CheckedAgainstPath { get; init; }
}
