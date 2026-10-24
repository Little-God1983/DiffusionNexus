using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Service.Services.Sync.Thumbnails;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// The record chain a copy sort gives each copy (#653). The library keeps one chain per file on
/// disk (two copies of a LoRA are two Model → ModelVersion → ModelFile chains, linked by
/// <see cref="Model.CivitaiModelPageId"/>), so a copy gets its own, carrying what makes it look and
/// sort like the original: name, kind, base model, tags, the user's category and edits, NSFW and
/// licence flags, trigger words, creator, hashes, images and sync state.
/// </summary>
/// <remarks>
/// Left out on purpose: the Civitai model and version ids, which only one chain may hold (unique
/// indexes — <c>CivitaiMetadataApplier</c> leaves them off a duplicate the same way).
/// <para>
/// The images are copied, thumbnail BLOBs included: tiles are per source folder, so a copy in
/// another source is a tile of its own, and the image sync never fetches for a version without a
/// Civitai id — without them that tile had no picture, permanently. The sync state is copied so
/// the library sync treats the copy as already identified, instead of hashing it and asking
/// Civitai again for what was just copied.
/// </para>
/// </remarks>
public static class SortCopyRecords
{
    /// <param name="original">The original's file record, with its version (trigger words) and
    /// model (tags) loaded.</param>
    public static Model CreateFor(ModelFile original, string copyPath)
    {
        var version = original.ModelVersion
            ?? throw new ArgumentException("The original's version must be loaded.", nameof(original));
        var model = version.Model
            ?? throw new ArgumentException("The original's model must be loaded.", nameof(original));

        var copyModel = new Model
        {
            CivitaiModelPageId = model.CivitaiModelPageId ?? model.CivitaiId,
            Name = model.Name,
            Description = model.Description,
            Type = model.Type,
            IsNsfw = model.IsNsfw,
            UserNsfwRating = model.UserNsfwRating,
            OriginalNsfwRating = model.OriginalNsfwRating,
            IsPoi = model.IsPoi,
            Mode = model.Mode,
            Source = model.Source,
            LastSyncedAt = model.LastSyncedAt,
            LastCheckedForUpdatesUtc = model.LastCheckedForUpdatesUtc,
            TotalVersionCount = model.TotalVersionCount,
            IsUserEdited = model.IsUserEdited,
            UserCategory = model.UserCategory,
            AllowNoCredit = model.AllowNoCredit,
            AllowCommercialUse = model.AllowCommercialUse,
            AllowDerivatives = model.AllowDerivatives,
            AllowDifferentLicense = model.AllowDifferentLicense,
            CreatorId = model.CreatorId,
        };
        foreach (var tag in model.Tags)
            copyModel.Tags.Add(new ModelTag { Model = copyModel, TagId = tag.TagId });

        if (model.SyncState is { } state)
        {
            copyModel.SyncState = new ModelSyncState
            {
                Model = copyModel,
                MetadataCheckedAt = state.MetadataCheckedAt,
                MetadataOutcome = state.MetadataOutcome,
                MetadataAttempts = state.MetadataAttempts,
                LastError = state.LastError,
                TagsCheckedAt = state.TagsCheckedAt,
                ImagesCheckedAt = state.ImagesCheckedAt,
                SidecarSignature = state.SidecarSignature,
                HeaderCheckedAt = state.HeaderCheckedAt,
            };
        }

        var copyVersion = new ModelVersion
        {
            Name = version.Name,
            Description = version.Description,
            BaseModelRaw = version.BaseModelRaw,
            PublishedAt = version.PublishedAt,
            DownloadUrl = version.DownloadUrl,
            EarlyAccessDays = version.EarlyAccessDays,
            IsUserEdited = version.IsUserEdited,
            DownloadCount = version.DownloadCount,
            RatingCount = version.RatingCount,
            Rating = version.Rating,
            ThumbsUpCount = version.ThumbsUpCount,
            ThumbsDownCount = version.ThumbsDownCount,
            Model = copyModel,
        };
        foreach (var word in version.TriggerWords)
            copyVersion.TriggerWords.Add(new TriggerWord { Word = word.Word, Order = word.Order, ModelVersion = copyVersion });

        foreach (var image in version.Images)
        {
            copyVersion.Images.Add(new ModelImage
            {
                CivitaiId = image.CivitaiId,
                Url = CopyImageUrl(image.Url, original.LocalPath, copyPath),
                MediaType = image.MediaType,
                NsfwLevel = image.NsfwLevel,
                Width = image.Width,
                Height = image.Height,
                BlurHash = image.BlurHash,
                SortOrder = image.SortOrder,
                CreatedAt = image.CreatedAt,
                PostId = image.PostId,
                Username = image.Username,
                ThumbnailData = image.ThumbnailData,
                ThumbnailMimeType = image.ThumbnailMimeType,
                ThumbnailWidth = image.ThumbnailWidth,
                ThumbnailHeight = image.ThumbnailHeight,
                ThumbnailAttemptedAt = image.ThumbnailAttemptedAt,
                ThumbnailFailure = image.ThumbnailFailure,
                LocalCachePath = image.LocalCachePath,
                IsLocalCacheValid = image.IsLocalCacheValid,
                CachedAt = image.CachedAt,
                CachedFileSize = image.CachedFileSize,
                Prompt = image.Prompt,
                NegativePrompt = image.NegativePrompt,
                Seed = image.Seed,
                Steps = image.Steps,
                Sampler = image.Sampler,
                CfgScale = image.CfgScale,
                GenerationModel = image.GenerationModel,
                DenoisingStrength = image.DenoisingStrength,
                LikeCount = image.LikeCount,
                HeartCount = image.HeartCount,
                CommentCount = image.CommentCount,
                ModelVersion = copyVersion,
            });
        }

        copyVersion.Files.Add(new ModelFile
        {
            CivitaiId = original.CivitaiId,
            FileName = Path.GetFileName(copyPath),
            SizeKB = original.SizeKB,
            FileSizeBytes = original.FileSizeBytes,
            FileType = original.FileType,
            IsPrimary = original.IsPrimary,
            Format = original.Format,
            Precision = original.Precision,
            SizeType = original.SizeType,
            DownloadUrl = original.DownloadUrl,
            PickleScanResult = original.PickleScanResult,
            PickleScanMessage = original.PickleScanMessage,
            VirusScanResult = original.VirusScanResult,
            ScannedAt = original.ScannedAt,
            HashAutoV1 = original.HashAutoV1,
            HashAutoV2 = original.HashAutoV2,
            HashSHA256 = original.HashSHA256,
            HashCRC32 = original.HashCRC32,
            HashBLAKE3 = original.HashBLAKE3,
            LocalPath = copyPath,
            IsLocalFileValid = true,
            LocalFileVerifiedAt = DateTimeOffset.UtcNow,
            ModelVersion = copyVersion,
        });
        copyModel.Versions.Add(copyVersion);
        return copyModel;
    }

    /// <summary>
    /// A local preview (<c>file://</c>) beside the original that the sorter copied next to the copy
    /// with its other sidecars: the copy's image points at that one, not at the original's, which
    /// the copy would otherwise depend on. Only when it is really there — the executor copies known
    /// sidecars only, and a failed sidecar copy is just a warning — otherwise, and for any other URL,
    /// the original's URL is kept.
    /// </summary>
    private static string CopyImageUrl(string url, string? originalPath, string copyPath)
    {
        if (originalPath is null || !LocalPreviewFiles.TryGetLocalPath(url, out var previewPath))
            return url;

        var besideOriginal = string.Equals(Path.GetDirectoryName(previewPath), Path.GetDirectoryName(originalPath),
                                 StringComparison.OrdinalIgnoreCase)
                             && Path.GetFileName(previewPath).StartsWith(Path.GetFileNameWithoutExtension(originalPath),
                                 StringComparison.OrdinalIgnoreCase);
        if (!besideOriginal)
            return url;

        var copiedPreview = SidecarLocator.DeriveSidecarTargetPath(previewPath, originalPath, copyPath);
        return File.Exists(copiedPreview) ? LocalPreviewFiles.FileUrlPrefix + copiedPreview : url;
    }
}
