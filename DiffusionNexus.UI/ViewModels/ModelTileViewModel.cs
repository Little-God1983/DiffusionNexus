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
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Views.Dialogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using System.Collections.ObjectModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for a single model tile in the LoRA Helper grid.
/// </summary>
public partial class ModelTileViewModel : ViewModelBase
{
    /// <summary>
    /// Raised after the model (and all its grouped versions) has been deleted from disk and DB.
    /// The parent view model should remove this tile from its collections.
    /// </summary>
    public event EventHandler? Deleted;

    /// <summary>
    /// Raised when the user wants to view the detail panel for this tile.
    /// The parent view model should open the detail view.
    /// </summary>
    public event EventHandler? DetailRequested;

    #region Observable Properties

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

        // Pick the richest model as primary (same logic as FromModelGroup)
        var primary = _allGroupedModels
            .OrderByDescending(m => m.CivitaiId.HasValue)
            .ThenByDescending(m => m.Versions.Sum(v => v.Images.Count))
            .ThenByDescending(m => m.LastSyncedAt)
            .First();

        ModelEntity = primary;
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
            var file = SelectedVersion?.Files?.FirstOrDefault(f => f.IsPrimary) 
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
            var file = SelectedVersion?.Files?.FirstOrDefault(f => f.IsPrimary)
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
    /// Version count display.
    /// </summary>
    public string VersionCountDisplay => HasMultipleVersions
        ? $"{Versions.Count} versions"
        : string.Empty;

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
    /// Copies text to the system clipboard via the Avalonia TopLevel.
    /// </summary>
    private static async Task CopyToClipboardAsync(string text)
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        var clipboard = topLevel?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    /// <summary>
    /// Open model on Civitai. Tries multiple ID sources to build the URL:
    /// CivitaiId → CivitaiModelPageId → SelectedVersion.CivitaiId (version-level URL).
    /// Logs a warning to the Unified Console when no Civitai link is available.
    /// </summary>
    [RelayCommand]
    private void OpenOnCivitai()
    {
        string? url = null;

        if (ModelEntity?.CivitaiId is { } modelCivitaiId)
        {
            url = $"https://civitai.com/models/{modelCivitaiId}";
        }
        else if (ModelEntity?.CivitaiModelPageId is { } pageId)
        {
            url = $"https://civitai.com/models/{pageId}";
        }
        else if (SelectedVersion?.CivitaiId is { } versionCivitaiId)
        {
            // Version-level ID — link to the version page directly
            url = $"https://civitai.com/api/v1/model-versions/{versionCivitaiId}";
        }

        if (url is null)
        {
            var logger = App.Services?.GetService<IUnifiedLogger>();
            logger?.Warn(LogCategory.General, "OpenOnCivitai",
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
        var file = SelectedVersion?.Files?.FirstOrDefault(f => f.LocalPath is not null);
        if (file?.LocalPath is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(file.LocalPath);
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
        var logger = App.Services?.GetService<IUnifiedLogger>();
        var dialogService = App.Services?.GetService<IDialogService>();

        if (dialogService is null)
        {
            logger?.Error(LogCategory.General, "Delete", "Dialog service unavailable — cannot show confirmation.");
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
        // Find the main window for ShowDialog
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var mainWindow = lifetime?.MainWindow;
        if (mainWindow is null)
        {
            logger?.Error(LogCategory.General, "Delete", "Cannot find main window for dialog.");
            return;
        }

        var allModels = _allGroupedModels.Count > 0
            ? _allGroupedModels
            : ModelEntity is not null ? new List<Model> { ModelEntity } : [];

        var dialog = new SelectLoraVersionsToDeleteDialog()
            .WithVersions(DisplayName, Versions, allModels);

        await dialog.ShowDialog(mainWindow);

        var result = dialog.Result;
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
                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
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
                using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
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
    /// Gets all model entity IDs across the group.
    /// </summary>
    private List<int> GetAllModelIds()
    {
        if (_allGroupedModels.Count > 0)
            return _allGroupedModels.Select(m => m.Id).ToList();

        return ModelEntity?.Id is { } id ? [id] : [];
    }

    /// <summary>
    /// Collects all local file paths across all grouped models and their versions.
    /// </summary>
    private List<string> CollectAllLocalFiles()
    {
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

        var allVersions = _allGroupedModels.Count > 0
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
        LoadThumbnailFromVersion();
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
        if (SelectedVersion?.PrimaryImage?.ThumbnailData is { } data && data.Length > 0)
        {
            try
            {
                // Use SkiaSharp to decode (handles WebP, JPEG, PNG, etc.)
                using var skBitmap = SKBitmap.Decode(data);
                if (skBitmap is not null)
                {
                    using var skImage = SKImage.FromBitmap(skBitmap);
                    using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 90);
                    using var stream = new MemoryStream(encoded.ToArray());
                    ThumbnailImage = new Bitmap(stream);
                }
                else
                {
                    ThumbnailImage = null;
                }
            }
            catch
            {
                ThumbnailImage = null;
            }
        }
        else if (SelectedVersion?.PrimaryImage is { } image && !string.IsNullOrEmpty(image.Url))
        {
            // No BLOB cached yet — download from Civitai URL in background
            ThumbnailImage = null;
            _ = DownloadThumbnailAsync(image);
        }
        else
        {
            ThumbnailImage = null;
        }
    }

    /// <summary>
    /// Returns true if the model was synced with Civitai but the selected version has no preview images.
    /// These models need their image records re-fetched from the Civitai API.
    /// </summary>
    public bool IsImageDataMissing =>
        ModelEntity?.CivitaiId is not null
        && SelectedVersion?.CivitaiId is not null
        && (SelectedVersion.Images is null || SelectedVersion.Images.Count == 0);

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
    public async Task TryDownloadMissingThumbnailAsync()
    {
        if (!IsThumbnailMissing) return;

        var primaryImage = SelectedVersion!.PrimaryImage!;

        // When the primary image is a video, look for a static sibling first — much more
        // reliable than FFmpeg extraction and avoids downloading the full video file.
        if (IsVideoPreview(primaryImage))
        {
            var staticSibling = SelectedVersion.Images
                .Where(i => !string.IsNullOrEmpty(i.Url) && !IsVideoPreview(i) && i.ThumbnailData is null)
                .OrderBy(i => i.IsNsfw) // prefer SFW
                .ThenBy(i => i.SortOrder)
                .FirstOrDefault();

            if (staticSibling is not null)
            {
                var logger = App.Services?.GetService<IUnifiedLogger>();
                logger?.Debug(LogCategory.Network, "ThumbnailDownload",
                    $"Primary image for '{DisplayName}' is video — using static sibling (ImageId={staticSibling.Id})");

                await DownloadThumbnailAsync(staticSibling);
                return;
            }
        }

        await DownloadThumbnailAsync(primaryImage);
    }

    /// <summary>
    /// Downloads a thumbnail from a Civitai image URL and caches it as a BLOB.
    /// For video previews, downloads the video to a temp file and extracts the mid-frame.
    /// </summary>
    private async Task DownloadThumbnailAsync(ModelImage image)
    {
        var logger = App.Services?.GetService<IUnifiedLogger>();
        var isVideo = IsVideoPreview(image);
        var previewType = isVideo ? "video" : "image";
        var displayName = DisplayName;

        logger?.Debug(LogCategory.Network, "ThumbnailDownload",
            $"Downloading {previewType} thumbnail for '{displayName}'",
            $"URL: {image.Url}\nMediaType: {image.MediaType ?? "(null)"}");

        IsLoading = true;
        try
        {
            byte[] thumbnailBytes;
            string mimeType;

            if (isVideo)
            {
                (thumbnailBytes, mimeType) = await DownloadVideoThumbnailAsync(image.Url, logger).ConfigureAwait(false);
            }
            else
            {
                (thumbnailBytes, mimeType) = await DownloadImageThumbnailAsync(image.Url).ConfigureAwait(false);
            }

            if (thumbnailBytes.Length == 0)
            {
                logger?.Warn(LogCategory.Network, "ThumbnailDownload",
                    $"No usable {previewType} thumbnail for '{displayName}' — download returned no data");
                return;
            }

            // Transcode to JPEG if Avalonia can't decode the raw bytes (e.g., WebP from Civitai CDN).
            // Also rejects video data that was mistakenly downloaded instead of an image.
            (thumbnailBytes, mimeType) = EnsureDecodableBytes(thumbnailBytes, mimeType, logger, displayName);

            if (thumbnailBytes.Length == 0)
            {
                // EnsureDecodableBytes already logged the specific reason (video data, corrupt, etc.)
                return;
            }

            logger?.Info(LogCategory.Network, "ThumbnailDownload",
                $"Thumbnail ready for '{displayName}' ({previewType}, {thumbnailBytes.Length / 1024.0:F1} KB, ImageId={image.Id})");

            // Store in-memory for immediate display
            image.ThumbnailData = thumbnailBytes;
            image.ThumbnailMimeType = mimeType;

            // Persist BLOB to the database so next startup is instant
            if (image.Id > 0)
            {
                await PersistThumbnailAsync(image.Id, thumbnailBytes, mimeType);
            }
            else
            {
                logger?.Warn(LogCategory.Network, "ThumbnailDownload",
                    $"Cannot persist thumbnail for '{displayName}': image.Id is 0 (not yet saved to DB)");
            }

            // Display the downloaded thumbnail
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(thumbnailBytes);
                    ThumbnailImage = new Bitmap(stream);
                }
                catch (Exception ex)
                {
                    logger?.Warn(LogCategory.General, "ThumbnailDownload",
                        $"Failed to decode thumbnail Bitmap for '{displayName}': {ex.Message}");
                    ThumbnailImage = null;
                }
            });
        }
        catch (Exception ex)
        {
            logger?.Error(LogCategory.Network, "ThumbnailDownload",
                $"Failed to create {previewType} thumbnail for '{displayName}': {ex.Message}", ex);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => IsLoading = false);
        }
    }

