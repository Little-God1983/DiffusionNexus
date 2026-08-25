using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Download;
using DiffusionNexus.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the model detail panel shown when a tile is clicked.
/// Fetches all versions from the Civitai API and shows which are downloaded (blue) vs available (yellow).
/// </summary>
public partial class ModelDetailViewModel : ViewModelBase
{
    private readonly ICivitaiClient? _civitaiClient;
    private readonly IAppSettingsService? _settingsService;
    private readonly ISecureStorage? _secureStorage;
    private readonly IUnifiedLogger? _logger;
    private readonly ICivitaiBaseModelCatalog? _baseModelCatalog;

    // #438: constructor-injected replacements for the former App.Services locator
    // calls. Nullable services degrade gracefully (as the null-conditional locator
    // calls did); the scheduler/clipboard fall back to shared production instances
    // so design-time / demo construction keeps working.
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IDialogService? _dialogService;
    private readonly ICivitaiModelDownloader? _modelDownloader;
    private readonly IClipboardService _clipboard = AvaloniaClipboardService.Instance;
    private readonly IUiScheduler _uiScheduler = AvaloniaUiScheduler.Instance;
    private ICivitaiApiKeyProvider? _apiKeyProvider;

    /// <summary>
    /// Cached Civitai model data from the initial API fetch.
    /// Reused after download to rebuild version tabs without an extra API call.
    /// </summary>
    private CivitaiModel? _cachedCivitaiModel;

    /// <summary>
    /// Cancels any in-flight Civitai thumbnail download when the selected version tab changes.
    /// </summary>
    private CancellationTokenSource? _detailThumbnailCts;

    /// <summary>
    /// Monotonic counter bumped on every <see cref="LoadAsync"/> call and captured by
    /// <see cref="LoadIdentitySourceAsync"/> before it awaits. If the panel has since moved on to
    /// a different tile (the counter no longer matches), the loader's final write is dropped
    /// instead of stamping the previous tile's stale identity source over the current one —
    /// guards against a slow-to-resolve model's lookup completing after a faster one for the
    /// tile the user switched to in the meantime.
    /// </summary>
    private int _identityLoadGeneration;

    /// <summary>
    /// Shared HttpClient for Civitai version-thumbnail downloads (see
    /// <see cref="LoadCivitaiThumbnailAsync"/>). Reusing a single instance avoids the
    /// socket exhaustion (TIME_WAIT accumulation) that a fresh <c>new HttpClient()</c> per
    /// call caused on the tile thumbnail path (issue #460). The tile itself no longer holds a
    /// client of its own — it fetches through <c>IThumbnailProvider</c>'s typed client (#521
    /// Plan B) — so this is the last hand-rolled one, and it is here because the version-tab
    /// preview is a full-size Civitai image, not a thumbnail the pipeline knows about.
    /// </summary>
    private static readonly HttpClient s_civitaiThumbnailClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    #region Observable Properties

    /// <summary>
    /// The source tile that opened this detail view.
    /// </summary>
    [ObservableProperty]
    private ModelTileViewModel? _sourceTile;

    /// <summary>
    /// Model name.
    /// </summary>
    [ObservableProperty]
    private string _modelName = string.Empty;

    /// <summary>
    /// The Civitai model ID.
    /// </summary>
    [ObservableProperty]
    private string _modelIdDisplay = string.Empty;

    /// <summary>
    /// Base model of the currently selected version.
    /// </summary>
    [ObservableProperty]
    private string _baseModelDisplay = string.Empty;

    /// <summary>
    /// Model type display (e.g., "LORA").
    /// </summary>
    [ObservableProperty]
    private string _modelTypeDisplay = string.Empty;

    /// <summary>
    /// Creator name.
    /// </summary>
    [ObservableProperty]
    private string _creatorDisplay = string.Empty;

    /// <summary>
    /// The description converted to readable plain text.
    /// </summary>
    [ObservableProperty]
    private string _descriptionText = string.Empty;

    /// <summary>
    /// Trigger words for the currently selected version.
    /// </summary>
    [ObservableProperty]
    private string _triggerWordsDisplay = string.Empty;

    /// <summary>
    /// Whether trigger words are available.
    /// </summary>
    [ObservableProperty]
    private bool _hasTriggerWords;

    /// <summary>
    /// Tags for the model.
    /// </summary>
    [ObservableProperty]
    private string _tagsDisplay = string.Empty;

    /// <summary>
    /// Whether tags are available.
    /// </summary>
    [ObservableProperty]
    private bool _hasTags;

    /// <summary>
    /// The inferred category (e.g., Character, Style, Concept) derived from the model's tags.
    /// </summary>
    [ObservableProperty]
    private string _categoryDisplay = string.Empty;

    /// <summary>
    /// Whether a category could be inferred from the model's tags.
    /// </summary>
    [ObservableProperty]
    private bool _hasCategory;

    /// <summary>
    /// The currently selected version tab.
    /// </summary>
    [ObservableProperty]
    private CivitaiVersionTabItem? _selectedVersionTab;

    /// <summary>
    /// The thumbnail image.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _thumbnailImage;

