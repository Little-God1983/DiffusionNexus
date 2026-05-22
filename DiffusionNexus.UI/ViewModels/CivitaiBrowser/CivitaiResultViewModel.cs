using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai.Models;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// View model for a single result card in the Civitai browser.
/// </summary>
public partial class CivitaiResultViewModel : ObservableObject
{
    public CivitaiResultViewModel(CivitaiModel model)
    {
        Model = model;
        Name = model.Name;
        Creator = model.Creator?.Username ?? "Unknown";
        DownloadCount = model.Stats?.DownloadCount ?? 0;
        ThumbsUp = model.Stats?.ThumbsUpCount ?? 0;
        IsNsfw = model.Nsfw;

        var first = model.ModelVersions.FirstOrDefault();
        BaseModel = first?.BaseModel ?? "";
        VersionCount = model.ModelVersions.Count;
        // Only flag the model as EA when the *latest* version is in early access;
        // older non-EA versions are still freely available even if newer ones aren't.
        IsEarlyAccess = first?.EarlyAccessTimeFrame > 0;

        foreach (var v in model.ModelVersions)
        {
            Versions.Add(new CivitaiVersionPickItemViewModel(v));
        }

        // Pre-select latest by default for cards' simple "select card → enqueue latest" flow.
        if (Versions.Count > 0) Versions[0].IsSelected = true;

        PreviewUrl = model.ModelVersions
            .SelectMany(v => v.Images)
            .FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url;

        _ = LoadPreviewAsync();
    }

    private CivitaiResultViewModel() { }

    public static CivitaiResultViewModel CreateDesignSample() => new()
    {
        Name = "Example LoRA",
        Creator = "Designer",
        BaseModel = "SDXL 1.0",
        VersionCount = 3,
        DownloadCount = 12345,
        ThumbsUp = 678
    };

    public CivitaiModel? Model { get; private init; }

    public string Name { get; private init; } = string.Empty;
    public string Creator { get; private init; } = string.Empty;
    public string BaseModel { get; private init; } = string.Empty;
    public int VersionCount { get; private init; }
    public int DownloadCount { get; private init; }
    public int ThumbsUp { get; private init; }
    public bool IsEarlyAccess { get; private init; }
    public bool IsNsfw { get; private init; }
    public string? PreviewUrl { get; private init; }

    public ObservableCollection<CivitaiVersionPickItemViewModel> Versions { get; } = [];

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isHidden;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _showVersionPicker;

    public event EventHandler? SelectionChanged;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Called after bulk version-selection commands so the UI re-evaluates the summary line.
    /// </summary>
    public void NotifyVersionSummaryChanged() => OnPropertyChanged(nameof(SelectedVersionSummary));

    [RelayCommand]
    private void SelectAllVersions()
    {
        foreach (var v in Versions) v.IsSelected = true;
        NotifyVersionSummaryChanged();
    }

    [RelayCommand]
    private void LatestVersionOnly()
    {
        if (Versions.Count == 0) return;
        for (var i = 0; i < Versions.Count; i++)
        {
            Versions[i].IsSelected = i == 0;
        }
        NotifyVersionSummaryChanged();
    }

    public string VersionCountLabel => VersionCount > 1 ? $"{VersionCount} versions" : "1 version";

    public string SelectedVersionSummary
    {
        get
        {
            var sel = Versions.Where(v => v.IsSelected).ToList();
            if (sel.Count == 0) return "(none selected)";
            if (sel.Count == 1) return sel[0].Name;
            return $"{sel.Count} versions selected";
        }
    }

    private async Task LoadPreviewAsync()
    {
        if (string.IsNullOrEmpty(PreviewUrl)) return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var data = await http.GetByteArrayAsync(PreviewUrl);
            if (data.Length == 0) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                using var ms = new MemoryStream(data);
                PreviewImage = new Bitmap(ms);
            });
        }
        catch
        {
            // Preview load failure is non-fatal — card just shows without thumbnail.
        }
    }
}
