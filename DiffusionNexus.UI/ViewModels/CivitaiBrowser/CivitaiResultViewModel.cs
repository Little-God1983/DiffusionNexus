using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels.CivitaiBrowser;

/// <summary>
/// View model for a single result card in the Civitai browser.
/// </summary>
public partial class CivitaiResultViewModel : ObservableObject
{
    private CancellationTokenSource _cts = new();
    private bool _showNsfwPreviews;

    /// <summary>
    /// One client for the lifetime of the process. A per-operation HttpClient discards the
    /// connection pool and the TLS session every time, which is socket churn against a host we
    /// are already asking to be patient with us. <see cref="SocketsHttpHandler.PooledConnectionLifetime"/>
    /// keeps a process-lifetime client from pinning a stale DNS answer for the app's whole run.
    /// </summary>
    private static readonly HttpClient s_imagePreviewHttp = new(CreatePooledHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Same rationale as <see cref="s_imagePreviewHttp"/>; longer timeout for the video stream.</summary>
    private static readonly HttpClient s_videoPreviewHttp = new(CreatePooledHandler()) { Timeout = TimeSpan.FromSeconds(60) };

    // Inlined rather than read from a separate static field: static field initializers run in
    // textual order, and a field declared AFTER s_imagePreviewHttp/s_videoPreviewHttp above would
    // still be default(TimeSpan) — zero — when those two run, silently disabling pooling instead
    // of setting a 2-minute lifetime.
    private static SocketsHttpHandler CreatePooledHandler() => new() { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };

    /// <summary>
    /// Signals every in-flight preview download (image fetch, video stream,
    /// FFmpeg frame extraction) to stop. Call when the card is being dropped
    /// from the result set so a fresh search doesn't pile up 50+ zombie downloads.
    /// </summary>
    public void Cancel()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
    }

    public CivitaiResultViewModel(CivitaiModel model, bool showNsfwPreviews)
    {
        Model = model;
        Name = model.Name;
        Creator = model.Creator?.Username ?? "Unknown";
        DownloadCount = model.Stats?.DownloadCount ?? 0;
        ThumbsUp = model.Stats?.ThumbsUpCount ?? 0;
        // Policy-based, NOT the raw model.Nsfw boolean: that flag only marks models
        // *designated* mature, while unflagged models routinely carry X/XXX gallery
        // imagery (we request nsfw=true, so the API returns every image unfiltered).
        IsNsfw = CivitaiNsfwPolicy.IsCardNsfw(model);
        Category = InferCategoryFromTags(model.Tags) ?? string.Empty;
        _showNsfwPreviews = showNsfwPreviews;

        var first = model.ModelVersions.FirstOrDefault();
        BaseModel = first?.BaseModel ?? "";
        VersionCount = model.ModelVersions.Count;
        // Only flag the model as EA when the *latest* version is in early access;
        // older non-EA versions are still freely available even if newer ones aren't.
        IsEarlyAccess = first.IsEarlyAccessActive();
        IsPermanentlyPaid = first.IsPermanentlyPaid();

        foreach (var v in model.ModelVersions)
        {
            var pick = new CivitaiVersionPickItemViewModel(v);
            pick.PropertyChanged += OnVersionPropertyChanged;
            Versions.Add(pick);
        }

        // Pre-select latest by default for cards' simple "select card → enqueue latest" flow.
        if (Versions.Count > 0) Versions[0].IsSelected = true;

        SetPreview(CivitaiNsfwPolicy.SelectPreview(model, showNsfwPreviews));

        _ = LoadPreviewAsync();
    }

    /// <summary>
    /// Applies preview selection to the card's bindable fields. Candidates come from
    /// <see cref="CivitaiNsfwPolicy.SelectPreview"/> (stills preferred over videos;
    /// restricted to PG/PG13 images while NSFW is hidden). For video previews the
    /// original URL is kept (FFmpeg streams from it directly); images are rewritten
    /// to a server-side-resized variant so we get a decoder-friendly thumbnail
    /// instead of full-resolution.
    /// </summary>
    private void SetPreview(CivitaiModelImage? preferred)
    {
        IsVideoPreview = preferred is not null && CivitaiNsfwPolicy.IsVideoAsset(preferred);
        PreviewUrl = IsVideoPreview
            ? preferred?.Url
            : RewriteToResizedImageUrl(preferred?.Url);

        // Cache key (videos only) — Civitai's image id when available, URL hash otherwise.
        _previewCacheKey = IsVideoPreview
            ? CivitaiPreviewCache.ComputeKey(preferred?.Id, preferred?.Url)
            : null;
    }