    /// <summary>
    /// Whether data is loading. Also gates the "Download Metadata" button so it
    /// can't be triggered while a fetch is already in flight.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadMetadataCommand))]
    private bool _isLoading;

    /// <summary>
    /// Whether a library metadata sync is running somewhere else. Set by
    /// <c>LoraViewerViewModel</c>, which owns the runs (R10).
    /// </summary>
    /// <remarks>
    /// The sync service is single-flight: a second run throws rather than queueing. The button
    /// therefore has to be off while one is going, or pressing it produces an exception message
    /// about something the user had no way of knowing was in progress.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DownloadMetadataCommand))]
    private bool _isLibrarySyncRunning;

    /// <summary>
    /// Status/error message.
    /// </summary>
    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// File name display for the selected version.
    /// </summary>
    [ObservableProperty]
    private string _fileNameDisplay = string.Empty;

    /// <summary>
    /// Folder containing the selected version's local file ("—" when the version
    /// has no file on disk). For per-location tiles this is the file in the tile's
    /// own LoRA-source location.
    /// </summary>
    [ObservableProperty]
    private string _folderPathDisplay = "—";

    /// <summary>
    /// Version ID display for the selected version.
    /// </summary>
    [ObservableProperty]
    private string _versionIdDisplay = string.Empty;

    /// <summary>
    /// How this model's identity was last resolved ("Civitai", "sidecar file", "file header",
    /// "guessed from filename"), or <c>null</c> when nothing meaningful can be said (never
    /// checked, checked and nothing found, or the last check errored). See
    /// <see cref="LoadIdentitySourceAsync"/> for the granularity caveat.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIdentitySource))]
    private string? _identitySourceDisplay;

    /// <summary>Whether the "Identity source:" row has anything to show.</summary>
    public bool HasIdentitySource => IdentitySourceDisplay is not null;

    #endregion

    #region Collections

    /// <summary>
    /// All version tabs (blue = downloaded, yellow = not downloaded).
    /// </summary>
    public ObservableCollection<CivitaiVersionTabItem> VersionTabs { get; } = [];

    /// <summary>
    /// Tags as individual items for display in a wrap panel.
    /// </summary>
    public ObservableCollection<string> TagItems { get; } = [];

    #endregion

    #region Constructors

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ModelDetailViewModel()
    {
        ModelName = "Semi-Fortnite 3D Style - Flux Kontext";
        ModelIdDisplay = "1843355";
        VersionIdDisplay = "2086052";
        BaseModelDisplay = "Flux.1 Kontext";
        ModelTypeDisplay = "LORA";
        FileNameDisplay = "40fy_v1.safetensors";
        FolderPathDisplay = @"C:\AI\Loras\Styles";
        CreatorDisplay = "ExampleCreator";
        DescriptionText = "Transform persons into a vibrant semi-transparent 3D style with this LoRA for Flux Kontext!";
        TriggerWordsDisplay = "40fy, 3d style, fortnite";
        HasTriggerWords = true;
        TagsDisplay = "3d, fortnite, style, character";
        HasTags = true;
        CategoryDisplay = "Style";
        HasCategory = true;
    }

    /// <summary>
    /// Runtime constructor with DI. The parameters after <paramref name="baseModelCatalog"/>
    /// are the dependencies the method bodies previously resolved from the
    /// <c>App.Services</c> locator (#438); they are optional so existing construction
    /// sites keep compiling, but the production site (<c>LoraViewerViewModel</c>)
    /// passes them from DI.
    /// </summary>
    public ModelDetailViewModel(
        ICivitaiClient? civitaiClient,
        IAppSettingsService? settingsService,
        ISecureStorage? secureStorage,
        IUnifiedLogger? logger,
        ICivitaiBaseModelCatalog? baseModelCatalog = null,
        IServiceScopeFactory? scopeFactory = null,
        IDialogService? dialogService = null,
        IClipboardService? clipboard = null,
        IUiScheduler? uiScheduler = null,
        ICivitaiApiKeyProvider? apiKeyProvider = null,
        ICivitaiModelDownloader? modelDownloader = null)
    {
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _secureStorage = secureStorage;
        _logger = logger;
        _baseModelCatalog = baseModelCatalog;
        _scopeFactory = scopeFactory;
        _dialogService = dialogService;
        _clipboard = clipboard ?? AvaloniaClipboardService.Instance;
        _uiScheduler = uiScheduler ?? AvaloniaUiScheduler.Instance;
        _apiKeyProvider = apiKeyProvider;
        _modelDownloader = modelDownloader;
    }

    #endregion

    #region Public Methods

