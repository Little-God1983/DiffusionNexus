using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Default <see cref="IResourceMonitorService"/>. Reads GPU VRAM/utilization from <c>nvidia-smi</c>
/// (NVIDIA only) and system RAM from the Win32 <c>GlobalMemoryStatusEx</c> API. All failures are
/// swallowed and surfaced through <see cref="ResourceSnapshot.Error"/>.
/// </summary>
public sealed class ResourceMonitorService : IResourceMonitorService
{
    private static readonly ILogger Logger = Log.ForContext<ResourceMonitorService>();

    public async Task<ResourceSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var (ramTotal, ramAvail, ramLoad) = GetSystemRam();
        var gpu = await GetGpuAsync(cancellationToken).ConfigureAwait(false);

        return new ResourceSnapshot
        {
            GpuAvailable = gpu.Available,
            GpuName = gpu.Name,
            VramTotalMB = gpu.TotalMB,
            VramUsedMB = gpu.UsedMB,
            VramFreeMB = gpu.FreeMB,
            GpuUtilPercent = gpu.UtilPercent,
            RamTotalMB = ramTotal,
            RamAvailableMB = ramAvail,
            RamUsedMB = Math.Max(0, ramTotal - ramAvail),
            RamUsedPercent = ramLoad,
            Error = gpu.Error,
        };
    }

    // ── GPU (nvidia-smi) ─────────────────────────────────────────────────────────

    private static async Task<(bool Available, string? Name, long TotalMB, long UsedMB, long FreeMB, int UtilPercent, string? Error)>
        GetGpuAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,memory.used,memory.free,utilization.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return (false, null, 0, 0, 0, 0, "nvidia-smi could not be started.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { /* best effort */ }
                return (false, null, 0, 0, 0, 0, "nvidia-smi timed out.");
            }

            var output = await stdoutTask.ConfigureAwait(false);
            var firstLine = output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(firstLine))
                return (false, null, 0, 0, 0, 0, "nvidia-smi returned no data.");

            // "NVIDIA GeForce RTX 4090, 24564, 3201, 21363, 12"
            var parts = firstLine.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 5)
                return (false, null, 0, 0, 0, 0, "Unexpected nvidia-smi output.");

            var name = parts[0];
            var total = ParseLong(parts[1]);
            var used = ParseLong(parts[2]);
            var free = ParseLong(parts[3]);
            var util = (int)ParseLong(parts[4]);

            return (true, name, total, used, free, util, null);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nvidia-smi not on PATH (no NVIDIA driver / non-NVIDIA GPU).
            return (false, null, 0, 0, 0, 0, "No NVIDIA GPU detected (nvidia-smi not found).");
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to query nvidia-smi for GPU stats.");
            return (false, null, 0, 0, 0, 0, $"GPU query failed: {ex.Message}");
        }
    }

    private static long ParseLong(string s)
        => long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // ── System RAM (GlobalMemoryStatusEx) ────────────────────────────────────────

    private static (long TotalMB, long AvailableMB, int LoadPercent) GetSystemRam()
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // TODO: Linux implementation (/proc/meminfo) when the app targets Linux.
                var gc = GC.GetGCMemoryInfo();
                var totalMb = gc.TotalAvailableMemoryBytes / (1024 * 1024);
                return (totalMb, 0, 0);
            }

            var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref status))
                return (0, 0, 0);

            var total = (long)(status.ullTotalPhys / (1024 * 1024));
            var avail = (long)(status.ullAvailPhys / (1024 * 1024));
            return (total, avail, (int)status.dwMemoryLoad);
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to read system RAM status.");
            return (0, 0, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