    /// <summary>
    /// Re-evaluates the preview when the browser's "Show NSFW content" toggle flips:
    /// hiding NSFW swaps an adult thumbnail for the model's safest image (and back).
    /// The in-flight load of the old preview is cancelled so a slow stale download
    /// can't overwrite the new thumbnail after the swap.
    /// </summary>
    public void ApplyNsfwPreference(bool showNsfw)
    {
        if (_showNsfwPreviews == showNsfw) return;
        _showNsfwPreviews = showNsfw;
        if (Model is null) return;

        var preferred = CivitaiNsfwPolicy.SelectPreview(Model, showNsfw);
        var newUrl = preferred is not null && CivitaiNsfwPolicy.IsVideoAsset(preferred)
            ? preferred.Url
            : RewriteToResizedImageUrl(preferred?.Url);
        if (string.Equals(newUrl, PreviewUrl, StringComparison.Ordinal)) return;

        try { _cts.Cancel(); } catch { /* already disposed */ }
        _cts = new CancellationTokenSource();

        PreviewImage = null;
        SetPreview(preferred);
        _ = LoadPreviewAsync();
    }

    private string? _previewCacheKey;

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

    /// <summary>Latest version is paywalled forever (paidAccess.permanent) — same
    /// latest-version-only semantic as <see cref="IsEarlyAccess"/>.</summary>
    public bool IsPermanentlyPaid { get; private init; }

    /// <summary>"Early Access" badge visibility — suppressed when the stronger
    /// "Paywalled" badge applies, so the two never stack.</summary>
    public bool ShowEarlyAccessBadge => IsEarlyAccess && !IsPermanentlyPaid;

    public bool IsNsfw { get; private init; }
    public string Category { get; private init; } = string.Empty;
    private bool _isVideoPreview;
    public bool IsVideoPreview
    {
        get => _isVideoPreview;
        private set => SetProperty(ref _isVideoPreview, value);
    }

    private string? _previewUrl;
    public string? PreviewUrl
    {
        get => _previewUrl;
        private set => SetProperty(ref _previewUrl, value);
    }

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

    /// <summary>
    /// Applies an installed-library snapshot to this card and to every version row:
    /// the card badge lights when ANY version is present locally, each row's badge
    /// only for its own version.
    /// </summary>
    public void ApplyInstalledIndex(CivitaiInstalledIndex index)
    {
        var any = false;
        foreach (var v in Versions)
        {
            v.IsInstalled = index.IsInstalled(v.Version);
            any |= v.IsInstalled;
        }
        IsInstalled = any;
    }

    [RelayCommand]
    private void SelectAllVersions()
    {
        foreach (var v in Versions) v.IsSelected = true;
        IsSelected = true;   // also tick the card so AddSelectionToQueue picks it up
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
        IsSelected = true;
        NotifyVersionSummaryChanged();
    }

    /// <summary>
    /// Direct one-click enqueue of every version on this card. Wired by the browser VM
    /// when the result is added to the grid so the result VM stays decoupled from the
    /// queue service.
    /// </summary>
    public Action<CivitaiResultViewModel>? EnqueueAllVersionsHandler { get; set; }

    [RelayCommand]
    private void EnqueueAllVersions() => EnqueueAllVersionsHandler?.Invoke(this);

    /// <summary>True while at least one version row is ticked.</summary>
    public bool HasSelectedVersions => Versions.Any(v => v.IsSelected);

    /// <summary>
    /// Enqueues only the ticked version rows of this card. Wired by the browser VM
    /// alongside <see cref="EnqueueAllVersionsHandler"/>.
    /// </summary>
    public Action<CivitaiResultViewModel>? EnqueueSelectedVersionsHandler { get; set; }

    [RelayCommand(CanExecute = nameof(HasSelectedVersions))]
    private void EnqueueSelectedVersions() => EnqueueSelectedVersionsHandler?.Invoke(this);

