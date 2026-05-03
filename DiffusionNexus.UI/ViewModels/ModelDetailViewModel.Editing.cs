using System.Collections.ObjectModel;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Editing surface for <see cref="ModelDetailViewModel"/>: thumbnail upload,
/// description editing, tag add/remove and trigger word editing. Edits are
/// persisted via a fresh <see cref="IUnitOfWork"/> scope and mark the entity
/// as <c>IsUserEdited = true</c> so the Civitai sync pipeline does not
/// overwrite them.
/// </summary>
public partial class ModelDetailViewModel
{
    #region Editable observable state

    /// <summary>Tags rendered as removable chips. Rebuilt from the loaded model.</summary>
    public ObservableCollection<EditableTagItem> EditableTagItems { get; } = [];

    /// <summary>New tag text bound to the inline add-tag TextBox.</summary>
    [ObservableProperty]
    private string _newTagText = string.Empty;

    [ObservableProperty]
    private bool _isEditingDescription;

    [ObservableProperty]
    private string _descriptionEditBuffer = string.Empty;

    [ObservableProperty]
    private bool _isEditingTriggerWords;

    [ObservableProperty]
    private string _triggerWordsEditBuffer = string.Empty;

    [ObservableProperty]
    private bool _isEditingName;

    [ObservableProperty]
    private string _nameEditBuffer = string.Empty;

    /// <summary>
    /// All selectable categories for the inline ComboBox. Includes a leading
    /// "(infer from tags)" entry mapped to <see cref="Domain.Enums.CivitaiCategory.Unknown"/>.
    /// </summary>
    public IReadOnlyList<CivitaiCategory> AvailableCategories { get; } =
        Enum.GetValues<CivitaiCategory>();

    /// <summary>Currently selected category in the ComboBox (two-way bound).</summary>
    [ObservableProperty]
    private CivitaiCategory _selectedCategory = CivitaiCategory.Unknown;

    private bool _suppressCategorySave;

    partial void OnSelectedCategoryChanged(CivitaiCategory value)
    {
        if (_suppressCategorySave) return;
        _ = SaveCategoryAsync(value);
    }

    #endregion

    #region Name editing

    [RelayCommand]
    private void BeginEditName()
    {
        NameEditBuffer = ModelName ?? string.Empty;
        IsEditingName = true;
    }

    [RelayCommand]
    private void CancelEditName()
    {
        IsEditingName = false;
        NameEditBuffer = string.Empty;
    }

    [RelayCommand]
    private async Task SaveNameAsync()
    {
        var model = SourceTile?.ModelEntity;
        if (model is null) return;

        var newName = (NameEditBuffer ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusMessage = "Name cannot be empty.";
            return;
        }

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            if (dbModel is null) return;

            dbModel.Name = newName;
            dbModel.IsUserEdited = true;

            await unitOfWork.SaveChangesAsync();
            ModelName = newName;
            IsEditingName = false;

            await PostSaveRefreshAsync(unitOfWork, model.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to save name: {ex.Message}", ex);
            StatusMessage = $"Failed to save name: {ex.Message}";
        }
    }

    #endregion

    #region Category editing

    /// <summary>
    /// Initializes <see cref="SelectedCategory"/> from the loaded model without
    /// triggering a save (uses <see cref="_suppressCategorySave"/>).
    /// </summary>
    public void LoadCategorySelection()
    {
        var model = SourceTile?.ModelEntity;
        // When the user hasn't set an explicit override, fall back to the
        // category inferred from the model's tags so the ComboBox is never
        // empty for an existing LoRA whose category derives from a tag.
        var current = model?.UserCategory
                      ?? InferCategoryEnumFromTags(model);
        _suppressCategorySave = true;
        try
        {
            SelectedCategory = current;
        }
        finally
        {
            _suppressCategorySave = false;
        }
        RefreshCategoryTagHighlight();
    }

    /// <summary>
    /// Returns the first <see cref="CivitaiCategory"/> that matches one of the
    /// model's tag names, or <see cref="CivitaiCategory.Unknown"/> when none match.
    /// Mirrors <c>InferCategoryFromTags</c> but returns the enum value.
    /// </summary>
    private static CivitaiCategory InferCategoryEnumFromTags(Model? model)
    {
        if (model?.Tags is not { Count: > 0 } tags) return CivitaiCategory.Unknown;
        foreach (var mt in tags)
        {
            var tagName = mt.Tag?.Name;
            if (string.IsNullOrWhiteSpace(tagName)) continue;
            var normalized = tagName.Replace(" ", "_").Trim();
            if (Enum.TryParse<CivitaiCategory>(normalized, ignoreCase: true, out var cat)
                && cat != CivitaiCategory.Unknown)
            {
                return cat;
            }
        }
        return CivitaiCategory.Unknown;
    }

