using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Views.Dialogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using System.Collections.ObjectModel;
// Two unrelated systems own a "ThumbnailRequest": ThumbnailOrchestrator's on-disk dataset/gallery
// cache, and the library-sync thumbnail pipeline. The tile means the latter, and says so.
using ThumbnailRequest = DiffusionNexus.Service.Services.Sync.Thumbnails.ThumbnailRequest;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for a single model tile in the LoRA Helper grid.
/// </summary>
public partial class ModelTileViewModel : ViewModelBase
{
    // #438: constructor-injected replacements for the former App.Services locator
    // calls woven through this file. Nullable services degrade exactly as the old
    // null-conditional locator lookups did; the scheduler/clipboard fall back to
    // shared production instances so design-time / demo / grouping-test construction
    // (which pass no dependency bundle) keeps working. The scope factory is a
    // singleton whose CreateScope() is still called per DB operation below.
    private readonly IUnifiedLogger? _logger;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IDialogService? _dialogService;
    private readonly IClipboardService _clipboard = AvaloniaClipboardService.Instance;
    private readonly IUiScheduler _uiScheduler = AvaloniaUiScheduler.Instance;

    /// <summary>
    /// Design-time / demo constructor. Produces a tile with no backing services —
    /// commands that need the database or dialogs no-op, and the clipboard/scheduler
    /// use their shared production defaults.
    /// </summary>
    public ModelTileViewModel() { }

    /// <summary>
    /// Runtime constructor. <paramref name="dependencies"/> carries the services the
    /// tile previously pulled from <c>App.Services</c> (#438); it is optional so the
    /// static factory methods and grouping tests keep compiling.
    /// </summary>
    public ModelTileViewModel(ModelTileDependencies? dependencies)
    {
        if (dependencies is { } d)
        {
            _logger = d.Logger;
            _scopeFactory = d.ScopeFactory;
            _dialogService = d.DialogService;
            _clipboard = d.Clipboard ?? AvaloniaClipboardService.Instance;
            _uiScheduler = d.UiScheduler ?? AvaloniaUiScheduler.Instance;
        }
    }
    /// <summary>
    /// The size above which a stored thumbnail is treated as legacy bloat and re-encoded on first
    /// read (1 MB). Nothing produces such a BLOB any more — the provider's output is a 450px JPEG,
    /// tens of KB — but the old naive <c>width=300</c> fetch stored whatever the CDN felt like
    /// returning, up to 25 MB. The sync step will never shrink those rows: it only selects images
    /// that have <i>no</i> thumbnail. So the tile's first read of one is the only occasion there is.
    /// </summary>
    private const int MaxThumbnailBytes = 1_048_576;

    /// <summary>
    /// Whether the tile's container is currently attached to the visual tree.
    /// Drives lazy thumbnail decoding so off-screen tiles don't keep decoded
    /// <see cref="Bitmap"/> instances in memory — critical at scale (4K+ LoRAs).
    /// Set by <see cref="Activate"/> / <see cref="Deactivate"/>.
    /// </summary>
    private bool _isActive;

    /// <summary>
    /// Raised after the model (and all its grouped versions) has been deleted from disk and DB.
    /// The parent view model should remove this tile from its collections.
    /// </summary>
    public event EventHandler? Deleted;

    /// <summary>
    /// Allows external collaborators (e.g. the detail-view "Delete Metadata"
    /// command) to signal that this tile should be removed from the grid after
    /// they have already removed the underlying record themselves.
    /// </summary>
    public void RaiseDeleted() => Deleted?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Raised when the user wants to view the detail panel for this tile.
    /// The parent view model should open the detail view.
    /// </summary>
    public event EventHandler? DetailRequested;

    #region Observable Properties

    /// <summary>
    /// Cancels any in-flight thumbnail download/load when the selected version changes.
    /// </summary>
    private CancellationTokenSource? _thumbnailCts;

    /// <summary>
    /// The model entity.
    /// </summary>
    [ObservableProperty]
    private Model? _modelEntity;

    /// <summary>
    /// The currently selected version.
    /// </summary>
    [ObservableProperty]
    private ModelVersion? _selectedVersion;

    /// <summary>
    /// The thumbnail image to display.
    /// </summary>
    [ObservableProperty]
    private Bitmap? _thumbnailImage;

    /// <summary>
    /// Whether metadata is being loaded.
    /// </summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Whether the tile is selected.
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    #endregion

    #region Grouping

    /// <summary>
    /// All model entities in this group (same Civitai page).
    /// For ungrouped models this contains just the single model.
    /// </summary>
    private List<Model> _allGroupedModels = [];

    /// <summary>
    /// All <see cref="Model.Id"/> values that map to this tile in the database,
    /// including re-discovery duplicates that <see cref="TileGroupingHelper.DeduplicateModels"/>
    /// dropped from <see cref="_allGroupedModels"/>. Used by destructive operations
    /// (e.g. "Delete Metadata") so dropped duplicate rows are not left behind in the DB.
    /// </summary>
    private HashSet<int> _allDatabaseModelIds = [];

    /// <summary>
    /// When set, this tile represents the model's files inside one specific LoRA-source
    /// location. Maps each <see cref="ModelVersion.Id"/> that has a file in this
    /// location to that file. The version switcher only exposes those versions, and
    /// destructive ops only touch files in this map. Set by <see cref="FromModelInLocation"/>
    /// for the Installed-tab fan-out (issue #380).
    /// </summary>
    private Dictionary<int, ModelFile>? _scopedFilesByVersionId;

    /// <summary>
    /// The LoRA-source root path this tile represents, or null for non-scoped tiles.
    /// </summary>
    public string? ScopedRootPath { get; private set; }

    /// <summary>
    /// The <see cref="ModelFile"/> in this tile's location that backs the currently
    /// <see cref="SelectedVersion"/>, or null for non-scoped tiles / unknown versions.
    /// Used by <c>FileName</c>, <c>OpenFolder</c>, and the per-version delete flow.
    /// </summary>
    public ModelFile? ScopedFile =>
        SelectedVersion is not null && _scopedFilesByVersionId is not null
            ? _scopedFilesByVersionId.GetValueOrDefault(SelectedVersion.Id)
            : null;

    /// <summary>
    /// The <see cref="ModelFile"/> in this tile's location backing the given version,
    /// or null for non-scoped tiles / versions without a file in this location.
    /// Unlike <see cref="ScopedFile"/>, independent of <see cref="SelectedVersion"/>.
    /// </summary>
    public ModelFile? GetScopedFileForVersion(int versionId) =>
        _scopedFilesByVersionId?.GetValueOrDefault(versionId);

    /// <summary>
    /// True when this tile is scoped to a single LoRA-source location.
    /// </summary>
    public bool IsLocationScoped => _scopedFilesByVersionId is not null;

    /// <summary>
    /// Updates the tile after a new version has been downloaded and persisted.
    /// Replaces or adds the refreshed model in the grouped models list, then triggers
    /// a full UI rebuild via the <see cref="ModelEntity"/> property change.
    /// </summary>
    public void RefreshModelData(Model refreshedModel)
    {
        var index = _allGroupedModels.FindIndex(m => m.Id == refreshedModel.Id);
        if (index >= 0)
        {
            _allGroupedModels[index] = refreshedModel;
        }
        else
        {
            _allGroupedModels.Add(refreshedModel);
        }

        // Location-scoped tiles (#380) filter Versions through the file map
        // snapshot taken at tile-build time. Fold in the refreshed model's files
        // that live under this tile's root so a just-downloaded version isn't
        // dropped when the version list rebuilds below.
        if (_scopedFilesByVersionId is not null && !string.IsNullOrEmpty(ScopedRootPath))
        {
            foreach (var version in refreshedModel.Versions)
            {
                if (_scopedFilesByVersionId.ContainsKey(version.Id)) continue;

                var fileInRoot = version.Files.FirstOrDefault(f =>
                    !string.IsNullOrWhiteSpace(f.LocalPath) &&
                    IsPathUnderRoot(f.LocalPath!, ScopedRootPath));
                if (fileInRoot is not null)
                {
                    _scopedFilesByVersionId[version.Id] = fileInRoot;
                }
            }
        }

        // Pick the richest model as primary (same logic as FromModelGroup)
        var primary = _allGroupedModels
            .OrderByDescending(m => m.CivitaiId.HasValue)
            .ThenByDescending(m => m.Versions.Sum(v => v.Images.Count))
            .ThenByDescending(m => m.LastSyncedAt)
            .First();

        if (ReferenceEquals(ModelEntity, primary))
        {
            // The generated setter short-circuits on an unchanged reference, but
            // the group's version/file content changed — rebuild explicitly.
            OnModelEntityChanged(primary);
        }
        else
        {
            ModelEntity = primary;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is the root itself or points inside it.
    /// </summary>
    private static bool IsPathUnderRoot(string path, string root)
    {
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        return path.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase)
               && (path.Length == trimmedRoot.Length
                   || path[trimmedRoot.Length] is '\\' or '/');
    }

    /// <summary>
    /// Updates the in-memory remote version count for every grouped model and
    /// re-raises the "additional versions" badge properties so the tile reflects
    /// the latest Civitai response without waiting for a full tile rebuild.
    /// Call this after the detail panel fetches the model from Civitai.
    /// </summary>
    public void UpdateRemoteVersionCount(int totalVersionCount, DateTime checkedAtUtc)
    {
        foreach (var model in _allGroupedModels)
        {
            model.TotalVersionCount = totalVersionCount;
            model.LastCheckedForUpdatesUtc = checkedAtUtc;
        }

        OnPropertyChanged(nameof(AdditionalVersionCount));
        OnPropertyChanged(nameof(HasAdditionalVersions));
        OnPropertyChanged(nameof(AdditionalVersionsBadge));
        OnPropertyChanged(nameof(AdditionalVersionsTooltip));
    }

    #endregion

    #region Collections

    /// <summary>
    /// Available versions for the version selector.
    /// </summary>
    public ObservableCollection<ModelVersion> Versions { get; } = [];

    /// <summary>
    /// Version toggle buttons for the UI.
    /// </summary>
    public ObservableCollection<VersionButtonViewModel> VersionButtons { get; } = [];

    #endregion

    #region Base Model Display Mappings

    private static readonly Dictionary<string, (string Short, string? Icon)> BaseModelMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SD 1.5"] = ("1.5", null),
        ["SDXL 1.0"] = ("XL", null),
        ["SDXL 0.9"] = ("XL 0.9", null),
        ["SDXL Turbo"] = ("XL ⚡", null),
        ["SDXL Lightning"] = ("XL ⚡⚡", null),
        ["Pony"] = ("Pony", "🐎"),
        ["Illustrious"] = ("IL", null),
        ["Flux.1 S"] = ("Flux S", null),
        ["Flux.1 D"] = ("F.1D", null),
        ["Z-Image-Turbo"] = ("ZIT", "ZI⚡"),
        ["Wan Video 14B t2v"] = ("Wan 14B", "🎬"),
        ["Wan Video 1.3B t2v"] = ("Wan 1.3B", "🎬 1.3"),
        ["NoobAI"] = ("Noob", null),
        ["???"] = ("???", null), // Unknown base model indicator
    };

