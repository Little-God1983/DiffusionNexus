using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a single version tab in the model detail view.
/// Blue = user has this version locally. Yellow = available on Civitai but not downloaded.
/// </summary>
public partial class CivitaiVersionTabItem : ObservableObject
{
    private readonly Action<CivitaiVersionTabItem> _onSelected;

    /// <summary>
    /// The Civitai version data (always present — comes from the API response).
    /// </summary>
    public CivitaiModelVersion CivitaiVersion { get; }

    /// <summary>
    /// The local model version, if the user has already downloaded this version.
    /// Null when the version exists only on Civitai.
    /// </summary>
    public ModelVersion? LocalVersion { get; }

    /// <summary>
    /// Whether the user owns this version locally.
    /// </summary>
    public bool IsDownloaded => LocalVersion is not null;

    /// <summary>
    /// Display label for the tab (version name).
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Base model string (e.g., "Flux.1 Kontext", "SDXL 1.0").
    /// </summary>
    public string BaseModel => CivitaiVersion.BaseModel;

    /// <summary>
    /// Trigger words for this version.
    /// </summary>
    public string TriggerWords => CivitaiVersion.TrainedWords.Count > 0
        ? string.Join(", ", CivitaiVersion.TrainedWords)
        : string.Empty;

    /// <summary>
    /// Whether trigger words are available.
    /// </summary>
    public bool HasTriggerWords => CivitaiVersion.TrainedWords.Count > 0;

    /// <summary>
    /// Download URL for this version.
    /// </summary>
    public string? DownloadUrl => CivitaiVersion.DownloadUrl;

    /// <summary>
    /// Whether this tab is currently selected.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// Whether a download is in progress for this version.
    /// </summary>
    [ObservableProperty]
    private bool _isDownloading;

    public CivitaiVersionTabItem(
        CivitaiModelVersion civitaiVersion,
        ModelVersion? localVersion,
        string label,
        Action<CivitaiVersionTabItem> onSelected)
    {
        CivitaiVersion = civitaiVersion;
        LocalVersion = localVersion;
        Label = label;
        _onSelected = onSelected;
    }

    /// <summary>
    /// Selects this version tab.
    /// </summary>
    [RelayCommand]
    private void Select()
    {
        _onSelected(this);
    }
}
