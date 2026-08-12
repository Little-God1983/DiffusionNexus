using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Models;
using Serilog;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the image generation metadata side panel in the image viewer.
/// Displays ComfyUI workflow data parsed from PNG text chunks, plus the
/// content tags and SFW/NSFW rating from the local tag index when available.
/// </summary>
public partial class ImageMetadataPanelViewModel : ObservableObject
{
    private readonly Services.ImageMetadataParser _parser = new();
    private readonly ITagIndexService? _tagIndexService;
    private readonly Action<string, bool>? _onNsfwRatingChanged;
    private Func<string, Task>? _copyToClipboard;
    private string? _currentImagePath;

    /// <summary>
    /// Guards against out-of-order tag lookups: arrow-key navigation fires
    /// one async index query per image, and a slow query for image N must
    /// not overwrite the tags already shown for image N+1. Only ever touched
    /// on the UI thread, so a plain int is enough.
    /// </summary>
    private int _tagLookupGeneration;

    /// <summary>
    /// The in-flight (or last completed) tag-index lookup. The panel never
    /// awaits it — tags pop in when ready — but tests must, or they assert
    /// against a race.
    /// </summary>
    internal Task TagLookup { get; private set; } = Task.CompletedTask;

    /// <param name="tagIndexService">Enables the Content Tags section when present.</param>
    /// <param name="onNsfwRatingChanged">
    /// Invoked with (path, effectiveIsNsfw) after the user overrides or resets
    /// a rating, so the gallery behind the viewer can update its tile badge
    /// and re-apply its NSFW filter without re-querying everything.
    /// </param>
    public ImageMetadataPanelViewModel(
        ITagIndexService? tagIndexService = null,
        Action<string, bool>? onNsfwRatingChanged = null)
    {
        _tagIndexService = tagIndexService;
        _onNsfwRatingChanged = onNsfwRatingChanged;
    }

    [ObservableProperty]
    private bool _isPanelExpanded = true;

    [ObservableProperty]
    private ImageGenerationData? _metadata;

    [ObservableProperty]
    private bool _hasData;

    [ObservableProperty]
    private string _statusMessage = "No image loaded";

    [ObservableProperty]
    private bool _positiveCopied;

    [ObservableProperty]
    private bool _negativeCopied;

    /// <summary>Content tags for the current image from the local tag index.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _tags = [];

    /// <summary>
    /// True when the current image has a row in the tag index — gates the
    /// whole tags section, so unindexed images (and videos) show nothing
    /// rather than a misleading empty list.
    /// </summary>
    [ObservableProperty]
    private bool _hasTagData;

    [ObservableProperty]
    private bool _isNsfw;

    /// <summary>
    /// True when <see cref="IsNsfw"/> is the user's manual verdict rather
    /// than the tagger's automatic rating — shows the "manual" marker and
    /// the reset-to-auto affordance.
    /// </summary>
    [ObservableProperty]
    private bool _isRatingOverridden;

    /// <summary>Whether any LoRAs were found in the metadata.</summary>
    public bool HasLoras => Metadata?.Loras.Count > 0;

    /// <summary>Whether the denoise value should be shown (only when &lt; 1.0).</summary>
    public bool ShowDenoise => Metadata?.Denoise is not null and < 1.0;

    /// <summary>
    /// Sets the clipboard copy delegate. Should be called from the View code-behind
    /// once the visual tree is attached.
    /// </summary>
    public void SetClipboardAction(Func<string, Task> copyToClipboard)
    {
        _copyToClipboard = copyToClipboard;
    }

    /// <summary>
    /// Parses and loads metadata from the specified image file.
    /// </summary>
    public void LoadMetadata(string? imagePath)
    {
        // Tag-index lookup is independent of the PNG metadata parse below —
        // a JPG has no generation chunks but can still be indexed and tagged.
        TagLookup = LoadTagIndexDataAsync(imagePath);

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            Metadata = null;
            HasData = false;
            StatusMessage = "No image loaded";
            OnDerivedPropertiesChanged();
            return;
        }

        try
        {
            var ext = Path.GetExtension(imagePath);
            if (!ext.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                Metadata = new ImageGenerationData
                {
                    FileName = Path.GetFileName(imagePath),
                    HasData = false
                };
                HasData = false;
                StatusMessage = "No generation data found in this image";
                OnDerivedPropertiesChanged();
                return;
            }

            Metadata = _parser.Parse(imagePath);
            HasData = Metadata.HasData;
            StatusMessage = HasData ? "" : "No generation data found in this image";
        }
        catch (Exception ex)
        {
            HasData = false;
            StatusMessage = $"Error reading metadata: {ex.Message}";
        }

