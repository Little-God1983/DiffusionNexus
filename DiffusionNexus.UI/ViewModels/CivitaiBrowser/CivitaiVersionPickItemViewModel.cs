using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// A single Civitai version row inside the multi-version detail picker.
/// Users tick the box for each version they want enqueued.
/// </summary>
public partial class CivitaiVersionPickItemViewModel : ObservableObject
{
    public CivitaiVersionPickItemViewModel(CivitaiModelVersion version)
    {
        Version = version;
        Name = version.Name;
        BaseModel = version.BaseModel;
        var primaryFile = version.Files.FirstOrDefault(f => f.Primary == true) ?? version.Files.FirstOrDefault();
        SizeBytes = (long)((primaryFile?.SizeKB ?? 0) * 1024);
        SizeDisplay = FormatSize(SizeBytes);
        IsEarlyAccess = version.IsEarlyAccessActive();
        IsPermanentlyPaid = version.IsPermanentlyPaid();
    }

    public CivitaiModelVersion Version { get; }
    public string Name { get; }
    public string BaseModel { get; }
    public long SizeBytes { get; }
    public string SizeDisplay { get; }
    public bool IsEarlyAccess { get; }

    /// <summary>True when the version is paywalled forever — waitlisting is pointless.</summary>
    public bool IsPermanentlyPaid { get; }

    /// <summary>
    /// Purple "EA" only for a gate that actually expires; permanently paid versions
    /// get the red "PAID" badge instead. Mirrors the card badge rule.
    /// </summary>
    public bool ShowEarlyAccessBadge => IsEarlyAccess && !IsPermanentlyPaid;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// True when this exact version is already in the local library. Observable because
    /// the installed snapshot is rebuilt after downloads finish, while cards stay on screen.
    /// </summary>
    [ObservableProperty]
    private bool _isInstalled;

    internal static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }
}