    /// <summary>
    /// Loads detail data for the given tile. Fetches all versions from Civitai API.
    /// </summary>
    public async Task LoadAsync(ModelTileViewModel tile)
    {
        _cachedCivitaiModel = null;
        SourceTile = tile;

        // Populate from local data immediately
        ModelName = tile.DisplayName;
        ModelTypeDisplay = tile.ModelTypeDisplay;
        CreatorDisplay = tile.CreatorName;
        ThumbnailImage = tile.ThumbnailImage;

        PopulateFromLocalVersion(tile);

        // Build editable tag chips from local data immediately
        await LoadEditableTagsAsync();
        LoadCategorySelection();

        // Populate the base-model dropdown from the Civitai catalog (cached;
        // falls back to a bundled snapshot when offline). Fire-and-forget so a
        // slow first fetch never blocks the detail view from rendering.
        _ = LoadBaseModelCatalogAsync();

        // Look up how this model was identified for the "Identity source:" row. Reset first so
        // a stale value from the previously displayed tile never lingers while the fresh lookup
        // is in flight, then fire-and-forget for the same reason as the catalog load above.
        // Bump the generation counter so a still-in-flight lookup from a previous tile can tell,
        // when it finally completes, that it is no longer the current one and must not overwrite
        // this tile's chip.
        IdentitySourceDisplay = null;
        var identityLoadGeneration = ++_identityLoadGeneration;
        _ = LoadIdentitySourceAsync(tile.ModelEntity?.Id ?? 0, identityLoadGeneration);

        // Try to fetch from Civitai API for the full version list
        await FetchCivitaiDataAsync(tile);
    }

    #endregion

    #region Commands

    /// <summary>
    /// Closes the detail panel.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens the model page on Civitai in the default browser.
    /// </summary>
    [RelayCommand]
    private void OpenOnCivitai()
    {
        if (!CanOpenOnCivitai) return;
        SourceTile?.OpenOnCivitaiCommand.Execute(null);
    }

    /// <summary>
    /// Requests a Civitai metadata download for this single LoRA. The parent
    /// <see cref="LoraViewerViewModel"/> handles the request (it owns the hash-lookup
    /// and persistence logic shared with the bulk "Download Metadata" flow): it hashes
    /// the LoRA file, looks it up on Civitai, persists the returned metadata, and then
    /// reloads this detail view so the new description/tags/images/versions appear.
    /// Disabled while a load or fetch is already in flight, and while a library-wide sync is
    /// running — the sync service admits one run at a time and refuses a second outright.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDownloadMetadata))]
    private void DownloadMetadata()
    {
        MetadataDownloadRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanDownloadMetadata() => !IsLoading && !IsLibrarySyncRunning;

    /// <summary>
    /// Copies trigger words to clipboard.
    /// </summary>
    [RelayCommand]
    private async Task CopyTriggerWordsAsync()
    {
        if (string.IsNullOrWhiteSpace(TriggerWordsDisplay)) return;
        await CopyToClipboardAsync(TriggerWordsDisplay);
    }

    /// <summary>
    /// Downloads the currently selected version if it's not locally available.
    /// Shows a dialog for destination selection, then streams the download with progress tracking.
    /// </summary>
    [RelayCommand]
    private async Task DownloadSelectedVersionAsync()
    {
        var tab = SelectedVersionTab;
        if (tab is null || tab.IsDownloaded) return;

        // Ensure a Civitai API token is configured before downloading.
        // If missing, show the token dialog so the user can paste one.
        if (!await EnsureCivitaiTokenAsync())
            return;

        // Resolve download URL
        var primaryFile = CivitaiVersionFiles.PickPrimary(tab.CivitaiVersion);
        var downloadUrl = primaryFile?.DownloadUrl ?? tab.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            _logger?.Warn(LogCategory.Download, "LoraDownload",
                $"No download URL available for '{ModelName}' version '{tab.Label}'");
            return;
        }

        // Get source folders for the dialog
        IReadOnlyList<string> sourceFolders = [];
        if (_settingsService is not null)
        {
            sourceFolders = await _settingsService.GetEnabledLoraSourcesAsync();
        }

        // Show download destination dialog
        if (_dialogService is null) return;

        var result = await _dialogService.ShowDownloadLoraVersionDialogAsync(
            ModelName, tab.CivitaiVersion, sourceFolders, CategoryDisplay);

        if (!result.Confirmed || string.IsNullOrWhiteSpace(result.TargetFolder))
            return;

        // The panel's own fallback name when the version carries no named file, instead of the
        // downloader's synthesized "model_{id}.safetensors". Also the name reported back to the user.
        var fallbackFileName = string.IsNullOrWhiteSpace(primaryFile?.Name)
            ? $"{ModelName}_{tab.Label}.safetensors"
            : null;
        var fileName = fallbackFileName ?? primaryFile!.Name!;

        // Mark as downloading
        tab.IsDownloading = true;

        // The one download path owns the transfer, the collision policy, hash verification,
        // persistence and the library-changed signal (spec §4.4) — this panel only asks.
        _ = Task.Run(async () =>
        {
            try
            {
                if (_modelDownloader is null)
                {
                    _logger?.Warn(LogCategory.Download, "LoraDownload",
                        "Download unavailable: ICivitaiModelDownloader not provided.");
                    return;
                }

                var request = new DownloadRequest(tab.CivitaiVersion, result.TargetFolder!, DownloadTrigger.DetailPanel)
                {
                    File = primaryFile,
                    ExistingModelId = SourceTile?.ModelEntity?.Id,
                    FileNameOverride = fallbackFileName,
                };

                var outcome = await _modelDownloader.DownloadAsync(request).ConfigureAwait(false);
                if (outcome.Success && outcome.FinalPath is not null)
                {
                    await _uiScheduler.InvokeAsync(() => _ = RefreshAfterDownloadAsync(outcome.FinalPath));
                    return;
                }

                // Without this the typed outcome had no consumer here: a 403 on a gated model just
                // stopped the spinner — no message, no dialog, no status text — while the inline
                // downloader this replaced reported the failure and the sibling migration in
                // LoraViewerViewModel maps every status to a visible line.
                if (DescribeFailedDownload(outcome, fileName) is { } message)
                    await _uiScheduler.InvokeAsync(() => StatusMessage = message);
            }
            finally
            {
                await _uiScheduler.InvokeAsync(() => tab.IsDownloading = false);
            }
        });
    }