    /// <summary>
    /// Determines whether a preview image is a video based on MediaType or URL extension.
    /// Falls back to URL extension for legacy records that don't have MediaType set.
    /// </summary>
    private static bool IsVideoPreview(ModelImage image)
    {
        if (image.IsVideo)
            return true;

        // Fallback: detect video by URL extension for legacy records without MediaType
        if (image.MediaType is null && !string.IsNullOrEmpty(image.Url))
        {
            var extension = Path.GetExtension(new Uri(image.Url).AbsolutePath);
            return extension is ".mp4" or ".webm" or ".mov" or ".avi" or ".mkv";
        }

        return false;
    }

    /// <summary>
    /// Downloads an image thumbnail from a Civitai URL.
    /// </summary>
    private static async Task<(byte[] Data, string MimeType)> DownloadImageThumbnailAsync(string url)
    {
        // Civitai supports width parameter for resized images
        var thumbnailUrl = url.Contains('?')
            ? $"{url}&width=300"
            : $"{url}/width=300";

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var bytes = await httpClient.GetByteArrayAsync(thumbnailUrl).ConfigureAwait(false);
        return (bytes, "image/jpeg");
    }

    /// <summary>
    /// Gets a thumbnail for a video preview by extracting a frame via FFmpeg.
    /// The Civitai CDN does not serve static poster images for video URLs;
    /// the <c>/width=300</c> trick only works for image URLs.
    /// </summary>
    private static async Task<(byte[] Data, string MimeType)> DownloadVideoThumbnailAsync(
        string videoUrl, IUnifiedLogger? logger)
    {
        // FFmpeg frame extraction — the only reliable way to get a poster from a video URL
        var videoThumbnailService = App.Services?.GetService<IVideoThumbnailService>();
        if (videoThumbnailService is null)
        {
            logger?.Warn(LogCategory.General, "ThumbnailDownload",
                "Video-only model: FFmpeg is required to generate thumbnails from video previews. " +
                "Install FFmpeg and ensure it is on PATH, or wait until a static preview image is available on Civitai.");
            return ([], string.Empty);
        }

        // Ensure FFmpeg is available BEFORE downloading the video — avoids wasting
        // bandwidth on a multi-MB download when FFmpeg can't be found/downloaded.
        try
        {
            await videoThumbnailService.EnsureFFmpegAvailableAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger?.Warn(LogCategory.General, "ThumbnailDownload",
                $"FFmpeg is not available — video thumbnails cannot be generated: {ex.Message}");
            return ([], string.Empty);
        }

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"dn_preview_{Guid.NewGuid():N}.mp4");
        string? generatedThumbnailPath = null;
        try
        {
            logger?.Debug(LogCategory.Network, "ThumbnailDownload",
                $"Downloading video to temp: {tempVideoPath}",
                $"URL: {videoUrl}");

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var videoBytes = await httpClient.GetByteArrayAsync(videoUrl).ConfigureAwait(false);
            await File.WriteAllBytesAsync(tempVideoPath, videoBytes).ConfigureAwait(false);

            logger?.Debug(LogCategory.Network, "ThumbnailDownload",
                $"Video downloaded ({videoBytes.Length / 1024.0:F0} KB), extracting mid-frame...");

            var result = await videoThumbnailService.GenerateThumbnailAsync(
                tempVideoPath,
                new VideoThumbnailOptions { MaxWidth = 300, OutputFormat = ThumbnailFormat.WebP })
                .ConfigureAwait(false);

            if (!result.Success || string.IsNullOrEmpty(result.ThumbnailPath))
            {
                logger?.Warn(LogCategory.General, "ThumbnailDownload",
                    $"FFmpeg frame extraction failed: {result.ErrorMessage ?? "unknown error"}");
                return ([], string.Empty);
            }

            generatedThumbnailPath = result.ThumbnailPath;
            var thumbnailBytes = await File.ReadAllBytesAsync(result.ThumbnailPath).ConfigureAwait(false);

            logger?.Debug(LogCategory.General, "ThumbnailDownload",
                $"Video frame extracted ({thumbnailBytes.Length / 1024.0:F1} KB, {result.Width}x{result.Height})",
                $"Captured at {result.CapturePosition} of {result.VideoDuration}");

            return (thumbnailBytes, "image/webp");
        }
        finally
        {
            // TODO: Linux Implementation for temp file cleanup
            TryDeleteFile(tempVideoPath);
            if (generatedThumbnailPath is not null)
                TryDeleteFile(generatedThumbnailPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* Best-effort cleanup */ }
    }