    /// <summary>
    /// Keeps the card in step with its version rows: ticking a checkbox in the picker
    /// must refresh the summary line and re-evaluate whether "Add selected to queue"
    /// has anything to add.
    /// </summary>
    private void OnVersionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(CivitaiVersionPickItemViewModel.IsSelected)) return;
        OnPropertyChanged(nameof(SelectedVersionSummary));
        OnPropertyChanged(nameof(HasSelectedVersions));
        EnqueueSelectedVersionsCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Opens the model's Civitai page in the default web browser. The browser always
    /// has the Civitai model id (it came back in the search response), so there's no
    /// "first download metadata" fallback needed like the Installed tile has.
    /// </summary>
    [RelayCommand]
    private void OpenOnCivitai()
    {
        if (Model?.Id is not int modelId || modelId <= 0) return;
        // civitai.com hides NSFW content for unauthenticated visitors; civitai.red
        // serves the full page. Route NSFW models to the mirror.
        var host = IsNsfw ? "civitai.red" : "civitai.com";
        var url = $"https://{host}/models/{modelId}";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            App.Services?.GetService<global::DiffusionNexus.Domain.Services.UnifiedLogging.IUnifiedLogger>()
                ?.Warn(global::DiffusionNexus.Domain.Services.UnifiedLogging.LogCategory.General,
                    "OpenOnCivitai",
                    $"Failed to launch browser for {url}: {ex.Message}");
        }
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

    /// <summary>
    /// Replaces a Civitai CDN URL's existing transform segment with <c>width=450</c>
    /// so we receive a server-side-resized thumbnail instead of <c>original=true</c>
    /// full-resolution payloads (which can be 6+ MB and trip Avalonia's bitmap decoder).
    /// No-op for non-Civitai hosts.
    /// </summary>
    private static string? RewriteToResizedImageUrl(string? url) => CivitaiImageUrls.ToThumbnailUrl(url);

    /// <summary>
    /// Delegates to the one shared inference helper: the LoRA Sorter reorganizes files
    /// into the folders this method picks, so a private copy here means the two silently
    /// drift and then fight over the same files (review 4.1).
    /// </summary>
    private static string? InferCategoryFromTags(IReadOnlyList<string> tags)
        => Services.Lora.Sorting.SorterCategoryResolver.InferFolderName(tags);

    // Cap the number of simultaneous video frame extractions across all visible cards.
    // Each extraction streams an MP4 and runs an FFmpeg invocation — letting 50 cards
    // race would peg CPU and the network. 3 keeps the UI responsive while still
    // populating thumbnails reasonably fast.
    private static readonly SemaphoreSlim s_videoExtractionGate = new(3, 3);

    private async Task LoadPreviewAsync()
    {
        if (string.IsNullOrEmpty(PreviewUrl)) return;

        var logger = App.Services?.GetService<IUnifiedLogger>();
        var ct = _cts.Token;
        if (ct.IsCancellationRequested) return;

        if (IsVideoPreview)
        {
            await LoadVideoFrameAsync(logger, ct);
        }
        else
        {
            await LoadImageAsync(logger, ct);
        }
    }

    private async Task LoadImageAsync(IUnifiedLogger? logger, CancellationToken ct)
    {
        try
        {
            using var response = await s_imagePreviewHttp.GetAsync(PreviewUrl!, HttpCompletionOption.ResponseHeadersRead, ct);

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "(none)";

            if (!response.IsSuccessStatusCode)
            {
                logger?.Warn(LogCategory.Network, "CivitaiPreview",
                    $"Preview fetch HTTP {(int)response.StatusCode} for {Name}",
                    $"Url: {PreviewUrl}\nContent-Type: {contentType}");
                return;
            }

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            if (data.Length == 0 || ct.IsCancellationRequested) return;

            var magic = SniffMagic(data);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var ms = new MemoryStream(data);
                    PreviewImage = new Bitmap(ms);
                }
                catch (Exception decodeEx)
                {
                    logger?.Warn(LogCategory.Network, "CivitaiPreview",
                        $"Preview decode failed for {Name} — content-type {contentType}, {data.Length:N0} bytes, sniff='{magic}'",
                        $"Url: {PreviewUrl}\nGot: {magic} ({contentType})\nDecode error: {decodeEx.Message}");
                }
            });
        }
        catch (OperationCanceledException) { /* card was dropped */ }
        catch (Exception ex)
        {
            logger?.Warn(LogCategory.Network, "CivitaiPreview",
                $"Preview load failed for {Name}: {ex.Message}",
                $"Url: {PreviewUrl}");
        }
    }

    /// <summary>
    /// Downloads a video preview to a temp file and asks <see cref="IVideoThumbnailService"/>
    /// (FFmpeg) to extract a mid-frame poster. Same pipeline the LoRA Viewer uses for
    /// installed video LoRAs; for the browser the temp file is deleted immediately
    /// after extraction so nothing is persisted.
    /// </summary>
    private async Task LoadVideoFrameAsync(IUnifiedLogger? logger, CancellationToken ct)
    {
        var url = PreviewUrl!;

        // Try the on-disk cache first. A hit skips the entire download + FFmpeg dance.
        if (_previewCacheKey is not null)
        {
            try
            {
                var cached = await CivitaiPreviewCache.TryGetAsync(_previewCacheKey, ct).ConfigureAwait(false);
                if (cached is { Length: > 0 })
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (ct.IsCancellationRequested) return;
                        try
                        {
                            using var ms = new MemoryStream(cached);
                            PreviewImage = new Bitmap(ms);
                        }
                        catch (Exception decodeEx)
                        {
                            logger?.Debug(LogCategory.Network, "CivitaiPreview",
                                $"Cached frame decode failed for {Name}: {decodeEx.Message}");
                        }
                    });
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
        }

        var thumbnailService = App.Services?.GetService<IVideoThumbnailService>();
        if (thumbnailService is null)
        {
            logger?.Warn(LogCategory.Network, "CivitaiPreview",
                $"Video preview skipped for {Name}: IVideoThumbnailService not registered.");
            return;
        }

        try
        {
            await thumbnailService.EnsureFFmpegAvailableAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            logger?.Warn(LogCategory.Network, "CivitaiPreview",
                $"FFmpeg unavailable; can't generate poster for {Name}: {ex.Message}");
            return;
        }

        // Hold a slot in the global extraction gate. Wait honors cancellation so a
        // queued card whose VM was abandoned exits immediately without consuming a slot.
        try
        {
            await s_videoExtractionGate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"dn_browser_preview_{Guid.NewGuid():N}.mp4");
        string? generatedThumbnailPath = null;
        try
        {
            if (ct.IsCancellationRequested) return;

            // Stream the video URL to a temp file. Both the GET and the CopyToAsync
            // honor the token, so a cancel mid-download stops within ~one buffer fill.
            using (var response = await s_videoPreviewHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using var responseStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fileStream = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, bufferSize: 81920, useAsync: true);
                await responseStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
            }

            if (ct.IsCancellationRequested) return;

            var size = new FileInfo(tempVideoPath).Length;
            logger?.Debug(LogCategory.Network, "CivitaiPreview",
                $"Video downloaded for {Name} ({size / 1024.0:F0} KB), extracting frame...");

            var result = await thumbnailService.GenerateThumbnailAsync(
                tempVideoPath,
                new VideoThumbnailOptions { MaxWidth = 450, OutputFormat = ThumbnailFormat.WebP },
                ct)
                .ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            if (!result.Success || string.IsNullOrEmpty(result.ThumbnailPath))
            {
                logger?.Warn(LogCategory.Network, "CivitaiPreview",
                    $"FFmpeg frame extraction failed for {Name}: {result.ErrorMessage ?? "unknown"}");
                return;
            }

            generatedThumbnailPath = result.ThumbnailPath;
            var thumbnailBytes = await File.ReadAllBytesAsync(result.ThumbnailPath, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    using var ms = new MemoryStream(thumbnailBytes);
                    PreviewImage = new Bitmap(ms);
                }
                catch (Exception decodeEx)
                {
                    logger?.Warn(LogCategory.Network, "CivitaiPreview",
                        $"Extracted-frame decode failed for {Name}: {decodeEx.Message}");
                }
            });

            // Persist for next time. Use CancellationToken.None so a card that's
            // cancelled right after the bitmap is shown still gets cached.
            if (_previewCacheKey is not null)
            {
                _ = CivitaiPreviewCache.PutAsync(_previewCacheKey, thumbnailBytes, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) { /* card was dropped during fetch/extract */ }
        catch (Exception ex)
        {
            logger?.Warn(LogCategory.Network, "CivitaiPreview",
                $"Video preview pipeline failed for {Name}: {ex.Message}",
                $"Url: {url}");
        }
        finally
        {
            TryDeleteFile(tempVideoPath);
            if (generatedThumbnailPath is not null) TryDeleteFile(generatedThumbnailPath);
            s_videoExtractionGate.Release();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Inspects the first bytes of a payload to identify its actual format. Beats
    /// trusting the Content-Type header which the CDN sometimes mislabels.
    /// </summary>
    private static string SniffMagic(byte[] data)
    {
        if (data.Length < 12) return "unknown (too short)";
        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "JPEG";
        // PNG: 89 50 4E 47
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "PNG";
        // GIF: "GIF8"
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "GIF";
        // WebP: "RIFF" .... "WEBP"
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return "WebP";
        // MP4: bytes 4-7 are "ftyp"
        if (data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70) return "MP4";
        // WebM/Matroska: 1A 45 DF A3
        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3) return "WebM/Matroska";
        // AVI: "RIFF" .... "AVI "
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x41 && data[9] == 0x56 && data[10] == 0x49) return "AVI";
        return $"unknown (first 4 bytes: {data[0]:X2} {data[1]:X2} {data[2]:X2} {data[3]:X2})";
    }
}
