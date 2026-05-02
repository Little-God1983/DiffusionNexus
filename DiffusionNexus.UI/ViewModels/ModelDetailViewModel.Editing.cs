using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
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

    [RelayCommand]
    private Task RemoveAsync() => _removeAsync(this);
}