    /// <summary>
    /// The user-visible line for a download that did not succeed, or null when there is nothing to
    /// report. Mirrors <c>LoraViewerViewModel.DownloadLoraAsync</c>'s switch: cancelling is not
    /// failing, and a hash mismatch is not a clean download — Task 5 made those distinguishable, so
    /// they must not collapse back into one red line. Internal so the mapping is testable without
    /// standing up a dialog service and a live download.
    /// </summary>
    internal static string? DescribeFailedDownload(DownloadOutcome outcome, string fileName) => outcome.Status switch
    {
        DownloadStatus.Cancelled => $"Download cancelled: {fileName}",
        DownloadStatus.HashMismatch => $"Downloaded {fileName} — hash mismatch, file kept for inspection",
        DownloadStatus.Failed =>
            $"Download failed: {fileName}{(outcome.Error is null ? "" : $" ({outcome.Error})")}",
        _ => null,
    };

    /// <summary>
    /// Reloads the model from the database and refreshes the source tile and detail panel
    /// so the newly downloaded version appears as "downloaded" (blue tab).
    /// Uses <see cref="_cachedCivitaiModel"/> to rebuild tabs without an extra API call.
    /// </summary>
    private async Task RefreshAfterDownloadAsync(string downloadedFilePath)
    {
        try
        {
            var sourceTile = SourceTile;
            if (sourceTile is null || _scopeFactory is null) return;

            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // Find the model that owns the file we just downloaded (targeted query, not full DB load)
            var refreshedModel = await unitOfWork.Models.FindByLocalFilePathAsync(downloadedFilePath);

            // Fallback: match by existing tile model ID
            refreshedModel ??= sourceTile.ModelEntity?.Id is > 0
                ? await unitOfWork.Models.GetByIdWithIncludesAsync(sourceTile.ModelEntity.Id)
                : null;

            if (refreshedModel is not null)
            {
                await _uiScheduler.InvokeAsync(() =>
                {
                    sourceTile.RefreshModelData(refreshedModel);

                    ModelName = refreshedModel.Name;
                    ModelIdDisplay = refreshedModel.CivitaiId?.ToString()
                                     ?? refreshedModel.CivitaiModelPageId?.ToString()
                                     ?? "\u2014";
                    CreatorDisplay = refreshedModel.Creator?.Username ?? "Unknown";

                    if (_cachedCivitaiModel is not null)
                    {
                        BuildCivitaiVersionTabs(_cachedCivitaiModel, sourceTile);
                    }
                    else
                    {
                        BuildLocalVersionTabs(sourceTile);
                    }
                });
            }
            else
            {
                _logger?.Debug(LogCategory.Download, "LoraDownload",
                    "Could not find model in DB after download \u2014 UI not refreshed");
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "LoraDownload",
                $"Failed to refresh UI after download: {ex.Message}");
        }
    }

    #endregion

    #region Events

    /// <summary>
    /// Raised when the user requests to close the detail panel.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Raised when the user clicks "Download Metadata" for this individual LoRA.
    /// The parent <see cref="LoraViewerViewModel"/> performs the hash-based Civitai
    /// lookup + persistence, then reloads this detail view.
    /// </summary>
    public event EventHandler? MetadataDownloadRequested;

    /// <summary>
    /// Raised after the user confirms "Delete Metadata" and all DB rows for this
    /// LoRA have been removed. The parent <see cref="LoraViewerViewModel"/>
    /// subscribes to re-discover the still-on-disk safetensors so the tile
    /// reappears immediately as a bare-metadata entry.
    /// </summary>
    public event EventHandler? MetadataDeleted;

    internal void RaiseMetadataDeleted() => MetadataDeleted?.Invoke(this, EventArgs.Empty);

    #endregion

    #region Private Methods