    #endregion

    #region Computed Properties

    /// <summary>
    /// Model name for display.
    /// </summary>
    public string DisplayName => ModelEntity?.Name ?? SelectedVersion?.Name ?? "Unknown Model";

    /// <summary>
    /// The filename on disk (without extension).
    /// </summary>
    public string FileName
    {
        get
        {
            var file = ScopedFile
                       ?? SelectedVersion?.Files?.FirstOrDefault(f => f.IsPrimary)
                       ?? SelectedVersion?.Files?.FirstOrDefault();
            if (file?.FileName is not null)
            {
                // Remove extension
                var name = file.FileName;
                var lastDot = name.LastIndexOf('.');
                return lastDot > 0 ? name[..lastDot] : name;
            }
            return DisplayName; // Fall back to display name if no file info
        }
    }

    /// <summary>
    /// The full filename on disk without extension (e.g., "Ellie_Williams_-_The_Last_of_Us_Part_I-ZIT").
    /// Used for copying to clipboard so users can search in ComfyUI.
    /// </summary>
    public string RealFileName
    {
        get
        {
            var file = ScopedFile
                       ?? SelectedVersion?.Files?.FirstOrDefault(f => f.IsPrimary)
                       ?? SelectedVersion?.Files?.FirstOrDefault();
            if (file?.FileName is null) return DisplayName;

            var name = file.FileName;
            var lastDot = name.LastIndexOf('.');
            return lastDot > 0 ? name[..lastDot] : name;
        }
    }

    /// <summary>
    /// Model type display (e.g., "LORA", "Checkpoint").
    /// </summary>
    public string ModelTypeDisplay => ModelEntity?.Type.ToString().ToUpperInvariant() ?? "UNKNOWN";

    /// <summary>
    /// Base models display string with short names.
    /// Shows the base model for the currently selected version.
    /// </summary>
    public string BaseModelsDisplay
    {
        get
        {
            if (SelectedVersion is not null)
            {
                return FormatBaseModel(SelectedVersion.BaseModelRaw);
            }
            return "???";
        }
    }

    /// <summary>
    /// Whether this model has NSFW content.
    /// </summary>
    public bool IsNsfw => ModelEntity?.IsNsfw ?? false;

    /// <summary>
    /// Whether this model has multiple versions.
    /// </summary>
    public bool HasMultipleVersions => Versions.Count > 1;

    /// <summary>
    /// Whether this tile groups multiple model entities (same Civitai page).
    /// </summary>
    public bool IsGrouped => _allGroupedModels.Count > 1;

    /// <summary>
    /// Number of individual model entities in this tile (1 for ungrouped, >1 for grouped).
    /// Used for accurate total/filtered counts that reflect individual LoRAs, not grouped cards.
    /// </summary>
    public int ModelCount => _allGroupedModels.Count > 0 ? _allGroupedModels.Count : 1;

    /// <summary>
    /// Version count display.
    /// </summary>
    public string VersionCountDisplay => HasMultipleVersions
        ? $"{Versions.Count} versions"
        : string.Empty;

    /// <summary>
    /// Number of additional versions of this Civitai model that exist remotely
    /// but are not present locally. Computed as
    /// <c>max(TotalVersionCount - ownedVersions, 0)</c> across all grouped models
    /// for the same Civitai model page.
    /// Returns 0 (badge hidden) when no update check has ever run for any of the
    /// grouped models — see <see cref="Model.LastCheckedForUpdatesUtc"/>.
    /// </summary>
    public int AdditionalVersionCount
    {
        get
        {
            if (_allGroupedModels.Count == 0)
            {
                return 0;
            }

            // No badge until at least one grouped model has been checked against Civitai.
            if (!_allGroupedModels.Any(m => m.LastCheckedForUpdatesUtc.HasValue))
            {
                return 0;
            }

            var totalRemote = _allGroupedModels.Max(m => m.TotalVersionCount);
            var ownedLocal = _allGroupedModels.Sum(m => m.Versions.Count);
            var diff = totalRemote - ownedLocal;
            return diff > 0 ? diff : 0;
        }
    }

    /// <summary>
    /// True when there are additional, not-yet-downloaded versions of this
    /// Civitai model that the user could download.
    /// </summary>
    public bool HasAdditionalVersions => AdditionalVersionCount > 0;

    /// <summary>
    /// Short label for the "more versions available" badge (e.g. "+5").
    /// </summary>
    public string AdditionalVersionsBadge => HasAdditionalVersions
        ? $"+{AdditionalVersionCount}"
        : string.Empty;

    /// <summary>
    /// Tooltip text for the "more versions available" badge.
    /// </summary>
    public string AdditionalVersionsTooltip => AdditionalVersionCount switch
    {
        0 => string.Empty,
        1 => "1 more version available on Civitai",
        var n => $"{n} more versions available on Civitai",
    };

    /// <summary>
    /// Creator name.
    /// </summary>
    public string CreatorName => ModelEntity?.Creator?.Username ?? "Unknown";

    /// <summary>
    /// Download count display.
    /// </summary>
    public string DownloadCountDisplay
    {
        get
        {
            var count = SelectedVersion?.DownloadCount ?? ModelEntity?.TotalDownloads ?? 0;
            return count switch
            {
                >= 1_000_000 => $"{count / 1_000_000.0:F1}M",
                >= 1_000 => $"{count / 1_000.0:F1}K",
                _ => count.ToString()
            };
        }
    }

    /// <summary>
    /// Whether a thumbnail is available.
    /// </summary>
    public bool HasThumbnail => ThumbnailImage is not null;

    /// <summary>
    /// Whether to show placeholder.
    /// </summary>
    public bool ShowPlaceholder => !HasThumbnail && !IsLoading;

    /// <summary>
    /// Tag names collected from all grouped models for search filtering.
    /// Built once per <see cref="ModelEntity"/> change; no DB round-trip.
    /// </summary>
    public IReadOnlyList<string> TagNames { get; private set; } = [];

    #endregion

    #region Commands

