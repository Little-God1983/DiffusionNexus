using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Inference.Captioning;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Displayable wrapper around <see cref="CaptioningModelManager.DownloadDestination"/>.
/// Adds the per-tier "fits / doesn't fit" badge that the dialog uses to gate
/// the OK button — the underlying record itself stays UI-agnostic.
/// </summary>
public sealed partial class DestinationOptionViewModel : ObservableObject
{
    public CaptioningModelManager.DownloadDestination Destination { get; }
    public string Path => Destination.Path;
    public string Label => Destination.Label;
    public long FreeBytes => Destination.FreeBytes;

    /// <summary>"123.4 GB free" / "unknown" for paths we couldn't probe.</summary>
    public string FreeBytesLabel =>
        FreeBytes <= 0 ? "free space unknown" : $"{ToReadable(FreeBytes)} free";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpaceCheckLabel))]
    [NotifyPropertyChangedFor(nameof(SpaceCheckColor))]
    [NotifyPropertyChangedFor(nameof(HasEnoughSpace))]
    private long _requiredBytes;

    public bool HasEnoughSpace => FreeBytes <= 0 || FreeBytes >= RequiredBytes;

    public string SpaceCheckLabel => RequiredBytes <= 0
        ? string.Empty
        : HasEnoughSpace
            ? $"OK — need {ToReadable(RequiredBytes)}"
            : $"NOT ENOUGH SPACE — need {ToReadable(RequiredBytes)}";

    /// <summary>Bound directly by the XAML — avoids needing a value converter.</summary>
    public string SpaceCheckColor => RequiredBytes <= 0
        ? "#999999"
        : HasEnoughSpace ? "#4CAF50" : "#F44336";

    public DestinationOptionViewModel(CaptioningModelManager.DownloadDestination destination)
    {
        Destination = destination;
    }

    private static string ToReadable(long bytes)
    {
        const double KB = 1024;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        return bytes switch
        {
            >= (long)GB => $"{bytes / GB:F1} GB",
            >= (long)MB => $"{bytes / MB:F0} MB",
            _ => $"{bytes:N0} B"
        };
    }
}

/// <summary>
/// ViewModel for <c>CaptioningDownloadOptionsDialog</c>. Combines VRAM-tier
/// selection with destination-folder selection and a live disk-space check
/// so the user gets a single confirmation step before a multi-GB fetch.
/// </summary>
public partial class CaptioningDownloadOptionsViewModel : ViewModelBase
{
    private readonly CaptioningModelManager _manager;
    private readonly DiffusionNexus.Domain.Enums.CaptioningModelType _modelType;

    public string ModelDisplayName { get; }

    public ObservableCollection<int> VramTiers { get; } = [];
    public ObservableCollection<DestinationOptionViewModel> Destinations { get; } = [];

    /// <summary>
    /// True when the model has VRAM tiers — the dialog shows its picker.
    /// False for non-tiered (legacy single-file) models so users only choose
    /// a destination.
    /// </summary>
    public bool HasVramTiers => VramTiers.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RequiredSpaceLabel))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private int _selectedVramGb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyPropertyChangedFor(nameof(InsufficientSpaceWarning))]
    private DestinationOptionViewModel? _selectedDestination;

    public string RequiredSpaceLabel
    {
        get
        {
            try
            {
                long required;
                if (HasVramTiers)
                {
                    if (SelectedVramGb <= 0) return string.Empty;
                    required = _manager.GetExpectedTierTotalBytes(_modelType, SelectedVramGb);
                }
                else
                {
                    // Non-tiered model — single fixed quant; the manager's
                    // GetExpectedModelSize is the model alone, so we add the
                    // expected mmproj separately via the resolved file size.
                    required = _manager.GetExpectedModelSize(_modelType);
                }
                return $"Required: {FormatBytes(required)} (model + mmproj)";
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public string InsufficientSpaceWarning =>
        SelectedDestination is { HasEnoughSpace: false }
            ? $"⚠ The selected location has only {FormatBytes(SelectedDestination.FreeBytes)} free — not enough for this download."
            : string.Empty;

    /// <summary>
    /// OK to confirm only when a destination is picked and it has enough
    /// space. Tiered models additionally need a tier selected.
    /// </summary>
    public bool CanConfirm =>
        (!HasVramTiers || SelectedVramGb > 0)
        && SelectedDestination is { HasEnoughSpace: true };

    public CaptioningDownloadOptionsViewModel(
        CaptioningModelManager manager,
        DiffusionNexus.Domain.Enums.CaptioningModelType modelType,
        int[] tiers,
        IReadOnlyList<CaptioningModelManager.DownloadDestination> destinations)
    {
        _manager = manager;
        _modelType = modelType;
        ModelDisplayName = CaptioningModelManager.GetDisplayName(modelType);

        foreach (var t in tiers) VramTiers.Add(t);
        foreach (var d in destinations) Destinations.Add(new DestinationOptionViewModel(d));

        // Default selections: smallest tier (less surprise on metered links),
        // first destination (which is the Core default).
        SelectedVramGb = VramTiers.Count > 0 ? VramTiers[0] : 0;
        SelectedDestination = Destinations.Count > 0 ? Destinations[0] : null;
        UpdateRequiredBytes();
    }

    partial void OnSelectedVramGbChanged(int value) => UpdateRequiredBytes();
    partial void OnSelectedDestinationChanged(DestinationOptionViewModel? value) => UpdateRequiredBytes();

    private void UpdateRequiredBytes()
    {
        long required;
        try
        {
            if (HasVramTiers)
            {
                required = SelectedVramGb > 0
                    ? _manager.GetExpectedTierTotalBytes(_modelType, SelectedVramGb)
                    : 0;
            }
            else
            {
                required = _manager.GetExpectedModelSize(_modelType);
            }
        }
        catch
        {
            required = 0;
        }

        foreach (var dest in Destinations)
        {
            dest.RequiredBytes = required;
        }

        OnPropertyChanged(nameof(CanConfirm));
        OnPropertyChanged(nameof(InsufficientSpaceWarning));
    }

    private static string FormatBytes(long bytes)
    {
        const double KB = 1024;
        const double MB = KB * 1024;
        const double GB = MB * 1024;
        return bytes switch
        {
            >= (long)GB => $"{bytes / GB:F1} GB",
            >= (long)MB => $"{bytes / MB:F0} MB",
            _ => $"{bytes:N0} B"
        };
    }
}
