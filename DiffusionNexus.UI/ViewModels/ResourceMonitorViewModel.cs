using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Backs the reusable <c>ResourceMonitorView</c> widget: live GPU VRAM + system RAM usage with a
/// manual refresh. The hosting control polls <see cref="RefreshCommand"/> on a low-frequency timer
/// while it is visible (see <c>ResourceMonitorView</c> code-behind).
/// </summary>
public partial class ResourceMonitorViewModel : ObservableObject
{
    private readonly IResourceMonitorService _service;

    [ObservableProperty]
    private string _gpuName = "GPU";

    [ObservableProperty]
    private bool _isGpuAvailable;

    [ObservableProperty]
    private string _vramText = "—";

    [ObservableProperty]
    private double _vramPercent;

    [ObservableProperty]
    private int _gpuUtilPercent;

    [ObservableProperty]
    private string _ramText = "—";

    [ObservableProperty]
    private double _ramPercent;

    [ObservableProperty]
    private string? _errorText;

    public ResourceMonitorViewModel(IResourceMonitorService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <summary>Design-time / fallback constructor.</summary>
    public ResourceMonitorViewModel() : this(new ResourceMonitorService())
    {
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var snapshot = await _service.GetSnapshotAsync().ConfigureAwait(true);
            Apply(snapshot);
        }
        catch
        {
            // The service is documented never to throw; guard anyway so a timer tick can't crash.
        }
    }

    private void Apply(ResourceSnapshot s)
    {
        IsGpuAvailable = s.GpuAvailable;
        ErrorText = s.GpuAvailable ? null : s.Error;
        GpuName = string.IsNullOrWhiteSpace(s.GpuName) ? "GPU" : s.GpuName!;
        GpuUtilPercent = s.GpuUtilPercent;

        VramPercent = s.VramTotalMB > 0
            ? Math.Clamp(s.VramUsedMB * 100.0 / s.VramTotalMB, 0, 100)
            : 0;
        VramText = s.VramTotalMB > 0
            ? $"{ToGb(s.VramUsedMB)} / {ToGb(s.VramTotalMB)} GB"
            : "—";

        RamPercent = s.RamTotalMB > 0
            ? Math.Clamp(s.RamUsedMB * 100.0 / s.RamTotalMB, 0, 100)
            : 0;
        RamText = s.RamTotalMB > 0
            ? $"{ToGb(s.RamUsedMB)} / {ToGb(s.RamTotalMB)} GB"
            : "—";
    }

    private static string ToGb(long megabytes)
        => (megabytes / 1024.0).ToString("N1", CultureInfo.InvariantCulture);
}
