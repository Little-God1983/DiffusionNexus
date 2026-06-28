using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// A point-in-time view of GPU (VRAM) and system (RAM) memory usage. Megabyte values are
/// system-wide (not per-process). When <see cref="GpuAvailable"/> is false the GPU fields are 0
/// and <see cref="Error"/> explains why (e.g. no NVIDIA GPU / nvidia-smi not found).
/// </summary>
public sealed record ResourceSnapshot
{
    public bool GpuAvailable { get; init; }
    public string? GpuName { get; init; }
    public long VramTotalMB { get; init; }
    public long VramUsedMB { get; init; }
    public long VramFreeMB { get; init; }
    public int GpuUtilPercent { get; init; }

    public long RamTotalMB { get; init; }
    public long RamUsedMB { get; init; }
    public long RamAvailableMB { get; init; }
    public int RamUsedPercent { get; init; }

    public string? Error { get; init; }

    public static ResourceSnapshot Empty { get; } = new();
}

/// <summary>
/// Samples current GPU/VRAM and system RAM usage. Implementations should be cheap enough to call
/// every few seconds and must never throw (failures are reported via <see cref="ResourceSnapshot.Error"/>).
/// </summary>
public interface IResourceMonitorService
{
    Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
}