    /// <summary>
    /// Returns <c>true</c> when the byte payload looks like a video container (MP4/WebM/AVI/MKV)
    /// rather than a decodable image. Used to reject CDN responses that return the full
    /// video stream instead of a poster frame.
    /// </summary>
    private static bool IsVideoData(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return false;

        // MP4 / MOV — "ftyp" box at offset 4
        if (data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            return true;

        // WebM / MKV — EBML header (0x1A 0x45 0xDF 0xA3)
        if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            return true;

        // AVI — "RIFF....AVI "
        if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'A' && data[9] == (byte)'V' && data[10] == (byte)'I')
            return true;

        return false;
    }

    /// <summary>
    /// Ensures the thumbnail bytes are in a format Avalonia's <see cref="Bitmap"/> can decode.
    /// Civitai's CDN often returns WebP for thumbnails, which Avalonia cannot decode
    /// natively. This method detects the issue and transcodes to JPEG via SkiaSharp.
    /// Returns empty data when the payload is video data (not an image at all).
    /// </summary>
    private static (byte[] Data, string MimeType) EnsureDecodableBytes(
        byte[] data, string mimeType, IUnifiedLogger? logger, string displayName)
    {
        // Reject video data early — CDN may return the full MP4 instead of a poster frame
        if (IsVideoData(data))
        {
            logger?.Warn(LogCategory.General, "ThumbnailDownload",
                $"Downloaded data for '{displayName}' is video ({data.Length / 1024.0:F1} KB), not an image — cannot use as thumbnail");
            return ([], string.Empty);
        }

        // Quick check: try Avalonia decode first — most JPEG/PNG will work
        try
        {
            using var testStream = new MemoryStream(data);
            _ = new Bitmap(testStream);
            return (data, mimeType); // Already decodable
        }
        catch
        {
            // Fall through to SkiaSharp transcode
        }

        // Transcode via SkiaSharp (handles WebP, AVIF, etc.)
        try
        {
            using var skBitmap = SKBitmap.Decode(data);
            if (skBitmap is null)
            {
                logger?.Warn(LogCategory.General, "ThumbnailDownload",
                    $"Neither Avalonia nor SkiaSharp can decode thumbnail for '{displayName}' " +
                    $"({data.Length / 1024.0:F1} KB) — data may be corrupted or an unsupported format");
                return ([], string.Empty);
            }

            using var skImage = SKImage.FromBitmap(skBitmap);
            using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 90);
            var jpegBytes = encoded.ToArray();

            logger?.Debug(LogCategory.General, "ThumbnailDownload",
                $"Transcoded thumbnail for '{displayName}' to JPEG ({data.Length / 1024.0:F1} KB → {jpegBytes.Length / 1024.0:F1} KB)");

            return (jpegBytes, "image/jpeg");
        }
        catch (Exception ex)
        {
            logger?.Warn(LogCategory.General, "ThumbnailDownload",
                $"SkiaSharp transcode failed for '{displayName}' ({data.Length / 1024.0:F1} KB): {ex.Message}");
            return ([], string.Empty);
        }
    }

    /// <summary>
    /// Persists downloaded thumbnail bytes to the database for a given ModelImage.
    /// </summary>
    private static async Task PersistThumbnailAsync(int imageId, byte[] thumbnailData, string mimeType)
    {
        var logger = App.Services?.GetService<IUnifiedLogger>();
        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<DataAccess.Data.DiffusionNexusCoreDbContext>();
            var dbImage = await dbContext.ModelImages.FindAsync(imageId);
            if (dbImage is not null)
            {
                dbImage.ThumbnailData = thumbnailData;
                dbImage.ThumbnailMimeType = mimeType;
                await dbContext.SaveChangesAsync();
                logger?.Debug(LogCategory.General, "ThumbnailDownload",
                    $"Thumbnail persisted to DB for ImageId={imageId} ({thumbnailData.Length / 1024.0:F1} KB)");
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
    /// Creates a ModelTileViewModel from a Model entity.
    /// </summary>
    public static ModelTileViewModel FromModel(Model model)
    {
        var vm = new ModelTileViewModel();
        vm._allGroupedModels = [model];
        vm.ModelEntity = model;
        return vm;
    }

    /// <summary>
    /// Creates a ModelTileViewModel from multiple Model entities that share the same Civitai page.
    /// Versions from all models are merged into a single tile.
    /// </summary>
    public static ModelTileViewModel FromModelGroup(IReadOnlyList<Model> models)
    {
        // Use the model with the richest data as the primary display model
        var primary = models
            .OrderByDescending(m => m.CivitaiId.HasValue)
            .ThenByDescending(m => m.Versions.Sum(v => v.Images.Count))
            .ThenByDescending(m => m.LastSyncedAt)
            .First();

        var vm = new ModelTileViewModel();
        vm._allGroupedModels = models.ToList();
        vm.ModelEntity = primary;
        return vm;
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