    /// <summary>
    /// Open model details.
    /// </summary>
    [RelayCommand]
    private void OpenDetails()
    {
        DetailRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Copy trigger words to clipboard.
    /// </summary>
    [RelayCommand]
    private async Task CopyTriggerWordsAsync()
    {
        var triggerWords = SelectedVersion?.TriggerWordsText;
        if (string.IsNullOrWhiteSpace(triggerWords)) return;

        await CopyToClipboardAsync(triggerWords);
    }

    /// <summary>
    /// Copy the real filename (with extension) to clipboard for ComfyUI search.
    /// </summary>
    [RelayCommand]
    private async Task CopyFileNameAsync()
    {
        var fileName = RealFileName;
        if (string.IsNullOrWhiteSpace(fileName)) return;

        await CopyToClipboardAsync(fileName);
    }

    /// <summary>
    /// Copies text to the system clipboard via the injected clipboard seam.
    /// </summary>
    private Task CopyToClipboardAsync(string text) => _clipboard.SetTextAsync(text);

    /// <summary>
    /// Open model on Civitai. Tries multiple ID sources to build the URL:
    /// CivitaiId → CivitaiModelPageId → SelectedVersion.CivitaiId (version-level URL).
    /// Logs a warning to the Unified Console when no Civitai link is available.
    /// </summary>
    [RelayCommand]
    private void OpenOnCivitai()
    {
        // civitai.com strips NSFW content for unauthenticated visitors; the mirror at
        // civitai.red serves the full page. Route NSFW models there so the user
        // doesn't land on a half-blanked-out page.
        var host = ModelEntity?.IsNsfw == true ? "civitai.red" : "civitai.com";

        string? url = null;

        if (ModelEntity?.CivitaiId is { } modelCivitaiId)
        {
            url = $"https://{host}/models/{modelCivitaiId}";
        }
        else if (ModelEntity?.CivitaiModelPageId is { } pageId)
        {
            url = $"https://{host}/models/{pageId}";
        }
        else if (SelectedVersion?.CivitaiId is { } versionCivitaiId)
        {
            // Version-level ID — link to the version page directly
            url = $"https://{host}/api/v1/model-versions/{versionCivitaiId}";
        }

        if (url is null)
        {
            _logger?.Warn(LogCategory.General, "OpenOnCivitai",
                $"No Civitai link available for '{DisplayName}' — run 'Download Metadata' first to sync with Civitai.");
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
        {
            UseShellExecute = true
        });
    }

    /// <summary>
    /// Open containing folder.
    /// </summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var path = ScopedFile?.LocalPath
                   ?? SelectedVersion?.Files?.FirstOrDefault(f => f.LocalPath is not null)?.LocalPath;
        if (path is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(path);
        if (folder is not null && Directory.Exists(folder))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder)
            {
                UseShellExecute = true
            });
        }
    }

    /// <summary>
    /// Deletes the model from disk and database after user confirmation.
    /// For single-version tiles, shows a simple confirmation dialog.
    /// For multi-version tiles, shows a version picker so the user can choose which to delete.
    /// </summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        var logger = _logger;
        var dialogService = _dialogService;

        if (dialogService is null)
        {
            logger?.Error(LogCategory.General, "Delete", "Dialog service unavailable — cannot show confirmation.");
            return;
        }

        // Issue #380: a tile bound to a specific physical file deletes only that copy.
        if (ScopedFile is not null)
        {
            await DeleteScopedFileAsync(logger, dialogService);
            return;
        }

        var allVersions = Versions.ToList();

        if (allVersions.Count <= 1)
        {
            // Single version: simple confirm dialog
            await DeleteSingleVersionAsync(logger, dialogService);
        }
        else
        {
            // Multiple versions: show version picker
            await DeleteWithVersionPickerAsync(logger);
        }
    }

    /// <summary>
    /// Deletes the currently-selected version's file in this tile's LoRA location and its
    /// ModelFile row. Leaves the ModelVersion and Model intact so other locations and
    /// future re-downloads keep working. Removes the deleted version from the tile's
    /// switcher; if no versions remain in this location, raises <see cref="Deleted"/>.
    /// Issue #380.
    /// </summary>
    private async Task DeleteScopedFileAsync(IUnifiedLogger? logger, IDialogService dialogService)
    {
        var scoped = ScopedFile;
        var scopedVersion = SelectedVersion;
        if (scoped is null || scopedVersion is null) return;

        var path = scoped.LocalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            logger?.Warn(LogCategory.General, "Delete",
                $"Scoped tile for '{DisplayName}' has no LocalPath — nothing to delete.");
            return;
        }

        var confirmed = await dialogService.ShowConfirmAsync(
            "Delete LoRA copy",
            $"Delete this copy of '{DisplayName}' from disk?\n\n{path}\n\nOnly the file at this location is removed; other copies stay intact. This action cannot be undone.");

        if (!confirmed) return;

        try
        {
            DeleteFilesFromDisk(logger, [path]);

            using var scope = _scopeFactory!.CreateScope();
            var dbContext = scope.ServiceProvider
                .GetRequiredService<IDbContextFactory<DiffusionNexusCoreDbContext>>()
                .CreateDbContext();
            await using (dbContext)
            {
                var dbFile = await dbContext.ModelFiles.FirstOrDefaultAsync(f => f.Id == scoped.Id);
                if (dbFile is not null)
                {
                    dbContext.ModelFiles.Remove(dbFile);
                    await dbContext.SaveChangesAsync();
                    logger?.Info(LogCategory.General, "Delete",
                        $"Removed ModelFile Id={scoped.Id} ({path}) for '{DisplayName}'.");
                }
            }

            // Drop the deleted version from this tile's in-memory state so the
            // version switcher refreshes without a full grid reload.
            _scopedFilesByVersionId?.Remove(scopedVersion.Id);

            if (_scopedFilesByVersionId is null || _scopedFilesByVersionId.Count == 0)
            {
                Deleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // Trigger OnModelEntityChanged to rebuild Versions / VersionButtons.
                OnModelEntityChanged(ModelEntity);
            }
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.General, "Delete",
                $"Failed to delete copy at '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Simple confirmation + delete for single-version tiles.
    /// </summary>
    private async Task DeleteSingleVersionAsync(IUnifiedLogger? logger, IDialogService dialogService)
    {
        var filePaths = CollectAllLocalFiles();
        var fileList = filePaths.Count > 0
            ? Path.GetFileName(filePaths[0])
            : "(no local file found)";

        var confirmed = await dialogService.ShowConfirmAsync(
            "Delete LoRA",
            $"Delete '{DisplayName}' from disk?\n\n{fileList}\n\nThis action cannot be undone.");

        if (!confirmed) return;

        await ExecuteFullDeletion(logger, filePaths, GetAllModelIds());
    }

    /// <summary>
    /// Shows a version picker dialog for multi-version grouped tiles.
    /// </summary>
    private async Task DeleteWithVersionPickerAsync(IUnifiedLogger? logger)
    {
        if (_dialogService is null)
        {
            logger?.Error(LogCategory.General, "Delete", "Dialog service unavailable — cannot show version picker.");
            return;
        }

        var allModels = _allGroupedModels.Count > 0
            ? _allGroupedModels
            : ModelEntity is not null ? new List<Model> { ModelEntity } : [];

        var result = await _dialogService.ShowSelectLoraVersionsToDeleteDialogAsync(
            DisplayName, Versions, allModels);

        if (result is null || !result.Confirmed || result.SelectedItems.Count == 0)
            return;

        // Collect file paths and version IDs from selected items
        var filePaths = result.SelectedItems
            .Where(i => !string.IsNullOrWhiteSpace(i.LocalPath))
            .Select(i => i.LocalPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (result.DeleteAll)
        {
            // Full delete: remove entire model entities
            await ExecuteFullDeletion(logger, filePaths, GetAllModelIds());
        }
        else
        {
            // Partial delete: remove only selected versions, keep models alive
            var versionIdsToRemove = result.SelectedItems
                .Select(i => i.Version.Id)
                .Distinct()
                .ToList();

            await ExecutePartialDeletion(logger, filePaths, versionIdsToRemove);
        }

        // If only some versions were deleted, refresh the tile instead of removing it
        if (!result.DeleteAll)
        {
            var deletedVersionIds = new HashSet<int>(result.SelectedItems.Select(i => i.Version.Id));

            // Remove deleted versions from the in-memory collections
            foreach (var item in result.SelectedItems)
            {
                Versions.Remove(item.Version);
            }

            // Remove corresponding version buttons
            var buttonsToRemove = VersionButtons
                .Where(b => deletedVersionIds.Contains(b.Version.Id))
                .ToList();
            foreach (var button in buttonsToRemove)
            {
                VersionButtons.Remove(button);
            }

            // Remove deleted versions from in-memory model entities
            foreach (var model in _allGroupedModels)
            {
                var versionsToRemove = model.Versions
                    .Where(v => deletedVersionIds.Contains(v.Id))
                    .ToList();
                foreach (var v in versionsToRemove)
                {
                    model.Versions.Remove(v);
                }
            }

            // Remove models that have no versions left
            _allGroupedModels.RemoveAll(m => m.Versions.Count == 0);

            // If no versions left after partial delete, treat as full delete
            if (Versions.Count == 0)
            {
                Deleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                // If the currently selected version was deleted, switch to the first remaining one
                if (SelectedVersion is null || deletedVersionIds.Contains(SelectedVersion.Id))
                {
                    var firstButton = VersionButtons.FirstOrDefault();
                    if (firstButton is not null)
                    {
                        OnVersionButtonSelected(firstButton);
                    }
                }

                // Re-pick the primary ModelEntity from remaining models
                var primary = _allGroupedModels
                    .OrderByDescending(m => m.CivitaiId.HasValue)
                    .ThenByDescending(m => m.Versions.Sum(v => v.Images.Count))
                    .ThenByDescending(m => m.LastSyncedAt)
                    .FirstOrDefault();

                if (primary is not null && primary != ModelEntity)
                {
                    ModelEntity = primary;
                }

                // Refresh UI with remaining versions
                OnPropertyChanged(nameof(HasMultipleVersions));
                OnPropertyChanged(nameof(IsGrouped));
                OnPropertyChanged(nameof(VersionCountDisplay));
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(FileName));
                OnPropertyChanged(nameof(BaseModelsDisplay));
                OnPropertyChanged(nameof(DownloadCountDisplay));
                OnPropertyChanged(nameof(TagNames));
            }
        }
    }

    /// <summary>
    /// Executes a full deletion: removes files from disk and entire model entities from the database.
    /// </summary>
    private async Task ExecuteFullDeletion(IUnifiedLogger? logger, List<string> filePaths, List<int> modelIds)
    {
        try
        {
            DeleteFilesFromDisk(logger, filePaths);

            // Remove entire model entities from the database
            if (modelIds.Count > 0)
            {
                using var scope = _scopeFactory!.CreateScope();
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

                foreach (var modelId in modelIds)
                {
                    var dbModel = await unitOfWork.Models.GetByIdAsync(modelId);
                    if (dbModel is not null)
                    {
                        unitOfWork.Models.Remove(dbModel);
                    }
                }

                await unitOfWork.SaveChangesAsync();
                logger?.Info(LogCategory.General, "Delete",
                    $"Removed {modelIds.Count} model record(s) from database for '{DisplayName}'.");
            }

            // Full delete → remove tile
            Deleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.General, "Delete",
                $"Failed to delete '{DisplayName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Executes a partial deletion: removes files from disk and only the selected versions
    /// from the database. Models that lose all their versions are also removed.
    /// </summary>
    private async Task ExecutePartialDeletion(IUnifiedLogger? logger, List<string> filePaths, List<int> versionIds)
    {
        try
        {
            DeleteFilesFromDisk(logger, filePaths);

            if (versionIds.Count > 0)
            {
                using var scope = _scopeFactory!.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DiffusionNexusCoreDbContext>>()
                    .CreateDbContext();
                await using (dbContext)
                {
                    // Remove only the selected versions (cascade handles Files, Images, TriggerWords)
                    var versionsToDelete = await dbContext.ModelVersions
                        .Where(v => versionIds.Contains(v.Id))
                        .ToListAsync();

                    dbContext.ModelVersions.RemoveRange(versionsToDelete);
                    await dbContext.SaveChangesAsync();

                    // Remove any orphaned models (models that now have zero versions)
                    var affectedModelIds = versionsToDelete
                        .Select(v => v.ModelId)
                        .Distinct()
                        .ToList();

                    var orphanedModels = await dbContext.Models
                        .Where(m => affectedModelIds.Contains(m.Id))
                        .Where(m => !dbContext.ModelVersions.Any(v => v.ModelId == m.Id))
                        .ToListAsync();

                    if (orphanedModels.Count > 0)
                    {
                        dbContext.Models.RemoveRange(orphanedModels);
                        await dbContext.SaveChangesAsync();
                    }

                    logger?.Info(LogCategory.General, "Delete",
                        $"Removed {versionsToDelete.Count} version(s) from database for '{DisplayName}'.");
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.General, "Delete",
                $"Failed to delete versions from '{DisplayName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Deletes files from disk, logging successes and failures.
    /// </summary>
    private static void DeleteFilesFromDisk(IUnifiedLogger? logger, List<string> filePaths)
    {
        foreach (var path in filePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    logger?.Info(LogCategory.General, "Delete", $"Deleted file: {path}");
                }
            }
            catch (Exception ex)
            {
                logger?.Error(LogCategory.General, "Delete",
                    $"Failed to delete file '{path}': {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Gets all model entity IDs across the group, including re-discovery
    /// duplicates dropped during grouping. Use this for any destructive DB op.
    /// </summary>
    public List<int> GetAllModelIds()
    {
        if (_allDatabaseModelIds.Count > 0)
            return _allDatabaseModelIds.ToList();

        if (_allGroupedModels.Count > 0)
            return _allGroupedModels.Select(m => m.Id).ToList();

        return ModelEntity?.Id is { } id ? [id] : [];
    }

    /// <summary>
    /// Collects all local file paths across all grouped models and their versions.
    /// For scoped tiles (issue #380), returns just the one file this tile represents.
    /// </summary>
    private List<string> CollectAllLocalFiles()
    {
        if (ScopedFile is not null)
        {
            return string.IsNullOrWhiteSpace(ScopedFile.LocalPath)
                ? []
                : [ScopedFile.LocalPath];
        }

        var models = _allGroupedModels.Count > 0
            ? _allGroupedModels
            : ModelEntity is not null ? [ModelEntity] : [];

        return models
            .SelectMany(m => m.Versions)
            .SelectMany(v => v.Files)
            .Where(f => !string.IsNullOrWhiteSpace(f.LocalPath))
            .Select(f => f.LocalPath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    #endregion

    #region Lifecycle

    partial void OnModelEntityChanged(Model? value)
    {
        // Populate versions from all grouped models (or just the primary model)
        Versions.Clear();
        VersionButtons.Clear();

        var allVersions = _scopedFilesByVersionId is not null
            ? _allGroupedModels
                .SelectMany(m => m.Versions)
                .Where(v => _scopedFilesByVersionId.ContainsKey(v.Id))
            : _allGroupedModels.Count > 0
                ? _allGroupedModels.SelectMany(m => m.Versions)
                : value?.Versions ?? Enumerable.Empty<ModelVersion>();

        // Deduplicate versions that share the same primary filename (re-discovery duplicates).
        // Keep the version with the richest data per filename.
        var uniqueVersions = DeduplicateVersions(allVersions);

        var isGrouped = _allGroupedModels.Count > 1;

        foreach (var version in uniqueVersions.OrderByDescending(v => v.CreatedAt))
        {
            Versions.Add(version);

            // Create button with short label from base model
            var (label, icon) = GetVersionButtonInfo(version);
            var tooltip = BuildVersionTooltip(version, isGrouped);
            var button = new VersionButtonViewModel(version, label, icon, tooltip, OnVersionButtonSelected);
            VersionButtons.Add(button);
        }

        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(ModelTypeDisplay));
        OnPropertyChanged(nameof(BaseModelsDisplay));
        OnPropertyChanged(nameof(IsNsfw));
        OnPropertyChanged(nameof(HasMultipleVersions));
        OnPropertyChanged(nameof(IsGrouped));
        OnPropertyChanged(nameof(VersionCountDisplay));
        OnPropertyChanged(nameof(AdditionalVersionCount));
        OnPropertyChanged(nameof(HasAdditionalVersions));
        OnPropertyChanged(nameof(AdditionalVersionsBadge));
        OnPropertyChanged(nameof(AdditionalVersionsTooltip));
        OnPropertyChanged(nameof(CreatorName));
        OnPropertyChanged(nameof(DownloadCountDisplay));

        // Build tag index from all grouped models
        var models = _allGroupedModels.Count > 0
            ? _allGroupedModels
            : value is not null ? [value] : [];

        TagNames = models
            .SelectMany(m => m.Tags)
            .Select(mt => mt.Tag?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()!;
        OnPropertyChanged(nameof(TagNames));

        // Auto-select first version
        if (VersionButtons.Count > 0)
        {
            OnVersionButtonSelected(VersionButtons.First());
        }
    }

    partial void OnSelectedVersionChanged(ModelVersion? value)
    {
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(RealFileName));
        OnPropertyChanged(nameof(BaseModelsDisplay));
        OnPropertyChanged(nameof(DownloadCountDisplay));
        // Only decode when the tile is on screen. The view's AttachedToVisualTree
        // handler calls Activate() which triggers a load if needed.
        if (_isActive) LoadThumbnailFromVersion();
    }

    /// <summary>
    /// Called by <see cref="Views.Controls.ModelTileControl"/> when the tile's container
    /// is attached to the visual tree. Triggers a thumbnail load if one isn't already
    /// materialized. Idempotent — safe to call repeatedly.
    /// </summary>
    public void Activate()
    {
        if (_isActive) return;
        _isActive = true;
        // Re-decode from the in-memory ThumbnailData when available (fast path),
        // or hit the DB / URL fallback when not.
        if (ThumbnailImage is null)
        {
            LoadThumbnailFromVersion();
        }
    }

    /// <summary>
    /// Called when the tile scrolls out of the visual tree. Drops both the decoded
    /// <see cref="Bitmap"/> (the multi-MB allocation) and the encoded bytes on the
    /// underlying <see cref="ModelImage"/>, then re-flags the image as deferred so
    /// the next <see cref="Activate"/> goes through the DB lazy-load path. Cancels
    /// any in-flight thumbnail download so we don't keep streaming for off-screen tiles.
    /// </summary>
    public void Deactivate()
    {
        if (!_isActive) return;
        _isActive = false;

        try { _thumbnailCts?.Cancel(); } catch { /* already disposed */ }

        ThumbnailImage = null;

        // Drop the encoded bytes too — they're persisted on disk in the DB and can be
        // re-fetched on demand. Without this, scrolling through 4K tiles leaves their
        // ThumbnailData in memory (up to 1 MB each). Setting it back to the deferred
        // sentinel makes the next Activate() take the lazy-load-from-DB path.
        var primaryImage = SelectedVersion?.PrimaryImage;
        if (primaryImage is not null
            && primaryImage.ThumbnailData is { Length: > 0 }
            && !primaryImage.IsThumbnailDeferred)
        {
            primaryImage.ThumbnailData = ModelImage.ThumbnailNotLoadedSentinel;
        }
    }

    partial void OnThumbnailImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Deduplicates versions that share the same primary filename (re-discovery duplicates).
    /// Keeps the version with the richest metadata per unique filename.
    /// </summary>
    private static List<ModelVersion> DeduplicateVersions(IEnumerable<ModelVersion> versions)
    {
        var byFile = new Dictionary<string, ModelVersion>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in versions)
        {
            var fileName = version.PrimaryFile?.FileName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                // No file info — keep using a unique synthetic key
                byFile[$"__no_file_{version.Id}_{byFile.Count}"] = version;
                continue;
            }

            if (byFile.TryGetValue(fileName, out var existing))
            {
                // Keep the one with CivitaiId, then more images
                if (version.CivitaiId.HasValue && !existing.CivitaiId.HasValue)
                    byFile[fileName] = version;
                else if (version.Images.Count > existing.Images.Count)
                    byFile[fileName] = version;
            }
            else
            {
                byFile[fileName] = version;
            }
        }

        return byFile.Values.ToList();
    }

    private void OnVersionButtonSelected(VersionButtonViewModel selected)
    {
        // Update all button states
        foreach (var button in VersionButtons)
        {
            button.IsSelected = ReferenceEquals(button, selected);
        }
        
        // Update selected version
        SelectedVersion = selected.Version;
    }

    private static (string Label, string? Icon) GetVersionButtonInfo(ModelVersion version)
    {
        // Try to get short label from base model
        if (!string.IsNullOrWhiteSpace(version.BaseModelRaw))
        {
            if (BaseModelMappings.TryGetValue(version.BaseModelRaw, out var mapping))
            {
                return (mapping.Short, mapping.Icon);
            }

            // Truncate if too long
            var baseModel = version.BaseModelRaw;
            if (baseModel.Length > 8)
            {
                return (baseModel[..7] + "…", null);
            }
            return (baseModel, null);
        }

        // Fall back to version name
        if (!string.IsNullOrWhiteSpace(version.Name))
        {
            var name = version.Name;
            if (name.Length > 8)
            {
                return (name[..7] + "…", null);
            }
            return (name, null);
        }

        return ("???", null);
    }

    /// <summary>
    /// Builds a rich tooltip for a version button showing version name and filename.
    /// </summary>
    private static string BuildVersionTooltip(ModelVersion version, bool isGrouped)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(version.Name))
        {
            parts.Add(version.Name);
        }

        if (isGrouped)
        {
            var file = version.PrimaryFile;
            if (file is not null && !string.IsNullOrWhiteSpace(file.FileName))
            {
                parts.Add($"File: {file.FileName}");
            }

            if (!string.IsNullOrWhiteSpace(version.BaseModelRaw))
            {
                parts.Add($"Base: {version.BaseModelRaw}");
            }
        }

        return parts.Count > 0 ? string.Join("\n", parts) : "Unknown version";
    }

    private static string FormatBaseModel(string? baseModel)
    {
        if (string.IsNullOrWhiteSpace(baseModel))
        {
            return "???";
        }

        if (BaseModelMappings.TryGetValue(baseModel, out var mapping))
        {
            return mapping.Icon is not null
                ? $"{mapping.Icon} {mapping.Short}"
                : mapping.Short;
        }

        // Return truncated original if no mapping
        return baseModel.Length > 12 ? baseModel[..11] + "…" : baseModel;
    }

    private void LoadThumbnailFromVersion()
    {
        // Cancel any in-flight thumbnail operation from a previous version switch
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        var primaryImage = SelectedVersion?.PrimaryImage;

        if (primaryImage?.ThumbnailData is { Length: > 0 } data && !primaryImage.IsThumbnailDeferred)
        {
            // Thumbnail BLOB already in memory — decode off the UI thread (downscaled,
            // no JPEG round-trip), then marshal only the property assignment back.
            // Guard with `ct` since Activate/Deactivate/version-switch can make this
            // in-flight decode stale before it reaches the UI thread.
            var image = primaryImage;
            _ = Task.Run(async () =>
            {
                var decode = TryCreateTileBitmap(data);
                if (ct.IsCancellationRequested) return;

                if (ShouldMarkCorrupt(data, decode.Outcome))
                {
                    // The placeholder stays: there is nothing to show, and now there is a record
                    // of why. The next activation takes the fetch branch, because the bytes are gone.
                    await MarkThumbnailCorruptAsync(image, data).ConfigureAwait(false);
                    return;
                }

                _uiScheduler.Post(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    ThumbnailImage = decode.Bitmap;
                });
            });
        }
        else if (primaryImage is not null && primaryImage.IsThumbnailDeferred)
        {
            // Thumbnail exists in DB but was not loaded (lightweight query) — fetch it on demand
            ThumbnailImage = null;
            _ = LazyLoadThumbnailFromDbAsync(primaryImage, ct);
        }
        else if (primaryImage is not null && IsFetchableUrl(primaryImage.Url))
        {
            // No BLOB cached yet — fetch through the provider in the background, but only if the
            // last attempt on this row says another one is worth making. Scrolling is not a reason
            // to retry: without this gate a poster that 404s costs a GET, a DI scope and a
            // SaveChanges every single time the tile passes the viewport, forever.
            ThumbnailImage = null;
            if (IsScrollFetchDue(primaryImage, DateTimeOffset.UtcNow))
                _ = DownloadThumbnailAsync(primaryImage, allowVideoDownload: false, ct);
        }
        else
        {
            // Everything else — no URL, a file:// preview, or a user-thumbnail:// row whose BLOB
            // has gone — lands here: look for a preview image next to the model file. Nothing is
            // fetched and nothing throws, which is the point; the old "any URL that is not file://"
            // condition sent user-thumbnail:// rows into an HTTP request built out of a scheme
            // nobody can serve.
            ThumbnailImage = null;
            _ = TryLoadLocalPreviewAsync(ct);
        }
    }

    /// <summary>
    /// Whether <paramref name="url"/> is something the thumbnail provider can fetch over the
    /// network. The database holds three URL shapes and only this one is a URL in the sense of
    /// "go and get it": <c>file://</c> points at disk (and is malformed by construction on
    /// Windows), and <c>user-thumbnail://</c> is a synthetic id for bytes that are already the
    /// record.
    /// </summary>
    internal static bool IsFetchableUrl(string? url) =>
        url is not null
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a stored BLOB is the legacy oversize kind that should be re-encoded on read.
    /// See <see cref="MaxThumbnailBytes"/> for why the tile is the only place this can happen.
    /// </summary>
    internal static bool NeedsOversizeSelfHeal(byte[]? data) => data is not null && data.Length > MaxThumbnailBytes;

    /// <summary>
    /// Whether a self-heal re-encode is worth writing back: only when it actually made the row
    /// smaller.
    /// </summary>
    /// <remarks>
    /// <see cref="NeedsOversizeSelfHeal"/> measures the stored bytes, so a BLOB that cannot shrink
    /// stays over the threshold and comes back through this path on every activation of the tile.
    /// Without this check that means a decode, a JPEG encode, a scope and a SaveChanges each time,
    /// forever, for a row that never changes. Such rows exist: an already-narrow image (under
    /// <see cref="ThumbnailCodec.TargetWidth"/>, so no resize) that is simply enormous, and a
    /// photographic source whose JPEG re-encode is no smaller than the PNG it came from.
    /// </remarks>
    internal static bool ShouldPersistSelfHeal(
        byte[] original, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] ThumbnailPayload? reencoded) =>
        reencoded is not null && reencoded.Data.Length < original.Length;

    /// <summary>
    /// Whether the scroll path may spend a request on this row, judged by the same policy the sync
    /// step uses — hard failures never, soft ones after the retry window, a dropped corrupt BLOB
    /// immediately.
    /// </summary>
    /// <remarks>
    /// <c>force: false</c> is the whole point: this is the tile deciding on its own, not the user
    /// asking. <see cref="TryDownloadMissingThumbnailAsync"/> — the one path a person initiates —
    /// does not come through here at all, because a user who clicks "download the missing
    /// thumbnail" has overruled every window by asking.
    /// </remarks>
    internal static bool IsScrollFetchDue(ModelImage image, DateTimeOffset now) =>
        SyncRetryPolicy.Default.IsThumbnailDue(image.ThumbnailAttemptedAt, image.ThumbnailFailure, now, force: false);

    /// <summary>
    /// Whether a decode attempt means the stored BLOB is corrupt.
    /// </summary>
    /// <remarks>
    /// Only <see cref="TileDecodeOutcome.NotAnImage"/> qualifies, and that is the whole point: the
    /// verdict deletes the bytes, so it must follow from "these bytes are not an image" and never
    /// from "we could not decode them right now". A decode can fail for reasons that are about this
    /// moment rather than this row — an <see cref="OutOfMemoryException"/> on a multi-MB legacy
    /// thumbnail under pressure is the obvious one — and the accepted trade in
    /// <see cref="MarkThumbnailCorruptAsync"/> only holds while undecodable really means
    /// undecodable. A hand-uploaded thumbnail sitting on a row whose civitai URL has since 404'd is
    /// gone for good once this says yes.
    /// <para>
    /// The other two clauses are about which bytes can be judged at all. A row with none is a
    /// missing thumbnail, which the fetch path answers; and the deferred sentinel is a one-byte
    /// marker that never was an image, so it decodes to <c>NotAnImage</c> by construction —
    /// mistaking that for corruption would null a row whose actual bytes are sitting in the
    /// database, unread.
    /// </para>
    /// </remarks>
    internal static bool ShouldMarkCorrupt(byte[]? data, TileDecodeOutcome outcome) =>
        outcome == TileDecodeOutcome.NotAnImage
        && data is { Length: > 0 }
        && !ReferenceEquals(data, ModelImage.ThumbnailNotLoadedSentinel);

    /// <summary>
    /// Records that an existing BLOB could not be decoded: the bytes are dropped and the row is
    /// stamped <see cref="ThumbnailFailureReason.Corrupt"/>, which is a soft failure, so the
    /// pipeline fetches a replacement rather than writing the image off.
    /// </summary>
    /// <remarks>
    /// Accepted edge: a user-uploaded thumbnail does not always live on a <c>user-thumbnail://</c>
    /// row — the upload reuses the version's primary image slot when one exists, so its bytes can
    /// sit on a row whose <c>Url</c> is a civitai address. If such a BLOB is undecodable this marks
    /// it and the next fetch replaces the user's picture with the CDN's. That is the right trade:
    /// an undecodable upload holds nothing recoverable, and the alternative is a permanently blank
    /// tile the user cannot explain.
    /// </remarks>
    private async Task MarkThumbnailCorruptAsync(ModelImage image, byte[] data)
    {
        var now = DateTimeOffset.UtcNow;

        _logger?.Warn(LogCategory.General, "ThumbnailDecode", FormattableString.Invariant(
            $"Stored thumbnail for '{DisplayName}' could not be decoded ({data.Length / 1024.0:F1} KB, ImageId={image.Id}) — dropped and marked for re-fetch"));

        image.ThumbnailData = null;
        ThumbnailWriter.ApplyFailure(image, ThumbnailFailureReason.Corrupt, now);

        if (image.Id > 0)
        {
            await PersistThumbnailAsync(image.Id, stored =>
            {
                stored.ThumbnailData = null;
                ThumbnailWriter.ApplyFailure(stored, ThumbnailFailureReason.Corrupt, now);
            }, "Corrupt-thumbnail verdict").ConfigureAwait(false);
        }
    }

    /// <summary>Decode width for tile thumbnails: 250px tile at up to 200% display scaling.</summary>
    private const int TileDecodeWidth = 500;

    /// <summary>What a decode attempt on stored thumbnail bytes actually established.</summary>
    internal enum TileDecodeOutcome
    {
        /// <summary>A bitmap came back.</summary>
        Decoded,

        /// <summary>
        /// SkiaSharp could build no image out of these bytes at all. A fact about the row, not
        /// about this attempt — repeating it tomorrow gets the same answer.
        /// </summary>
        NotAnImage,

        /// <summary>
        /// The attempt failed and says nothing about the bytes: memory pressure, a decoder that
        /// threw, a platform that is not there. The row is left exactly as it was.
        /// </summary>
        TransientFailure,
    }

    /// <summary>A decoded tile bitmap, or the reason there is none.</summary>
    internal readonly record struct TileDecodeResult(Bitmap? Bitmap, TileDecodeOutcome Outcome);

    /// <summary>
    /// Decodes thumbnail bytes into a displayable Bitmap, downscaled to the tile
    /// width. Safe to call from any thread — Avalonia Bitmaps are immutable and
    /// may be created off the UI thread. Falls back to a Skia transcode for
    /// formats Avalonia's decoder rejects.
    /// </summary>
    internal static Bitmap? CreateTileBitmap(byte[] data) => TryCreateTileBitmap(data).Bitmap;

    /// <summary>
    /// <see cref="CreateTileBitmap"/>, plus the distinction its callers need: whether a null
    /// bitmap means these bytes are not an image, or merely that this attempt failed.
    /// </summary>
    /// <remarks>
    /// <see cref="ShouldMarkCorrupt"/> deletes the stored BLOB on the strength of that answer, so
    /// "the decoder said no" is not good enough. Avalonia's decoder rejecting the bytes proves
    /// nothing on its own — it is the reason the Skia fallback exists — and an exception from
    /// either path (<see cref="OutOfMemoryException"/> on a multi-MB legacy thumbnail is the case
    /// that started this) is about the moment, not the row. The only authority for "not an image"
    /// is <see cref="SKBitmap.Decode(byte[])"/> refusing the raw bytes, which is exactly the
    /// authority <c>ThumbnailCodec.Encode</c> uses — including its quirk of throwing
    /// <see cref="ArgumentNullException"/>, rather than returning null, when it cannot build a
    /// codec for the bytes at all.
    /// </remarks>
    internal static TileDecodeResult TryCreateTileBitmap(byte[] data)
        => TryCreateTileBitmap(data, DecodeWithAvalonia, SKBitmap.Decode);

    /// <summary>
    /// <see cref="TryCreateTileBitmap(byte[])"/> with both decoders injected, so a test can make
    /// one of them throw. Neither failure mode is reachable on demand otherwise: memory pressure
    /// cannot be summoned, and no byte sequence makes Skia raise <c>OutOfMemoryException</c> to
    /// order.
    /// </summary>
    internal static TileDecodeResult TryCreateTileBitmap(
        byte[] data, Func<byte[], Bitmap?> avaloniaDecode, Func<byte[], SKBitmap?> skiaDecode)
    {
        try
        {
            var decoded = avaloniaDecode(data);
            if (decoded is not null) return new TileDecodeResult(decoded, TileDecodeOutcome.Decoded);
        }
        catch
        {
            // Deliberately swallowed and deliberately NOT a verdict: Avalonia's decoder is
            // narrower than Skia's, which is the whole reason the fallback below exists.
        }

        SKBitmap? skBitmap;
        try
        {
            skBitmap = skiaDecode(data);
        }
        catch (ArgumentNullException)
        {
            // How SkiaSharp reports "no codec for these bytes" — a handful of garbage with no
            // recognisable header. ThumbnailCodec.Encode treats it as identical to a null return
            // and so does this: it is a fact about the bytes.
            return new TileDecodeResult(null, TileDecodeOutcome.NotAnImage);
        }
        catch
        {
            return new TileDecodeResult(null, TileDecodeOutcome.TransientFailure);
        }

        if (skBitmap is null) return new TileDecodeResult(null, TileDecodeOutcome.NotAnImage);

        try
        {
            using (skBitmap)
            {
                using var skImage = SKImage.FromBitmap(skBitmap);
                if (skImage is null) return new TileDecodeResult(null, TileDecodeOutcome.TransientFailure);

                using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 90);
                if (encoded is null) return new TileDecodeResult(null, TileDecodeOutcome.TransientFailure);

                using var stream = new MemoryStream(encoded.ToArray());
                return new TileDecodeResult(new Bitmap(stream), TileDecodeOutcome.Decoded);
            }
        }
        catch
        {
            // Skia decoded the bytes, so they ARE an image; everything past that point is
            // transcoding and allocation, and a failure there is this attempt's, not the row's.
            return new TileDecodeResult(null, TileDecodeOutcome.TransientFailure);
        }
    }

    private static Bitmap? DecodeWithAvalonia(byte[] data)
    {
        using var stream = new MemoryStream(data);
        return Bitmap.DecodeToWidth(stream, TileDecodeWidth);
    }

    /// <summary>
    /// Lazy-loads a thumbnail BLOB from the database for a single image.
    /// Called when the bulk query deferred loading to save memory at scale.
    /// </summary>
    /// <summary>
    /// Delay between a tile scrolling into view and its thumbnail actually loading. Tiles
    /// that scroll past within this window (deactivated → <paramref name="ct"/> cancelled)
    /// never touch the DB or decoder, so flinging through the list doesn't fire a load per
    /// tile it flies over. Awaiting with ConfigureAwait(false) also pushes the DI-scope /
    /// DbContext / query setup off the UI thread — it used to run as this method's
    /// synchronous head on the caller (the UI thread), one hit per realized tile.
    /// </summary>
    private const int ThumbnailSettleDelayMs = 100;

    private async Task LazyLoadThumbnailFromDbAsync(ModelImage image, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ThumbnailSettleDelayMs, ct).ConfigureAwait(false);
            using var scope = _scopeFactory!.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<DataAccess.UnitOfWork.IUnitOfWork>();
            var (data, mimeType) = await unitOfWork.Models
                .GetImageThumbnailDataAsync(image.Id, ct)
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            if (data is { Length: > 0 })
            {
                // Gradual self-healing for legacy oversized BLOBs, through the same codec every
                // other producer uses — so a row written by the old naive fetch ends up
                // indistinguishable from one written today. An undecodable oversize BLOB falls
                // through unchanged and is handled below as what it is: corrupt; one that
                // re-encodes no smaller is left alone (see ShouldPersistSelfHeal).
                if (NeedsOversizeSelfHeal(data))
                {
                    var reencoded = ThumbnailCodec.Encode(data);
                    if (ShouldPersistSelfHeal(data, reencoded))
                    {
                        var now = DateTimeOffset.UtcNow;
                        _logger?.Info(LogCategory.General, "ThumbnailSelfHeal", FormattableString.Invariant(
                            $"Re-encoded legacy thumbnail for '{DisplayName}' ({data.Length / 1024.0:F1} KB → {reencoded.Data.Length / 1024.0:F1} KB, ImageId={image.Id})"));

                        data = reencoded.Data;
                        mimeType = reencoded.MimeType;
                        ThumbnailWriter.ApplySuccess(image, reencoded, now);
                        await PersistThumbnailAsync(
                            image.Id,
                            stored => ThumbnailWriter.ApplySuccess(stored, reencoded, now),
                            "Re-encoded legacy thumbnail").ConfigureAwait(false);
                    }
                }

                // Update the in-memory entity so subsequent accesses don't re-fetch
                image.ThumbnailData = data;
                image.ThumbnailMimeType = mimeType;

                // Decode on this (pool) thread; only the bound-property
                // assignment needs the UI thread.
                var decode = TryCreateTileBitmap(data);
                ct.ThrowIfCancellationRequested();

                if (ShouldMarkCorrupt(data, decode.Outcome))
                {
                    await MarkThumbnailCorruptAsync(image, data).ConfigureAwait(false);
                    return;
                }

                await _uiScheduler.InvokeAsync(() => ThumbnailImage = decode.Bitmap);
            }
            else
            {
                // Sentinel was wrong or data was deleted — clear it and fall through
                image.ThumbnailData = null;
                // Try fetching from the recorded URL as a fallback
                if (IsFetchableUrl(image.Url))
                {
                    // Ungated on purpose: this is the sentinel-was-wrong path — the row was
                    // reported as having bytes and does not. That contradiction is not a failed
                    // attempt whose window we should be waiting out, and a row that genuinely has
                    // no bytes never reaches here: it takes the gated branch above instead.
                    await DownloadThumbnailAsync(image, allowVideoDownload: false, ct);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            var logger = _logger;
            logger?.Debug(LogCategory.General, "ThumbnailLazyLoad",
                $"Failed to lazy-load thumbnail for image {image.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Searches for a local preview image next to the model's safetensors file, displays it,
    /// and persists the thumbnail BLOB to the database so it loads instantly next time.
    /// Discovery is <see cref="LocalPreviewFiles.FindSibling"/> and the encode is
    /// <see cref="ThumbnailCodec"/> — the same two the sidecar applier and the sync step use, so
    /// a preview found here is byte-for-byte the one found there.
    /// </summary>
    private async Task TryLoadLocalPreviewAsync(CancellationToken ct = default)
    {
        try
        {
            var localPath = SelectedVersion?.PrimaryFile?.LocalPath;
            if (string.IsNullOrEmpty(localPath) || !File.Exists(localPath)) return;

            var localImagePath = LocalPreviewFiles.FindSibling(localPath);
            if (localImagePath is null) return;

            var logger = _logger;
            logger?.Debug(LogCategory.General, "LocalPreview",
                $"Found local preview for '{DisplayName}': {Path.GetFileName(localImagePath)}");

            var imageBytes = await File.ReadAllBytesAsync(localImagePath, ct);
            if (imageBytes.Length == 0) return;

            ct.ThrowIfCancellationRequested();

            var payload = ThumbnailCodec.Encode(imageBytes);
            if (payload is null)
            {
                logger?.Debug(LogCategory.General, "LocalPreview",
                    $"Local preview for '{DisplayName}' could not be decoded: {Path.GetFileName(localImagePath)}");
                return;
            }

            ct.ThrowIfCancellationRequested();

            // Persist to DB: create or update the ModelImage entity with the thumbnail BLOB
            var version = SelectedVersion;
            if (version is not null && _scopeFactory is not null)
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<DiffusionNexusCoreDbContext>();

                    var dbVersion = await dbContext.ModelVersions
                        .Include(v => v.Images)
                        .FirstOrDefaultAsync(v => v.Id == version.Id, ct);

                    if (dbVersion is not null)
                    {
                        var primaryImage = dbVersion.Images.FirstOrDefault();
                        if (primaryImage is null)
                        {
                            // Create a new image entity for the local thumbnail
                            primaryImage = new ModelImage
                            {
                                ModelVersionId = dbVersion.Id,
                                Url = $"{LocalPreviewFiles.FileUrlPrefix}{localImagePath}",
                                SortOrder = 0,
                            };
                            dbVersion.Images.Add(primaryImage);
                        }

                        ThumbnailWriter.ApplySuccess(primaryImage, payload, DateTimeOffset.UtcNow);

                        await dbContext.SaveChangesAsync(ct);

                        logger?.Debug(LogCategory.General, "LocalPreview", FormattableString.Invariant(
                            $"Persisted local thumbnail for '{DisplayName}' ({payload.Data.Length / 1024.0:F1} KB)"));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw; // Re-throw so the outer catch handles it
                }
                catch (Exception ex)
                {
                    logger?.Debug(LogCategory.General, "LocalPreview",
                        $"Failed to persist local thumbnail for '{DisplayName}': {ex.Message}");
                    // Non-fatal — still display the in-memory thumbnail below
                }
            }

            ct.ThrowIfCancellationRequested();

            // Decoded here, on the pool thread; only the property assignment needs the UI thread.
            var bitmap = CreateTileBitmap(payload.Data);
            await _uiScheduler.InvokeAsync(() => ThumbnailImage = bitmap);
        }
        catch (OperationCanceledException)
        {
            // Version changed while loading — discard silently
        }
        catch
        {
            // Local preview is best-effort — don't propagate failures
        }
    }

    /// <summary>
    /// Returns true if the tile is showing "No Preview" but has a downloadable image URL.
    /// Checks the actual visual state (Bitmap) rather than entity data, so it catches
    /// corrupt BLOBs and decode failures too. Also returns true when the primary image is
    /// a video but a static sibling image exists that could be used instead.
    /// </summary>
    public bool IsThumbnailMissing =>
        ThumbnailImage is null
        && SelectedVersion?.Images is { Count: > 0 }
        && SelectedVersion.Images.Any(i => !string.IsNullOrEmpty(i.Url));

    /// <summary>
    /// Attempts to download the thumbnail for the selected version if it is missing.
    /// When the primary image is a video, prefers a static sibling image from the same
    /// version — Civitai CDN only serves resized images for static URLs, not for video URLs.
    /// </summary>
    /// <remarks>
    /// The one thumbnail path a person starts, and the only one allowed to download an original
    /// video and cut a frame out of it with FFmpeg. That costs megabytes, which is exactly why the
    /// scroll path never does it — but here somebody has asked for this model's thumbnail and is
    /// waiting for it, so the price is theirs to pay. For the same reason the retry gate that
    /// governs the scroll path (<see cref="IsScrollFetchDue"/>) is not consulted: a hard failure
    /// recorded yesterday is not an answer to a request made today.
    /// </remarks>
    public async Task TryDownloadMissingThumbnailAsync()
    {
        if (!IsThumbnailMissing) return;

        var primaryImage = SelectedVersion!.PrimaryImage!;

        // When the primary image is a video, look for a static sibling first — much more
        // reliable than FFmpeg extraction and avoids downloading the full video file.
        if (IsVideoPreview(primaryImage))
        {
            var staticSibling = PickStaticSibling(SelectedVersion.Images);

            if (staticSibling is not null)
            {
                var logger = _logger;
                logger?.Debug(LogCategory.Network, "ThumbnailDownload",
                    $"Primary image for '{DisplayName}' is video — using static sibling (ImageId={staticSibling.Id})");

                await DownloadThumbnailAsync(staticSibling, allowVideoDownload: true);
                return;
            }
        }

        // No sibling to borrow from — or the primary was never a video. Same guard as the sibling
        // pick applies here too: a deferred primary already has its bytes in the database, only
        // unloaded, so fetching would drive ApplySuccess straight over stored bytes that can be a
        // thumbnail the user uploaded by hand. Load what is already there instead of the network.
        if (ShouldLazyLoadInsteadOfFetch(primaryImage))
        {
            await LazyLoadThumbnailFromDbAsync(primaryImage, _thumbnailCts?.Token ?? CancellationToken.None);
            return;
        }

        await DownloadThumbnailAsync(primaryImage, allowVideoDownload: true);
    }

    /// <summary>
    /// True when <paramref name="image"/> is the deferred-sentinel row: its thumbnail bytes are
    /// already sitting in the database (the lightweight bulk query simply didn't load them), so a
    /// missing visual is answered by reading them, not by fetching from the network. A row with no
    /// bytes at all — the sentinel cleared, or never set — still needs the network fetch.
    /// </summary>
    internal static bool ShouldLazyLoadInsteadOfFetch(ModelImage image) => image.IsThumbnailDeferred;

    /// <summary>
    /// Picks the still image a video-primary version should borrow its thumbnail from: the first
    /// non-video row with a URL and no displayable thumbnail, SFW before NSFW, then by sort order.
    /// </summary>
    /// <remarks>
    /// "No displayable thumbnail" is two conditions, not one, and the old <c>ThumbnailData is
    /// null</c> got both wrong in opposite directions. An empty BLOB — bytes present, thumbnail
    /// absent — read as a thumbnail that exists, so a row this method should have found was
    /// skipped: that is what <see cref="ModelImage.HasThumbnail"/> fixes. But <c>HasThumbnail</c>
    /// alone is false for a <i>deferred</i> row too, and a deferred row's bytes are real and
    /// simply unloaded — fetching one would drive <c>ApplySuccess</c> straight over stored bytes,
    /// which on a version whose sort-0 row is not the primary image can be a thumbnail the user
    /// uploaded by hand. Hence both clauses.
    /// </remarks>
    internal static ModelImage? PickStaticSibling(IEnumerable<ModelImage> images) =>
        images
            .Where(i => !string.IsNullOrEmpty(i.Url)
                        && !IsVideoPreview(i)
                        && !i.HasThumbnail
                        && !i.IsThumbnailDeferred)
            .OrderBy(i => i.IsNsfw) // prefer SFW
            .ThenBy(i => i.SortOrder)
            .FirstOrDefault();

    /// <summary>
    /// Fetches this image's thumbnail through <see cref="IThumbnailProvider"/> and records the
    /// outcome — bytes, or the reason there are none — on the image row.
    /// </summary>
    /// <remarks>
    /// The tile no longer knows how a thumbnail is obtained: the provider owns the ladder (local
    /// file, video poster, still image), the codec owns what a thumbnail looks like, and the writer
    /// owns which columns a verdict touches. What is left here is the tile's own two jobs — show
    /// the result, and stop when the user has scrolled away.
    /// <para>
    /// On the scroll path a video costs one poster request and no more — callers there pass
    /// <paramref name="allowVideoDownload"/> false, because the alternative is streaming the whole
    /// clip to a temp file and running FFmpeg over it, for a tile the user is currently scrolling
    /// past. That was the old behaviour, and it is the reason flinging through a video-heavy
    /// library could pull hundreds of megabytes. Only
    /// <see cref="TryDownloadMissingThumbnailAsync"/> passes true.
    /// </para>
    /// </remarks>
    /// <param name="image">The row to fetch and stamp.</param>
    /// <param name="allowVideoDownload">
    /// Permission to fall back to downloading the original video and extracting a frame. True only
    /// where a person asked for this thumbnail and is waiting for it.
    /// </param>
    /// <param name="ct">The tile's own token — cancelled when it scrolls away or switches version.</param>
    private async Task DownloadThumbnailAsync(ModelImage image, bool allowVideoDownload, CancellationToken ct = default)
    {
        if (_scopeFactory is null) return;

        var logger = _logger;
        var displayName = DisplayName;

        logger?.Debug(LogCategory.Network, "ThumbnailDownload",
            $"Fetching thumbnail for '{displayName}'",
            $"URL: {image.Url}\nMediaType: {image.MediaType ?? "(null)"}");

        IsLoading = true;
        try
        {
            // Resolved from a scope for the same reason every other DB/service touch in this class
            // is: the tile holds the singleton factory, never a service instance.
            using var scope = _scopeFactory.CreateScope();
            var provider = scope.ServiceProvider.GetRequiredService<IThumbnailProvider>();

            var request = new ThumbnailRequest(
                image.Url,
                image.MediaType,
                SelectedVersion?.PrimaryFile?.LocalPath,
                AllowVideoDownload: allowVideoDownload);

            // ct is the tile's own token: Deactivate() cancels it, so scrolling away cancels the
            // request in flight rather than finishing a download for a tile nobody is looking at.
            var result = await provider.ProduceAsync(request, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();

            var now = DateTimeOffset.UtcNow;

            if (!result.Succeeded)
            {
                // Recorded, not merely reported — and now read: IsScrollFetchDue gates the scroll
                // path on this stamp, so a hard failure (a 404 poster) costs one attempt rather
                // than one per scroll past, and a soft one comes back after the retry window. The
                // provider already logged what went wrong.
                ThumbnailWriter.ApplyFailure(image, result.Failure!, now);
                if (image.Id > 0)
                {
                    await PersistThumbnailAsync(
                        image.Id,
                        stored => ThumbnailWriter.ApplyFailure(stored, result.Failure!, now),
                        $"Thumbnail failure ({result.Failure})").ConfigureAwait(false);
                }

                return; // the placeholder stays
            }

            var payload = result.Payload!;

            logger?.Info(LogCategory.Network, "ThumbnailDownload", FormattableString.Invariant(
                $"Thumbnail ready for '{displayName}' ({payload.Width}x{payload.Height}, {payload.Data.Length / 1024.0:F1} KB, ImageId={image.Id})"));

            // In-memory first so the tile is right even if the write below is not possible.
            ThumbnailWriter.ApplySuccess(image, payload, now);

            if (image.Id > 0)
            {
                await PersistThumbnailAsync(
                    image.Id,
                    stored => ThumbnailWriter.ApplySuccess(stored, payload, now),
                    "Thumbnail").ConfigureAwait(false);
            }
            else
            {
                logger?.Warn(LogCategory.Network, "ThumbnailDownload",
                    $"Cannot persist thumbnail for '{displayName}': image.Id is 0 (not yet saved to DB)");
            }

            ct.ThrowIfCancellationRequested();

            var bitmap = CreateTileBitmap(payload.Data);
            await _uiScheduler.InvokeAsync(() => ThumbnailImage = bitmap);
        }
        catch (OperationCanceledException)
        {
            // Scrolled away or the version changed — discard silently, and record nothing: nobody
            // waited for this thumbnail, so nothing about it failed.
        }
        catch (Exception ex)
        {
            // The provider answers expected faults with a reason rather than an exception, so
            // anything arriving here is the unexpected kind and keeps its stack trace.
            logger?.Error(LogCategory.Network, "ThumbnailDownload",
                $"Failed to create thumbnail for '{displayName}': {ex.Message}", ex);
        }
        finally
        {
            await _uiScheduler.InvokeAsync(() => IsLoading = false);
        }
    }

    /// <summary>
    /// Whether a preview image is a video — media type first, URL extension for legacy records that
    /// carry no media type.
    /// </summary>
    /// <remarks>
    /// The rule itself lives on <see cref="ModelImage.IsVideoLike"/> and this is the delegation, not
    /// a second copy. It used to be the only implementation of the extension fallback, which left
    /// the sync pipeline's rung 3 — gated on the entity predicate — blind to exactly the rows this
    /// method could see. Moving it down means the tile, the entity property and the SQL-side
    /// candidate ranking cannot disagree about what a video is.
    /// </remarks>
    private static bool IsVideoPreview(ModelImage image) =>
        ModelImage.IsVideoLike(image.MediaType, image.Url);

    /// <summary>
    /// Writes one thumbnail verdict to the stored row: a fresh scope, the tracked entity,
    /// <paramref name="stamp"/>, one save.
    /// </summary>
    /// <remarks>
    /// Both outcomes come through here, and both arrive as a <see cref="ThumbnailWriter"/> call the
    /// caller has already applied to its in-memory entity — so the row and the tile always say the
    /// same thing, and the six thumbnail columns are still only written in one place.
    /// Persistence is best-effort by design: a thumbnail the database refused is still a thumbnail
    /// on screen, and nothing the user is doing should fail because of it.
    /// </remarks>
    /// <param name="imageId">The row to stamp.</param>
    /// <param name="stamp">The writer call to apply to it.</param>
    /// <param name="outcome">What is being recorded, for the log line.</param>
    private async Task PersistThumbnailAsync(int imageId, Action<ModelImage> stamp, string outcome)
    {
        if (_scopeFactory is null) return;

        var logger = _logger;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataAccess.Data.DiffusionNexusCoreDbContext>();
            var dbImage = await dbContext.ModelImages.FindAsync(imageId);
            if (dbImage is not null)
            {
                stamp(dbImage);
                await dbContext.SaveChangesAsync();
                logger?.Debug(LogCategory.General, "ThumbnailDownload",
                    $"{outcome} persisted to DB for ImageId={imageId}");
            }
            else
            {
                logger?.Warn(LogCategory.General, "ThumbnailDownload",
                    $"Cannot persist thumbnail: ImageId={imageId} not found in database");
            }
        }
        catch (Exception ex)
        {
            logger?.Warn(LogCategory.General, "ThumbnailDownload",
                $"Failed to persist thumbnail for ImageId={imageId}: {ex.Message}");
        }
    }

    #endregion

    #region Factory Methods

    /// <summary>
    /// Creates a ModelTileViewModel from a Model entity. <paramref name="dependencies"/>
    /// carries the services the tile needs at runtime (#438); pass <c>null</c> for
    /// design-time / grouping-logic tests.
    /// </summary>
    public static ModelTileViewModel FromModel(Model model, ModelTileDependencies? dependencies = null)
    {
        var vm = new ModelTileViewModel(dependencies);
        vm._allGroupedModels = [model];
        vm._allDatabaseModelIds = [model.Id];
        vm.ModelEntity = model;
        return vm;
    }

    /// <summary>
    /// Creates a tile scoped to one LoRA-source location, possibly spanning multiple
    /// <see cref="Model"/> rows that share a Civitai page (legacy data where local-file
    /// discovery created separate entities for the same Civitai model). The version
    /// switcher exposes one button per <paramref name="versionFiles"/> entry; switching
    /// retargets <see cref="FileName"/> / <see cref="OpenFolder"/> / delete to that
    /// version's file in this location. Issue #380.
    /// </summary>
    public static ModelTileViewModel FromModelInLocation(
        IReadOnlyList<Model> models,
        string rootPath,
        IReadOnlyList<(ModelVersion Version, ModelFile File)> versionFiles,
        ModelTileDependencies? dependencies = null)
    {
        var map = new Dictionary<int, ModelFile>(versionFiles.Count);
        foreach (var (version, file) in versionFiles)
        {
            // Last-write-wins if the same version has two files in this root
            // (subdirectories of the same source). The Installed tab will still
            // show one button per version; the user can disambiguate via OpenFolder.
            map[version.Id] = file;
        }

        // Pick the richest row as the display primary — same heuristic as FromModelGroup.
        var primary = models
            .OrderByDescending(m => m.CivitaiId.HasValue)
            .ThenByDescending(m => m.Versions.Sum(v => v.Images.Count))
            .ThenByDescending(m => m.LastSyncedAt)
            .First();

        var vm = new ModelTileViewModel(dependencies)
        {
            _allGroupedModels = models.ToList(),
            _allDatabaseModelIds = models.Select(m => m.Id).ToHashSet(),
            _scopedFilesByVersionId = map,
            ScopedRootPath = rootPath,
            ModelEntity = primary,
        };
        return vm;
    }

    /// <summary>
    /// Creates a ModelTileViewModel from multiple Model entities that share the same Civitai page.
    /// Versions from all models are merged into a single tile.
    /// </summary>
    public static ModelTileViewModel FromModelGroup(IReadOnlyList<Model> models, ModelTileDependencies? dependencies = null)
    {
        // Use the model with the richest data as the primary display model
        var primary = models
            .OrderByDescending(m => m.CivitaiId.HasValue)
            .ThenByDescending(m => m.Versions.Sum(v => v.Images.Count))
            .ThenByDescending(m => m.LastSyncedAt)
            .First();

        var vm = new ModelTileViewModel(dependencies);
        vm._allGroupedModels = models.ToList();
        vm._allDatabaseModelIds = models.Select(m => m.Id).ToHashSet();
        vm.ModelEntity = primary;
        return vm;
    }

    /// <summary>
    /// Records additional database <see cref="Model.Id"/> values (e.g. re-discovery
    /// duplicates dropped during grouping) so destructive operations can clean
    /// them up alongside the displayed survivors.
    /// </summary>
    public void RegisterAdditionalDatabaseIds(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            _allDatabaseModelIds.Add(id);
    }

    /// <summary>
    /// Creates demo data for design-time and testing.
    /// </summary>
    public static ModelTileViewModel CreateDemo(
        string name,
        string creatorName,
        params string[] baseModels)
    {
        var creator = new Creator { Username = creatorName };
        var model = new Model
        {
            Name = name,
            Type = Domain.Enums.ModelType.LORA,
            Creator = creator,
            CreatorId = 1
        };

        var versionNum = 1;
        foreach (var baseModel in baseModels)
        {
            var version = new ModelVersion
            {
                Name = $"v{versionNum}.0 - {baseModel}",
                BaseModelRaw = baseModel,
                DownloadCount = Random.Shared.Next(100, 50000),
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-Random.Shared.Next(1, 90) * versionNum)
            };
            
            // Add a demo file with filename
            version.Files.Add(new ModelFile
            {
                FileName = $"{name.Replace(" ", "_").Replace("(", "").Replace(")", "")}_v{versionNum}.safetensors",
                IsPrimary = true
            });
            
            model.Versions.Add(version);
            versionNum++;
        }

        return FromModel(model);
    }

    #endregion
}