    /// <summary>
    /// Marks the tag chip whose name maps to the active category so the UI
    /// can highlight it and the user cannot remove it (removing it would
    /// silently change the category).
    /// </summary>
    private void RefreshCategoryTagHighlight()
    {
        var model = SourceTile?.ModelEntity;
        var active = model?.UserCategory ?? InferCategoryEnumFromTags(model);
        var activeName = active == CivitaiCategory.Unknown ? null : active.ToString();
        foreach (var item in EditableTagItems)
        {
            var normalized = item.Name.Replace(" ", "_").Trim();
            item.IsCategoryTag = activeName is not null
                && string.Equals(normalized, activeName, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task SaveCategoryAsync(CivitaiCategory value)
    {
        var model = SourceTile?.ModelEntity;
        if (model is null) return;

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            if (dbModel is null) return;

            // Determine the previously-active category (override or inferred) so we
            // know which tag chip currently "owns" the category badge — that tag is
            // dropped before the new one is added.
            var previousActive = dbModel.UserCategory ?? InferCategoryEnumFromTags(dbModel);

            dbModel.UserCategory = value == CivitaiCategory.Unknown ? null : value;
            dbModel.IsUserEdited = true;

            // Sync the tag list so the previously-protected tag goes away and the
            // new category tag is added (and becomes the new protected chip).
            SyncCategoryTag(dbModel, previousActive, value, await unitOfWork.Models.GetAllTagsLookupAsync());

            await unitOfWork.SaveChangesAsync();

            // Update displayed category to reflect the override (or fall back to inference).
            var label = value == CivitaiCategory.Unknown
                ? null
                : (value == CivitaiCategory.BaseModel ? "Base Model" : value.ToString());
            CategoryDisplay = label ?? string.Empty;
            HasCategory = !string.IsNullOrEmpty(CategoryDisplay);

            await PostSaveRefreshAsync(unitOfWork, model.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to save category: {ex.Message}", ex);
            StatusMessage = $"Failed to save category: {ex.Message}";
        }
    }

    /// <summary>
    /// Removes the tag corresponding to <paramref name="oldCategory"/> (when it
    /// exists in the model's tag list) and adds a tag for
    /// <paramref name="newCategory"/>. The tag name uses the enum's exact
    /// member name so <see cref="InferCategoryEnumFromTags"/> can match it.
    /// </summary>
    private static void SyncCategoryTag(
        Model dbModel,
        CivitaiCategory oldCategory,
        CivitaiCategory newCategory,
        IDictionary<string, Tag> tagLookup)
    {
        if (oldCategory == newCategory) return;

        if (oldCategory != CivitaiCategory.Unknown)
        {
            var oldName = oldCategory.ToString();
            var oldNormalized = oldName.ToLowerInvariant();
            var existing = dbModel.Tags.FirstOrDefault(mt => mt.Tag != null
                && string.Equals(mt.Tag.NormalizedName, oldNormalized, StringComparison.Ordinal));
            if (existing is not null)
            {
                dbModel.Tags.Remove(existing);
            }
        }

        if (newCategory != CivitaiCategory.Unknown)
        {
            var newName = newCategory.ToString();
            var newNormalized = newName.ToLowerInvariant();
            var alreadyHas = dbModel.Tags.Any(mt => mt.Tag != null
                && string.Equals(mt.Tag.NormalizedName, newNormalized, StringComparison.Ordinal));
            if (!alreadyHas)
            {
                if (!tagLookup.TryGetValue(newNormalized, out var tag))
                {
                    tag = new Tag { Name = newName, NormalizedName = newNormalized };
                    tagLookup[newNormalized] = tag;
                }
                dbModel.Tags.Add(new ModelTag { Tag = tag });
            }
        }
    }

    #endregion

    #region Base model editing

    /// <summary>
    /// Selectable base model labels (e.g. "SDXL 1.0", "Pony", "Flux.1 D"). The
    /// list is sourced from <see cref="ICivitaiBaseModelCatalog"/> at load time
    /// and matches Civitai's own base-model filter dropdown. The currently
    /// persisted value is always present, even when the catalog hasn't heard of
    /// it yet, so the ComboBox can round-trip arbitrary legacy strings.
    /// </summary>
    public ObservableCollection<string> AvailableBaseModels { get; } = [];

    /// <summary>Currently selected base model in the ComboBox (two-way bound).</summary>
    [ObservableProperty]
    private string? _selectedBaseModel;

    private bool _suppressBaseModelSave;

    partial void OnSelectedBaseModelChanged(string? value)
    {
        if (_suppressBaseModelSave) return;
        _ = SaveSelectedBaseModelAsync(value);
    }

    /// <summary>
    /// Refreshes <see cref="AvailableBaseModels"/> from the catalog and seeds
    /// <see cref="SelectedBaseModel"/> from the current version's
    /// <c>BaseModelRaw</c>. Safe to call multiple times.
    /// </summary>
    public async Task LoadBaseModelCatalogAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> labels;
        try
        {
            labels = _baseModelCatalog is not null
                ? await _baseModelCatalog.GetBaseModelsAsync(cancellationToken: cancellationToken)
                : Array.Empty<string>();
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Network, "BaseModelCatalog",
                $"Failed to load Civitai base model catalog: {ex.Message}");
            labels = Array.Empty<string>();
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var current = ResolveCurrentBaseModelRaw();

            _suppressBaseModelSave = true;
            try
            {
                AvailableBaseModels.Clear();
                foreach (var label in labels)
                {
                    AvailableBaseModels.Add(label);
                }

                // Make sure the currently-stored value is selectable even if it
                // isn't in the Civitai catalog (e.g. legacy / custom labels).
                if (!string.IsNullOrWhiteSpace(current)
                    && !AvailableBaseModels.Any(b => string.Equals(b, current, StringComparison.OrdinalIgnoreCase)))
                {
                    AvailableBaseModels.Insert(0, current);
                }

                SelectedBaseModel = string.IsNullOrWhiteSpace(current) ? null : current;
            }
            finally
            {
                _suppressBaseModelSave = false;
            }
        });
    }