    /// <summary>
    /// Looks up how this model's identity was last resolved and maps it to the short display
    /// string shown in the "Identity source:" row. Fire-and-forget from <see cref="LoadAsync"/>,
    /// same pattern as <see cref="LoadBaseModelCatalogAsync"/> — a slow lookup must not block the
    /// rest of the detail view from rendering. Swallow-and-log on failure, same as that loader.
    /// </summary>
    /// <param name="modelId">The model whose identity source is being looked up.</param>
    /// <param name="generation">
    /// The <see cref="_identityLoadGeneration"/> value captured by the caller at the moment this
    /// lookup was fired. Unlike <see cref="LoadBaseModelCatalogAsync"/> (model-invariant — any
    /// result is valid for any tile), this loader is keyed to the specific model passed in, so a
    /// slow call for a previous tile must not stamp its result over a newer tile's chip once the
    /// user has switched. Checked right before the final write; a mismatch means the panel has
    /// since moved on and the result is discarded.
    /// </param>
    /// <remarks>
    /// The source tracked here is <b>per model</b> — one <c>ModelSyncState</c> row — while the
    /// base model shown just above it in the view is <b>per version</b>. On a model with several
    /// versions this row describes how the model as a whole was identified, not necessarily how
    /// any one version's base model value came to be.
    /// <c>internal</c> (rather than <c>private</c>) so the cross-model race guard on
    /// <paramref name="generation"/> is directly unit-testable — same rationale as
    /// <see cref="DescribeIdentitySource"/>.
    /// </remarks>
    internal async Task LoadIdentitySourceAsync(int modelId, int generation)
    {
        if (modelId <= 0 || _scopeFactory is null)
        {
            await _uiScheduler.InvokeAsync(() =>
            {
                if (generation == _identityLoadGeneration) IdentitySourceDisplay = null;
            });
            return;
        }

        string? display;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var state = await unitOfWork.SyncStates.GetByModelIdAsync(modelId);
            display = state is not null ? DescribeIdentitySource(state.MetadataOutcome) : null;
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.General, "ModelDetail",
                $"Failed to load identity source for model {modelId}: {ex.Message}");
            display = null;
        }

        await _uiScheduler.InvokeAsync(() =>
        {
            // Drop the write if a newer LoadAsync call has since fired for a different tile —
            // otherwise a slow lookup for the previous model could overwrite the current tile's
            // correct chip with stale data (see _identityLoadGeneration).
            if (generation == _identityLoadGeneration) IdentitySourceDisplay = display;
        });
    }

    /// <summary>
    /// Maps a <see cref="SyncOutcome"/> to the short label shown in the "Identity source:" row.
    /// <c>internal static</c> so it is directly unit-testable.
    /// </summary>
    internal static string? DescribeIdentitySource(SyncOutcome outcome) => outcome switch
    {
        SyncOutcome.Matched => "Civitai",
        SyncOutcome.Sidecar => "sidecar file",
        SyncOutcome.Header => "file header",
        SyncOutcome.Heuristic => "guessed from filename",
        _ => null,   // None, NotIdentified, Error — say nothing rather than something scary
    };

    private void PopulateFromLocalVersion(ModelTileViewModel tile)
    {
        var model = tile.ModelEntity;
        var version = tile.SelectedVersion;

        ModelIdDisplay = model?.CivitaiId?.ToString() ?? model?.CivitaiModelPageId?.ToString() ?? "\u2014";
        VersionIdDisplay = version?.CivitaiId?.ToString() ?? "\u2014";
        BaseModelDisplay = version?.BaseModelRaw ?? "Unknown";

        // File name + containing folder
        var primaryFile = version?.PrimaryFile;
        FileNameDisplay = primaryFile?.FileName ?? "\u2014";
        var localFile = version is not null
            ? tile.GetScopedFileForVersion(version.Id) ?? primaryFile
            : null;
        FolderPathDisplay = FolderDisplayFromFile(localFile);

        // Description
        DescriptionText = HtmlTextHelper.HtmlToPlainText(model?.Description);

        // Trigger words
        var triggerWords = version?.TriggerWordsText ?? string.Empty;
        TriggerWordsDisplay = triggerWords;
        HasTriggerWords = !string.IsNullOrWhiteSpace(triggerWords);

        // Tags
        PopulateTags(model);

        // Build version tabs from local data only (Civitai fetch will enhance this)
        BuildLocalVersionTabs(tile);
    }

    /// <summary>
    /// Directory of the given file's <see cref="ModelFile.LocalPath"/>, or "—"
    /// when the version has no file on disk.
    /// </summary>
    private static string FolderDisplayFromFile(ModelFile? file)
    {
        var path = file?.LocalPath;
        if (string.IsNullOrWhiteSpace(path)) return "—";
        var dir = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(dir) ? "—" : dir;
    }

    private void PopulateTags(Model? model)
    {
        TagItems.Clear();
        if (model?.Tags is { Count: > 0 } tags)
        {
            var tagNames = tags
                .Where(t => t.Tag is not null)
                .Select(t => t.Tag!.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            foreach (var tag in tagNames)
            {
                TagItems.Add(tag);
            }

            TagsDisplay = string.Join(", ", tagNames);
            HasTags = tagNames.Count > 0;
        }
        else
        {
            TagsDisplay = string.Empty;
            HasTags = false;
        }

        var (categoryDisplay, hasCategory) = ComputeCategoryDisplay(model);
        CategoryDisplay = categoryDisplay;
        HasCategory = hasCategory;
    }

    /// <summary>
    /// The category shown in the detail panel: the user's explicit override when set,
    /// otherwise the first tag that names a <see cref="Domain.Enums.CivitaiCategory"/>.
    /// Delegates to the one resolver the sorter and the download pipeline already use — the
    /// private copy this replaced predated its <c>LooksLikeCategoryName</c> guard, so the real
    /// Civitai tag "2000" showed here as a category called "2000" and "character,style" as
    /// Celebrity. Internal so the rules can be tested without standing up a full panel load.
    /// </summary>
    internal static (string Display, bool Has) ComputeCategoryDisplay(Model? model)
    {
        var category = model is null
            ? Domain.Enums.CivitaiCategory.Unknown
            : Services.Lora.Sorting.SorterCategoryResolver.ResolveForModel(model);

        return category == Domain.Enums.CivitaiCategory.Unknown
            ? (string.Empty, false)
            : (Services.Lora.Sorting.SorterCategoryResolver.ToFolderName(category), true);
    }

    /// <summary>
    /// Maps one local <see cref="ModelFile"/> row onto the Civitai DTO the one download path
    /// (spec §4.4) consumes. The hashes are load-bearing, not decoration: a detail-panel download
    /// of a LOCAL version hands this object straight to <c>ICivitaiModelDownloader</c>, where the
    /// SHA256 is both what <c>DownloadCollisionPolicy</c> proves ownership of a colliding file
    /// with and what the post-transfer verification checks against. Omitting them (as this mapping
    /// originally did) left both blind: every such download fell through to the suffixed name
    /// <c>{stem}_{CivitaiId ?? 0}</c>, so two local-only versions in one folder both claimed
    /// <c>{stem}_0</c> and the second silently replaced the first model's weights.
    /// Internal so the mapping is directly testable without standing up a panel load.
    /// </summary>
    internal static CivitaiModelFile ToCivitaiFile(ModelFile file) => new()
    {
        Id = file.CivitaiId ?? 0,
        Name = file.FileName,
        SizeKB = file.SizeKB,
        Primary = file.IsPrimary,
        DownloadUrl = file.DownloadUrl,
        Hashes = new CivitaiFileHashes
        {
            SHA256 = file.HashSHA256,
            AutoV2 = file.HashAutoV2,
            CRC32 = file.HashCRC32,
            BLAKE3 = file.HashBLAKE3,
        },
    };

    private void BuildLocalVersionTabs(ModelTileViewModel tile)
    {
        VersionTabs.Clear();

        foreach (var version in tile.Versions)
        {
            // Map local files to CivitaiModelFile so a download of this version has file data
            var civFiles = version.Files.Select(ToCivitaiFile).ToList();

            // Map local images to CivitaiModelImage so thumbnails/IDs carry through
            var civImages = version.Images.Select(img => new CivitaiModelImage
            {
                Id = img.CivitaiId,
                Url = img.Url,
                Nsfw = img.IsNsfw,
                Width = img.Width,
                Height = img.Height,
                Hash = img.BlurHash,
                Type = img.MediaType,
                CreatedAt = img.CreatedAt,
                PostId = img.PostId,
                Username = img.Username,
            }).ToList();

            var civitaiVersion = new CivitaiModelVersion
            {
                Id = version.CivitaiId ?? 0,
                ModelId = tile.ModelEntity?.CivitaiId ?? tile.ModelEntity?.CivitaiModelPageId ?? 0,
                Name = version.Name,
                BaseModel = version.BaseModelRaw ?? "Unknown",
                TrainedWords = version.TriggerWords.Select(tw => tw.Word).ToList(),
                DownloadUrl = version.DownloadUrl,
                Files = civFiles,
                Images = civImages,
            };

            var label = !string.IsNullOrWhiteSpace(version.Name) ? version.Name : version.BaseModelRaw ?? "???";
            var tab = new CivitaiVersionTabItem(civitaiVersion, version, label, OnVersionTabSelected);
            VersionTabs.Add(tab);
        }

        // Select the tab matching the tile's currently selected version, or the first tab
        var selectedVersionId = tile.SelectedVersion?.Id;
        var matchingTab = selectedVersionId.HasValue
            ? VersionTabs.FirstOrDefault(t => t.LocalVersion?.Id == selectedVersionId.Value)
            : null;
        if (matchingTab is not null)
        {
            OnVersionTabSelected(matchingTab);
        }
        else if (VersionTabs.Count > 0)
        {
            OnVersionTabSelected(VersionTabs[0]);
        }
    }

    private async Task FetchCivitaiDataAsync(ModelTileViewModel tile)
    {
        if (_civitaiClient is null)
        {
            StatusMessage = "Civitai client not available";
            return;
        }

        var modelId = tile.ModelEntity?.CivitaiId
                      ?? tile.ModelEntity?.CivitaiModelPageId;

        if (modelId is null or 0)
        {
            StatusMessage = "No Civitai ID \u2014 run 'Download Metadata' first";
            return;
        }

        var tileName = tile.ModelEntity?.Name ?? tile.DisplayName;
        var previousTotal = tile.ModelEntity?.TotalVersionCount ?? 0;
        var lastCheckedDisplay = tile.ModelEntity?.LastCheckedForUpdatesUtc?.ToString("u") ?? "never";

        _logger?.Trace(LogCategory.Network, "LoraUpdateChecker",
            $"Attempting update check for '{tileName}' (trigger={LoraUpdateTriggerSource.DetailView}, civitaiId={modelId.Value}, lastChecked={lastCheckedDisplay}, previousTotal={previousTotal})");

        IsLoading = true;
        StatusMessage = "Fetching versions from Civitai...";

        try
        {
            var apiKey = await GetApiKeyAsync();
            var civitaiModel = await _civitaiClient.GetModelAsync(modelId.Value, apiKey);

            if (civitaiModel is null)
            {
                StatusMessage = "Model not found on Civitai";
                _logger?.Debug(LogCategory.Network, "LoraUpdateChecker",
                    $"Civitai returned no model for '{tileName}' (trigger={LoraUpdateTriggerSource.DetailView}, civitaiId={modelId.Value})");
                return;
            }

            _cachedCivitaiModel = civitaiModel;

            // Persist the remote version count so the "+N more versions" tile badge
            // reflects what Civitai reports right now, even for models whose metadata
            // was last downloaded before TotalVersionCount existed. The detail panel
            // is the natural refresh point: opening it already costs one Civitai call.
            var totalRemoteVersions = civitaiModel.ModelVersions?.Count ?? 0;
            var checkedAtUtc = DateTime.UtcNow;
            await PersistRemoteVersionCountAsync(tile, totalRemoteVersions, checkedAtUtc);

            var delta = totalRemoteVersions - previousTotal;
            var deltaText = delta switch
            {
                > 0 => $"+{delta}",
                < 0 => delta.ToString(),
                _ => "no change",
            };
            _logger?.Debug(LogCategory.Network, "LoraUpdateChecker",
                $"Update check completed for '{tileName}' (trigger={LoraUpdateTriggerSource.DetailView}, civitaiId={modelId.Value}): remoteVersions={totalRemoteVersions} ({deltaText} vs previous {previousTotal})");

            await _uiScheduler.InvokeAsync(() =>
            {
                // Refresh the tile's "+N" badge in-place without waiting for a reload.
                tile.UpdateRemoteVersionCount(totalRemoteVersions, checkedAtUtc);

                // Update model-level info
                ModelName = civitaiModel.Name;
                ModelIdDisplay = civitaiModel.Id.ToString();
                DescriptionText = HtmlTextHelper.HtmlToPlainText(civitaiModel.Description);

                // Update tags from Civitai
                if (civitaiModel.Tags.Count > 0)
                {
                    TagItems.Clear();
                    foreach (var tag in civitaiModel.Tags)
                    {
                        TagItems.Add(tag);
                    }
                    TagsDisplay = string.Join(", ", civitaiModel.Tags);
                    HasTags = true;
                }

                // Build version tabs with full Civitai data
                BuildCivitaiVersionTabs(civitaiModel, tile);

                StatusMessage = null;
            });
        }
        catch (HttpRequestException ex)
        {
            _logger?.Error(LogCategory.Network, "ModelDetail",
                $"Failed to fetch model from Civitai: {ex.StatusCode} {ex.Message}", ex);
            StatusMessage = $"Civitai error: {ex.StatusCode}";
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.Network, "ModelDetail",
                $"Failed to fetch model detail: {ex.Message}", ex);
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Persists the latest remote version count and check timestamp to every
    /// grouped model behind the tile so the "+N more versions" badge survives
    /// app restarts. Best-effort: failures are logged and swallowed since the
    /// in-memory tile update has already happened.
    /// </summary>
    private async Task PersistRemoteVersionCountAsync(ModelTileViewModel tile, int totalRemoteVersions, DateTime checkedAtUtc)
    {
        try
        {
            var modelIds = tile.GetAllModelIds();
            if (modelIds.Count == 0) return;

            if (_scopeFactory is null) return;

            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            foreach (var id in modelIds)
            {
                var model = await unitOfWork.Models.GetByIdWithIncludesAsync(id);
                if (model is null) continue;

                model.TotalVersionCount = totalRemoteVersions;
                model.LastCheckedForUpdatesUtc = checkedAtUtc;
            }

            await unitOfWork.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Network, "ModelDetail",
                $"Failed to persist remote version count: {ex.Message}");
        }
    }

    private void BuildCivitaiVersionTabs(CivitaiModel civitaiModel, ModelTileViewModel tile)
    {
        // Build a lookup of locally downloaded version CivitaiIds
        var localVersionByCivitaiId = tile.Versions
            .Where(v => v.CivitaiId.HasValue)
            .ToDictionary(v => v.CivitaiId!.Value, v => v);

        // Also match by name as fallback
        var localVersionByName = tile.Versions
            .ToDictionary(v => v.Name, v => v, StringComparer.OrdinalIgnoreCase);

        VersionTabs.Clear();

        foreach (var civVersion in civitaiModel.ModelVersions)
        {
            // Try to find a matching local version
            ModelVersion? localVersion = null;
            if (localVersionByCivitaiId.TryGetValue(civVersion.Id, out var byId))
            {
                localVersion = byId;
            }
            else if (localVersionByName.TryGetValue(civVersion.Name, out var byName))
            {
                localVersion = byName;
            }

            var label = !string.IsNullOrWhiteSpace(civVersion.Name) ? civVersion.Name : civVersion.BaseModel;
            var tab = new CivitaiVersionTabItem(civVersion, localVersion, label, OnVersionTabSelected);
            VersionTabs.Add(tab);
        }

        // Select the tab matching the tile's currently selected version, then fall back
        // to the first downloaded tab, then the first tab overall.
        var selectedVersionId = tile.SelectedVersion?.Id;
        var matchingTab = selectedVersionId.HasValue
            ? VersionTabs.FirstOrDefault(t => t.LocalVersion?.Id == selectedVersionId.Value)
            : null;
        var firstTab = matchingTab
                       ?? VersionTabs.FirstOrDefault(t => t.IsDownloaded)
                       ?? VersionTabs.FirstOrDefault();
        if (firstTab is not null)
        {
            OnVersionTabSelected(firstTab);
        }
    }

    private void OnVersionTabSelected(CivitaiVersionTabItem selected)
    {
        foreach (var tab in VersionTabs)
        {
            tab.IsSelected = ReferenceEquals(tab, selected);
        }

        SelectedVersionTab = selected;

        // Update display for the selected version
        VersionIdDisplay = selected.CivitaiVersion.Id > 0
            ? selected.CivitaiVersion.Id.ToString()
            : "\u2014";
        BaseModelDisplay = selected.BaseModel;
        SyncSelectedBaseModelFromVersion();

        // Trigger words
        TriggerWordsDisplay = selected.TriggerWords;
        HasTriggerWords = selected.HasTriggerWords;

        // File name from Civitai or local
        if (selected.LocalVersion?.PrimaryFile is { } localFile)
        {
            FileNameDisplay = localFile.FileName ?? "\u2014";
        }
        else
        {
            var civFile = CivitaiVersionFiles.PickPrimary(selected.CivitaiVersion);
            FileNameDisplay = civFile?.Name ?? "\u2014";
        }

        // Containing folder \u2014 prefer the file in the tile's own location (#380)
        var versionFile = selected.LocalVersion is { } lv
            ? SourceTile?.GetScopedFileForVersion(lv.Id) ?? lv.PrimaryFile
            : null;
        FolderPathDisplay = FolderDisplayFromFile(versionFile);

        // Update thumbnail if local version available
        if (selected.LocalVersion is not null && SourceTile is not null)
        {
            // Find the matching version button on the source tile and select it
            var matchingButton = SourceTile.VersionButtons
                .FirstOrDefault(b => b.Version.Id == selected.LocalVersion.Id);
            if (matchingButton is not null)
            {
                matchingButton.SelectCommand.Execute(null);
                ThumbnailImage = SourceTile.ThumbnailImage;
            }
        }
        else if (selected.CivitaiVersion.Images.Count > 0)
        {
            // Cancel any in-flight thumbnail download from a previous version tab
            _detailThumbnailCts?.Cancel();
            _detailThumbnailCts?.Dispose();
            _detailThumbnailCts = new CancellationTokenSource();

            // Load first image from Civitai version
            _ = LoadCivitaiThumbnailAsync(selected.CivitaiVersion.Images[0], _detailThumbnailCts.Token);
        }
    }

    private async Task LoadCivitaiThumbnailAsync(CivitaiModelImage image, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(image.Url)) return;

        try
        {
            var data = await s_civitaiThumbnailClient.GetByteArrayAsync(image.Url, ct);
            if (data.Length == 0) return;

            ct.ThrowIfCancellationRequested();

            await _uiScheduler.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(data);
                    ThumbnailImage = new Bitmap(stream);
                }
                catch
                {
                    // Image decode failure — ignore
                }
            });
        }
        catch (OperationCanceledException)
        {
            // Version tab changed while downloading — discard silently
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Network, "ModelDetail",
                $"Failed to load Civitai thumbnail: {ex.Message}");
        }
    }

    /// <summary>
    /// Retrieves the Civitai API key via <see cref="ICivitaiApiKeyProvider"/> — see its doc
    /// comment for why a fresh DI scope is used instead of the constructor-injected
    /// <c>_settingsService</c>.
    /// </summary>
    private Task<string?> GetApiKeyAsync()
    {
        _apiKeyProvider ??= CivitaiApiKeys.Resolve(_scopeFactory);
        return _apiKeyProvider.GetApiKeyAsync();
    }

    /// <summary>
    /// Checks whether a Civitai API token is configured. If not, opens a dialog
    /// for the user to enter one. Returns true when a token is available (either
    /// already configured or just provided), false when the user cancelled.
    /// </summary>
    private async Task<bool> EnsureCivitaiTokenAsync()
    {
        var existingKey = await GetApiKeyAsync();
        if (!string.IsNullOrWhiteSpace(existingKey))
            return true;

        if (_dialogService is null) return false;

        // The dialog service marshals to the UI thread internally.
        var result = await _dialogService.ShowCivitaiTokenDialogAsync();

        if (!result.IsSaved || string.IsNullOrWhiteSpace(result.TokenText))
            return false;

        // Persist the token (encrypted) via the settings service
        if (_settingsService is not null)
        {
            await _settingsService.SetCivitaiApiKeyAsync(result.TokenText);
            _logger?.Info(LogCategory.General, "CivitaiToken",
                "Civitai API token saved from download prompt");
        }

        return true;
    }

    private Task CopyToClipboardAsync(string text) => _clipboard.SetTextAsync(text);

    #endregion
}