        OnDerivedPropertiesChanged();
    }

    /// <summary>
    /// Queries the tag index for the current image's tags and rating.
    /// Fire-and-forget from <see cref="LoadMetadata"/>: the panel renders the
    /// (fast) PNG parse immediately and the tags pop in when the DB answers.
    /// </summary>
    private async Task LoadTagIndexDataAsync(string? imagePath)
    {
        var generation = ++_tagLookupGeneration;
        _currentImagePath = imagePath;
        Tags = [];
        HasTagData = false;
        IsNsfw = false;
        IsRatingOverridden = false;

        if (_tagIndexService is null || string.IsNullOrWhiteSpace(imagePath)) return;

        try
        {
            // SQLite's async is synchronous under the hood — keep it off the
            // UI thread. The index stores normalized full paths.
            var lookup = await Task.Run(() => _tagIndexService.GetTagsForFilesAsync([imagePath]));
            if (generation != _tagLookupGeneration) return;

            if (lookup.TryGetValue(Path.GetFullPath(imagePath), out var info))
            {
                Tags = info.Tags;
                IsNsfw = info.IsNsfw;
                IsRatingOverridden = info.IsRatingOverridden;
                HasTagData = true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ImageViewer] Tag index lookup failed for {Path}", imagePath);
        }
    }

    /// <summary>
    /// Records the clicked side of the SFW/NSFW segmented control as the
    /// user's manual verdict. The override wins over the tagger everywhere
    /// (badge, gallery tiles, Hide NSFW / NSFW only) and survives index
    /// rebuilds. Clicking the already-active side is a no-op — it must not
    /// quietly pin an override that changes nothing visible.
    /// </summary>
    /// <param name="rating">"SFW" or "NSFW" (the button's CommandParameter).</param>
    [RelayCommand]
    private async Task SetRatingAsync(string? rating)
    {
        var path = _currentImagePath;
        if (_tagIndexService is null || path is null || !HasTagData) return;

        var newIsNsfw = string.Equals(rating, "NSFW", StringComparison.OrdinalIgnoreCase);
        if (newIsNsfw == IsNsfw) return;
        try
        {
            await Task.Run(() => _tagIndexService.SetRatingOverrideAsync(path, newIsNsfw));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ImageViewer] Failed to save the rating override for {Path}", path);
            return;
        }

        // Only reflect the change after the write actually landed — a DB
        // failure must not leave the badge lying about what is stored.
        IsNsfw = newIsNsfw;
        IsRatingOverridden = true;
        _onNsfwRatingChanged?.Invoke(path, newIsNsfw);
    }

    /// <summary>Discards the manual verdict and re-reads the automatic rating.</summary>
    [RelayCommand]
    private async Task ResetRatingAsync()
    {
        var path = _currentImagePath;
        if (_tagIndexService is null || path is null || !IsRatingOverridden) return;

        try
        {
            await Task.Run(() => _tagIndexService.ClearRatingOverrideAsync(path));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ImageViewer] Failed to clear the rating override for {Path}", path);
            return;
        }

        // Re-query rather than guessing what the automatic rating was.
        await (TagLookup = LoadTagIndexDataAsync(path));
        if (HasTagData)
            _onNsfwRatingChanged?.Invoke(path, IsNsfw);
    }

    [RelayCommand]
    private void TogglePanel()
    {
        IsPanelExpanded = !IsPanelExpanded;
    }

    [RelayCommand]
    private async Task CopyPositivePromptAsync()
    {
        if (Metadata?.PositivePrompt is null || _copyToClipboard is null) return;

        await _copyToClipboard(Metadata.PositivePrompt);
        PositiveCopied = true;
        await Task.Delay(1500);
        PositiveCopied = false;
    }

    [RelayCommand]
    private async Task CopyNegativePromptAsync()
    {
        if (Metadata?.NegativePrompt is null || _copyToClipboard is null) return;

        await _copyToClipboard(Metadata.NegativePrompt);
        NegativeCopied = true;
        await Task.Delay(1500);
        NegativeCopied = false;
    }

    private void OnDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasLoras));
        OnPropertyChanged(nameof(ShowDenoise));
    }
}