    /// <summary>
    /// Re-syncs <see cref="SelectedBaseModel"/> to the value backing the
    /// currently selected version tab (or the local primary version) without
    /// triggering a save. Called whenever the version tab changes.
    /// </summary>
    public void SyncSelectedBaseModelFromVersion()
    {
        var current = ResolveCurrentBaseModelRaw();
        if (!string.IsNullOrWhiteSpace(current)
            && !AvailableBaseModels.Any(b => string.Equals(b, current, StringComparison.OrdinalIgnoreCase)))
        {
            AvailableBaseModels.Insert(0, current);
        }

        _suppressBaseModelSave = true;
        try
        {
            SelectedBaseModel = string.IsNullOrWhiteSpace(current) ? null : current;
        }
        finally
        {
            _suppressBaseModelSave = false;
        }
    }

    /// <summary>
    /// Returns the BaseModelRaw of the currently displayed version: the local
    /// version backing the selected tab when present, otherwise the Civitai
    /// version label, otherwise the source tile's selected version.
    /// </summary>
    private string? ResolveCurrentBaseModelRaw()
    {
        var localFromTab = SelectedVersionTab?.LocalVersion?.BaseModelRaw;
        if (!string.IsNullOrWhiteSpace(localFromTab)) return localFromTab;

        var civitai = SelectedVersionTab?.CivitaiVersion?.BaseModel;
        if (!string.IsNullOrWhiteSpace(civitai)) return civitai;

        return SourceTile?.SelectedVersion?.BaseModelRaw;
    }

    private async Task SaveSelectedBaseModelAsync(string? value)
    {
        var localVersion = SelectedVersionTab?.LocalVersion ?? SourceTile?.SelectedVersion;
        if (localVersion is null)
        {
            // Nothing local to persist (Civitai-only tab) — silently ignore;
            // BaseModelRaw is fixed by the API for those tabs.
            return;
        }

        var newValue = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (string.Equals(newValue, localVersion.BaseModelRaw, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(localVersion.ModelId);
            if (dbModel is null) return;

            var dbVersion = dbModel.Versions.FirstOrDefault(v => v.Id == localVersion.Id);
            if (dbVersion is null) return;

            dbVersion.BaseModelRaw = newValue;
            dbVersion.BaseModel = BaseModelTypeExtensions.ParseCivitai(newValue);
            dbVersion.IsUserEdited = true;

            await unitOfWork.SaveChangesAsync();

            BaseModelDisplay = newValue ?? "Unknown";

            await PostSaveRefreshAsync(unitOfWork, dbModel.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to save base model: {ex.Message}", ex);
            StatusMessage = $"Failed to save base model: {ex.Message}";
        }
    }

    #endregion

    #region Tag editing

    /// <summary>
    /// Rebuilds <see cref="EditableTagItems"/> from the source tile so the
    /// inline tag chips match the latest persisted state.
    /// </summary>
    public Task LoadEditableTagsAsync()
    {
        EditableTagItems.Clear();
        var model = SourceTile?.ModelEntity;
        if (model?.Tags is { Count: > 0 } tags)
        {
            foreach (var name in tags
                .Where(t => t.Tag is not null)
                .Select(t => t.Tag!.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                EditableTagItems.Add(new EditableTagItem(name, RemoveTagAsync));
            }
        }
        HasTags = EditableTagItems.Count > 0;
        RefreshCategoryTagHighlight();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task AddTagAsync()
    {
        var raw = NewTagText?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        var model = SourceTile?.ModelEntity;
        if (model is null) return;

        var normalized = raw.ToLowerInvariant();
        if (EditableTagItems.Any(t => string.Equals(t.Name, raw, StringComparison.OrdinalIgnoreCase)))
        {
            NewTagText = string.Empty;
            return;
        }

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            if (dbModel is null) return;

            var lookup = await unitOfWork.Models.GetAllTagsLookupAsync();
            if (!lookup.TryGetValue(normalized, out var tag))
            {
                tag = new Tag { Name = raw, NormalizedName = normalized };
            }

            if (!dbModel.Tags.Any(mt => mt.Tag != null
                && string.Equals(mt.Tag.NormalizedName, normalized, StringComparison.Ordinal)))
            {
                dbModel.Tags.Add(new ModelTag { Tag = tag });
            }
            dbModel.IsUserEdited = true;

            await unitOfWork.SaveChangesAsync();
            await PostSaveRefreshAsync(unitOfWork, model.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to add tag '{raw}': {ex.Message}", ex);
            StatusMessage = $"Failed to add tag: {ex.Message}";
        }
        finally
        {
            NewTagText = string.Empty;
        }
    }

    private async Task RemoveTagAsync(EditableTagItem item)
    {
        var model = SourceTile?.ModelEntity;
        if (model is null) return;

        if (item.IsCategoryTag)
        {
            StatusMessage = $"Cannot remove '{item.Name}' \u2014 it defines this LoRA's category. Change the Category first.";
            return;
        }

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            if (dbModel is null) return;

            var normalized = item.Name.ToLowerInvariant();
            var existing = dbModel.Tags.FirstOrDefault(mt => mt.Tag != null
                && string.Equals(mt.Tag.NormalizedName, normalized, StringComparison.Ordinal));
            if (existing is not null)
            {
                dbModel.Tags.Remove(existing);
            }
            dbModel.IsUserEdited = true;

            await unitOfWork.SaveChangesAsync();
            await PostSaveRefreshAsync(unitOfWork, model.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to remove tag '{item.Name}': {ex.Message}", ex);
            StatusMessage = $"Failed to remove tag: {ex.Message}";
        }
    }

    #endregion

    #region Description editing

    [RelayCommand]
    private void BeginEditDescription()
    {
        DescriptionEditBuffer = DescriptionText ?? string.Empty;
        IsEditingDescription = true;
    }

    [RelayCommand]
    private void CancelEditDescription()
    {
        IsEditingDescription = false;
        DescriptionEditBuffer = string.Empty;
    }

    [RelayCommand]
    private async Task SaveDescriptionAsync()
    {
        var model = SourceTile?.ModelEntity;
        if (model is null) return;

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            if (dbModel is null) return;

            dbModel.Description = DescriptionEditBuffer ?? string.Empty;
            dbModel.IsUserEdited = true;

            await unitOfWork.SaveChangesAsync();
            DescriptionText = dbModel.Description;
            IsEditingDescription = false;

            await PostSaveRefreshAsync(unitOfWork, model.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to save description: {ex.Message}", ex);
            StatusMessage = $"Failed to save description: {ex.Message}";
        }
    }

    #endregion

    #region Trigger words editing (per selected version)

    [RelayCommand]
    private void BeginEditTriggerWords()
    {
        TriggerWordsEditBuffer = TriggerWordsDisplay ?? string.Empty;
        IsEditingTriggerWords = true;
    }

    [RelayCommand]
    private void CancelEditTriggerWords()
    {
        IsEditingTriggerWords = false;
        TriggerWordsEditBuffer = string.Empty;
    }

    [RelayCommand]
    private async Task SaveTriggerWordsAsync()
    {
        var localVersion = SelectedVersionTab?.LocalVersion;
        if (localVersion is null)
        {
            StatusMessage = "Cannot edit trigger words on a version that is not downloaded.";
            return;
        }

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(localVersion.ModelId);
            if (dbModel is null) return;

            var dbVersion = dbModel.Versions.FirstOrDefault(v => v.Id == localVersion.Id);
            if (dbVersion is null) return;

            var words = (TriggerWordsEditBuffer ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .ToList();

            dbVersion.TriggerWords.Clear();
            var order = 0;
            foreach (var word in words)
            {
                dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
            }
            dbVersion.IsUserEdited = true;
            dbModel.IsUserEdited = true;

            await unitOfWork.SaveChangesAsync();

            TriggerWordsDisplay = string.Join(", ", words);
            HasTriggerWords = words.Count > 0;
            IsEditingTriggerWords = false;

            await PostSaveRefreshAsync(unitOfWork, dbModel.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to save trigger words: {ex.Message}", ex);
            StatusMessage = $"Failed to save trigger words: {ex.Message}";
        }
    }

    #endregion

    #region Thumbnail upload

    /// <summary>
    /// Maximum width (px) for stored custom thumbnail. Mirrors
    /// <c>ModelTileViewModel.MaxThumbnailWidth</c> conventions.
    /// </summary>
    private const int CustomThumbnailMaxWidth = 640;

    /// <summary>
    /// Lets the user pick an image and stores it as the primary image's
    /// <c>ThumbnailData</c> BLOB on the currently selected version.
    /// </summary>
    /// TODO: Linux Implementation for native file picker fallback
    [RelayCommand]
    private async Task UploadThumbnailAsync()
    {
        var dialog = App.Services?.GetService<IDialogService>();
        if (dialog is null) return;

        var localVersion = SelectedVersionTab?.LocalVersion ?? SourceTile?.SelectedVersion;
        var model = SourceTile?.ModelEntity;
        if (model is null || localVersion is null)
        {
            StatusMessage = "Cannot upload a thumbnail without a local version.";
            return;
        }

        var path = await dialog.ShowOpenFileDialogAsync(
            "Choose thumbnail image",
            "*.png;*.jpg;*.jpeg;*.webp;*.bmp");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return;

        try
        {
            var (data, mime, width, height) = await Task.Run(() => EncodeThumbnail(path));
            if (data.Length == 0)
            {
                StatusMessage = "Selected image could not be decoded.";
                return;
            }

            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            var dbVersion = dbModel?.Versions.FirstOrDefault(v => v.Id == localVersion.Id);
            if (dbModel is null || dbVersion is null) return;

            // Reuse the primary image slot (sort 0) when present so the tile picks it up automatically.
            var image = dbVersion.Images.OrderBy(i => i.SortOrder).FirstOrDefault();
            if (image is null)
            {
                image = new ModelImage
                {
                    ModelVersionId = dbVersion.Id,
                    Url = $"user-thumbnail://{Guid.NewGuid():N}",
                    SortOrder = 0,
                };
                dbVersion.Images.Add(image);
            }

            image.ThumbnailData = data;
            image.ThumbnailMimeType = mime;
            image.ThumbnailWidth = width;
            image.ThumbnailHeight = height;
            image.IsLocalCacheValid = false;
            image.LocalCachePath = null;
            image.CachedAt = DateTimeOffset.UtcNow;

            dbModel.IsUserEdited = true;

            await unitOfWork.SaveChangesAsync();

            // Refresh in-memory bitmap and notify tile.
            using (var ms = new MemoryStream(data))
            {
                var bmp = new Bitmap(ms);
                await Dispatcher.UIThread.InvokeAsync(() => ThumbnailImage = bmp);
            }

            await PostSaveRefreshAsync(unitOfWork, model.Id);
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "ModelDetailEdit",
                $"Failed to upload thumbnail: {ex.Message}", ex);
            StatusMessage = $"Failed to upload thumbnail: {ex.Message}";
        }
    }

    private static (byte[] Data, string Mime, int Width, int Height) EncodeThumbnail(string path)
    {
        var raw = File.ReadAllBytes(path);
        using var src = SKBitmap.Decode(raw);
        if (src is null) return ([], string.Empty, 0, 0);

        SKBitmap working = src;
        SKBitmap? scaled = null;
        try
        {
            if (src.Width > CustomThumbnailMaxWidth)
            {
                var ratio = (float)CustomThumbnailMaxWidth / src.Width;
                var targetH = Math.Max(1, (int)(src.Height * ratio));
                scaled = src.Resize(new SKImageInfo(CustomThumbnailMaxWidth, targetH), SKFilterQuality.High);
                if (scaled is not null) working = scaled;
            }

            using var img = SKImage.FromBitmap(working);
            using var encoded = img.Encode(SKEncodedImageFormat.Jpeg, 85);
            return (encoded.ToArray(), "image/jpeg", working.Width, working.Height);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    #endregion

    #region Manual Civitai ID assignment

    /// <summary>
    /// True when the current model already has a Civitai ID linked. Used by the
    /// XAML to disable the "Assign Civitai IDs Manually" button (and surface a
    /// tooltip explaining why) since manual assignment only makes sense when
    /// auto-detection failed.
    /// </summary>
    public bool HasCivitaiId =>
        SourceTile?.ModelEntity is { } m && (m.CivitaiId is > 0 || m.CivitaiModelPageId is > 0);

    /// <summary>
    /// True when the Civitai action can open a valid model page.
    /// </summary>
    public bool CanOpenOnCivitai => HasCivitaiId;

    /// <summary>
    /// Tooltip shown on the "Open on Civitai" button, including the disabled-state reason.
    /// </summary>
    public string OpenOnCivitaiButtonTooltip => CanOpenOnCivitai
        ? "Open this LoRA's Civitai model page in your browser"
        : "This LoRA is not linked to a Civitai model yet. Use \"Assign Civitai IDs Manually\" first.";

    /// <summary>
    /// True when this detail view has meaningful metadata that can be removed.
    /// A bare database row created by local file discovery is not enough; at least
    /// one user-visible metadata field must be populated.
    /// </summary>
    public bool HasMetadata => HasDeletableMetadata(SourceTile?.ModelEntity);

    /// <summary>
    /// Tooltip shown on the "Delete Metadata" button, including the disabled-state reason.
    /// </summary>
    public string DeleteMetadataButtonTooltip => HasMetadata
        ? "Removes all database metadata for this LoRA. The .safetensors file on disk is NOT deleted."
        : "No metadata is available to delete. Metadata becomes deletable when Model ID, Version ID, Base Model, Category, Tags, Trigger Words, or Description is populated.";

    private static bool HasDeletableMetadata(Model? model)
    {
        if (model is null) return false;

        if (model.CivitaiId is > 0 || model.CivitaiModelPageId is > 0)
            return true;

        if (!string.IsNullOrWhiteSpace(model.Description))
            return true;

        if (model.UserCategory is { } category && category != CivitaiCategory.Unknown)
            return true;

        if (model.Tags.Any(mt => !string.IsNullOrWhiteSpace(mt.Tag?.Name)))
            return true;

        foreach (var version in model.Versions)
        {
            if (version.CivitaiId is > 0)
                return true;

            if (HasRealBaseModel(version.BaseModelRaw))
                return true;

            if (version.TriggerWords.Any(tw => !string.IsNullOrWhiteSpace(tw.Word)))
                return true;
        }

        return false;
    }

    private static bool HasRealBaseModel(string? baseModel)
        => !string.IsNullOrWhiteSpace(baseModel)
           && !string.Equals(baseModel.Trim(), "???", StringComparison.Ordinal)
           && !string.Equals(baseModel.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tooltip shown on the "Assign Civitai IDs Manually" button. Explains the
    /// disabled state when the model is already linked to Civitai.
    /// </summary>
    public string AssignCivitaiButtonTooltip => HasCivitaiId
        ? "This LoRA is already linked to a Civitai model. Use \"Delete Metadata\" first if you want to re-assign it."
        : "Manually paste a Civitai URL or enter Model/Version IDs when auto-detection failed";

    /// <summary>
    /// True while the user is being asked to confirm a metadata deletion.
    /// Toggles an inline confirmation strip (red Confirm + Cancel) below the
    /// action buttons; nothing is touched until the user clicks Confirm.
    /// </summary>
    [ObservableProperty]
    private bool _isConfirmingDeleteMetadata;

    /// <summary>
    /// Raised when the delete metadata confirmation strip becomes visible so
    /// the view can scroll it into view after layout has updated.
    /// </summary>
    public event EventHandler? DeleteMetadataConfirmationRequested;

    partial void OnSourceTileChanged(ModelTileViewModel? value)
    {
        OnPropertyChanged(nameof(HasCivitaiId));
        OnPropertyChanged(nameof(CanOpenOnCivitai));
        OnPropertyChanged(nameof(OpenOnCivitaiButtonTooltip));
        OnPropertyChanged(nameof(HasMetadata));
        OnPropertyChanged(nameof(DeleteMetadataButtonTooltip));
        OnPropertyChanged(nameof(AssignCivitaiButtonTooltip));
        IsConfirmingDeleteMetadata = false;
    }

    /// <summary>
    /// Opens a dialog where the user can paste a Civitai URL or enter Model/Version IDs
    /// manually, previews the resolved Civitai model, and on confirm persists the IDs
    /// plus the core Civitai metadata for this LoRA.
    /// </summary>
    /// TODO: Linux Implementation for owner window resolution
    [RelayCommand]
    private async Task AssignCivitaiIdsManuallyAsync()
    {
        var model = SourceTile?.ModelEntity;
        if (model is null)
        {
            StatusMessage = "No model selected.";
            return;
        }

        var owner = (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is null) return;

        var dialog = new AssignCivitaiIdsDialog();
        await dialog.ShowDialog(owner);

        if (!dialog.IsConfirmed || dialog.ResolvedModel is null)
            return;

        var civitaiModel = dialog.ResolvedModel;
        var civitaiVersion = dialog.ResolvedVersion ?? civitaiModel.ModelVersions.FirstOrDefault();

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var dbModel = await unitOfWork.Models.GetByIdWithIncludesAsync(model.Id);
            if (dbModel is null) return;

            // --- Model-level IDs + metadata ---
            dbModel.CivitaiModelPageId = civitaiModel.Id;

            var civIdTaken = await unitOfWork.Models.IsCivitaiIdTakenAsync(civitaiModel.Id, dbModel.Id);
            if (!civIdTaken)
            {
                dbModel.CivitaiId = civitaiModel.Id;
            }
            else
            {
                _logger?.Warn(LogCategory.Network, "AssignCivitaiIds",
                    $"CivitaiId {civitaiModel.Id} is already used by another model — only the page ID was assigned.");
            }

            // Refresh model fields unless the user has explicitly edited this model already.
            if (!dbModel.IsUserEdited)
            {
                dbModel.Name = civitaiModel.Name;
                dbModel.Description = civitaiModel.Description;
            }
            dbModel.IsNsfw = civitaiModel.Nsfw;
            dbModel.IsPoi = civitaiModel.Poi;
            dbModel.Source = DataSource.CivitaiApi;
            dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
            dbModel.AllowNoCredit = civitaiModel.AllowNoCredit;
            dbModel.AllowDerivatives = civitaiModel.AllowDerivatives;
            dbModel.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;

            if (civitaiModel.Creator is not null)
            {
                if (dbModel.Creator is not null)
                {
                    dbModel.Creator.Username = civitaiModel.Creator.Username;
                    dbModel.Creator.AvatarUrl ??= civitaiModel.Creator.Image;
                }
                else
                {
                    var existingCreator = await unitOfWork.Models
                        .FindCreatorByUsernameAsync(civitaiModel.Creator.Username);
                    dbModel.Creator = existingCreator ?? new Creator
                    {
                        Username = civitaiModel.Creator.Username,
                        AvatarUrl = civitaiModel.Creator.Image,
                    };
                }
            }

            // --- Version-level IDs + metadata ---
            // Pick the local version to attach the IDs to: the currently selected one,
            // falling back to the first local version on the model.
            var dbVersion = SelectedVersionTab?.LocalVersion is { } selectedLocal
                ? dbModel.Versions.FirstOrDefault(v => v.Id == selectedLocal.Id)
                : dbModel.Versions.FirstOrDefault();

            if (dbVersion is not null && civitaiVersion is not null)
            {
                var versionIdTaken = await unitOfWork.Models
                    .IsVersionCivitaiIdTakenAsync(civitaiVersion.Id, dbVersion.Id);
                if (!versionIdTaken)
                {
                    dbVersion.CivitaiId = civitaiVersion.Id;
                }
                else
                {
                    _logger?.Warn(LogCategory.Network, "AssignCivitaiIds",
                        $"Version CivitaiId {civitaiVersion.Id} is already used by another version — skipped.");
                }

                if (!dbVersion.IsUserEdited)
                {
                    dbVersion.Name = civitaiVersion.Name;
                    dbVersion.Description = civitaiVersion.Description;
                }
                dbVersion.BaseModelRaw = civitaiVersion.BaseModel;
                dbVersion.DownloadUrl = civitaiVersion.DownloadUrl;
                dbVersion.DownloadCount = civitaiVersion.Stats?.DownloadCount ?? 0;
                dbVersion.PublishedAt = civitaiVersion.PublishedAt;
                dbVersion.EarlyAccessDays = civitaiVersion.EarlyAccessTimeFrame;

                // Trigger words — only when the version has not been user-edited.
                if (!dbVersion.IsUserEdited)
                {
                    dbVersion.TriggerWords.Clear();
                    var order = 0;
                    foreach (var word in civitaiVersion.TrainedWords)
                    {
                        dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
                    }
                }

                // Add new images (skip duplicates by CivitaiId).
                var existingImageIds = dbVersion.Images
                    .Where(i => i.CivitaiId.HasValue)
                    .Select(i => i.CivitaiId!.Value)
                    .ToHashSet();
                var sortOrder = dbVersion.Images.Count;
                foreach (var civImage in civitaiVersion.Images)
                {
                    if (string.IsNullOrEmpty(civImage.Url)) continue;
                    if (civImage.Id.HasValue && existingImageIds.Contains(civImage.Id.Value)) continue;

                    dbVersion.Images.Add(new ModelImage
                    {
                        ModelVersionId = dbVersion.Id,
                        CivitaiId = civImage.Id,
                        Url = civImage.Url,
                        MediaType = civImage.Type,
                        IsNsfw = civImage.Nsfw,
                        Width = civImage.Width,
                        Height = civImage.Height,
                        BlurHash = civImage.Hash,
                        SortOrder = sortOrder++,
                        CreatedAt = civImage.CreatedAt,
                        PostId = civImage.PostId,
                        Username = civImage.Username,
                        Prompt = civImage.Meta?.Prompt,
                        NegativePrompt = civImage.Meta?.NegativePrompt,
                        Seed = civImage.Meta?.Seed,
                        Steps = civImage.Meta?.Steps,
                        Sampler = civImage.Meta?.Sampler,
                        CfgScale = civImage.Meta?.CfgScale,
                    });
                }

                // File hashes from the primary Civitai file.
                var civFile = civitaiVersion.Files.FirstOrDefault(f => f.Primary == true)
                              ?? civitaiVersion.Files.FirstOrDefault();
                if (civFile?.Hashes is not null)
                {
                    var dbFile = dbVersion.PrimaryFile ?? dbVersion.Files.FirstOrDefault();
                    if (dbFile is not null)
                    {
                        dbFile.CivitaiId ??= civFile.Id;
                        dbFile.HashSHA256 ??= civFile.Hashes.SHA256;
                        dbFile.HashAutoV2 ??= civFile.Hashes.AutoV2;
                        dbFile.HashCRC32 ??= civFile.Hashes.CRC32;
                        dbFile.HashBLAKE3 ??= civFile.Hashes.BLAKE3;
                    }
                }
            }

            // Tags — only when the model has not been user-edited.
            if (!dbModel.IsUserEdited && civitaiModel.Tags is { Count: > 0 } tags)
            {
                var lookup = await unitOfWork.Models.GetAllTagsLookupAsync();
                dbModel.Tags.Clear();
                foreach (var tagName in tags)
                {
                    if (string.IsNullOrWhiteSpace(tagName)) continue;
                    var normalized = tagName.Trim().ToLowerInvariant();
                    if (!lookup.TryGetValue(normalized, out var tag))
                    {
                        tag = new Tag { Name = tagName, NormalizedName = normalized };
                        lookup[normalized] = tag;
                    }
                    dbModel.Tags.Add(new ModelTag { Tag = tag });
                }
            }

            await unitOfWork.SaveChangesAsync();

            _logger?.Info(LogCategory.Network, "AssignCivitaiIds",
                $"Assigned Civitai IDs to '{dbModel.Name}' (ModelId={civitaiModel.Id}, VersionId={civitaiVersion?.Id})");

            // Reload the detail view from the freshly persisted entity.
            await PostSaveRefreshAsync(unitOfWork, dbModel.Id);

            var refreshed = await unitOfWork.Models.GetByIdWithIncludesAsync(dbModel.Id);
            if (refreshed is not null && SourceTile is not null)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    SourceTile.RefreshModelData(refreshed);
                    await LoadAsync(SourceTile);
                });
            }

            StatusMessage = "Civitai IDs assigned successfully.";
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.Network, "AssignCivitaiIds",
                $"Failed to assign Civitai IDs: {ex.Message}", ex);
            StatusMessage = $"Failed to assign Civitai IDs: {ex.Message}";
        }
    }

    /// <summary>
    /// Shows the inline "are you sure?" confirmation strip for metadata deletion.
    /// No data is touched until <see cref="ConfirmDeleteMetadataAsync"/> runs.
    /// </summary>
    [RelayCommand]
    private void RequestDeleteMetadata()
    {
        if (!HasMetadata)
        {
            StatusMessage = "No metadata is available to delete for this LoRA.";
            return;
        }

        IsConfirmingDeleteMetadata = true;
        DeleteMetadataConfirmationRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Hides the inline confirmation strip without deleting anything.
    /// </summary>
    [RelayCommand]
    private void CancelDeleteMetadata()
    {
        IsConfirmingDeleteMetadata = false;
    }

    /// <summary>
    /// Deletes ALL database metadata for this LoRA — the model record and every
    /// version/file/image/trigger-word it owns (cascade) — but leaves the
    /// safetensors file on disk untouched. Closes the detail panel and removes
    /// the source tile from the grid.
    /// </summary>
    [RelayCommand]
    private async Task ConfirmDeleteMetadataAsync()
    {
        IsConfirmingDeleteMetadata = false;

        var tile = SourceTile;
        var model = tile?.ModelEntity;
        if (tile is null || model is null)
        {
            StatusMessage = "No model selected.";
            return;
        }

        try
        {
            using var scope = App.Services!.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            // A tile may group multiple Model rows (e.g. multiple versions of
            // the same LoRA). Delete every grouped row so no orphan
            // images/versions remain after the user confirms.
            var ids = tile.GetAllModelIds();
            var deleted = 0;
            foreach (var id in ids)
            {
                var dbModel = await unitOfWork.Models.GetByIdAsync(id);
                if (dbModel is not null)
                {
                    unitOfWork.Models.Remove(dbModel);
                    deleted++;
                }
            }

            if (deleted > 0)
            {
                await unitOfWork.SaveChangesAsync();
            }

            _logger?.Info(LogCategory.General, "DeleteMetadata",
                $"Deleted metadata for '{model.Name}' ({deleted} of {ids.Count} grouped model row(s)). Files on disk were not touched.");

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Order matters and each step is required:
                // 1. RaiseDeleted() — synchronously removes the tile from the
                //    parent's AllTiles/FilteredTiles collections AND updates
                //    TotalModelCount / FilteredModelCount so the overview
                //    reflects the deletion immediately.
                // 2. RaiseMetadataDeleted() — asks the parent VM to re-discover
                //    the on-disk safetensors so a fresh bare-metadata tile
                //    reappears without requiring a manual refresh.
                // 3. CloseRequested — closes the detail panel last; this
                //    triggers CloseDetail() which unsubscribes MetadataDeleted,
                //    so it must come AFTER step 2.
                tile.RaiseDeleted();
                RaiseMetadataDeleted();
                CloseRequested?.Invoke(this, EventArgs.Empty);
            });
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, "DeleteMetadata",
                $"Failed to delete metadata for '{model.Name}': {ex.Message}", ex);
            StatusMessage = $"Failed to delete metadata: {ex.Message}";
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Reloads the model after a save and refreshes the source tile so grid
    /// thumbnails / tag chips update without requiring a full re-scan.
    /// </summary>
    private async Task PostSaveRefreshAsync(IUnitOfWork unitOfWork, int modelId)
    {
        var refreshed = await unitOfWork.Models.GetByIdWithIncludesAsync(modelId);
        if (refreshed is null) return;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            SourceTile?.RefreshModelData(refreshed);
            _ = LoadEditableTagsAsync();
            PopulateTags(refreshed);
        });
    }

    #endregion
}

/// <summary>
/// Removable tag chip used by the editable Tags section in the LoRA detail view.
/// </summary>
public partial class EditableTagItem : ObservableObject
{
    private readonly Func<EditableTagItem, Task> _removeAsync;

    public EditableTagItem(string name, Func<EditableTagItem, Task> removeAsync)
    {
        Name = name;
        _removeAsync = removeAsync;
    }

    public string Name { get; }

    /// <summary>
    /// True when this tag is the one that drives the LoRA's category. The UI
    /// highlights such chips and disables their remove button so the user
    /// can't silently change the category by deleting the tag.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TooltipText))]
    private bool _isCategoryTag;

    /// <summary>Hover tooltip shown for the chip (locked vs. removable).</summary>
    public string TooltipText => IsCategoryTag
        ? "This is the LoRA's category tag and cannot be removed. Change the Category dropdown above to swap it."
        : Name;

    [RelayCommand]
    private Task RemoveAsync() => _removeAsync(this);
}
