using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// The record chain a copy sort gives each copy (#653). The library keeps one chain per file on
/// disk (two copies of a LoRA are two Model → ModelVersion → ModelFile chains, linked by
/// <see cref="Model.CivitaiModelPageId"/>), so a copy gets its own, carrying what makes it look and
/// sort like the original: name, kind, base model, tags, the user's category and edits, NSFW and
/// licence flags, trigger words, creator, hashes.
/// </summary>
/// <remarks>
/// Left out on purpose: the Civitai model and version ids, which only one chain may hold (unique
/// indexes — <c>CivitaiMetadataApplier</c> leaves them off a duplicate the same way), and the
/// images, whose thumbnail BLOBs would be duplicated per copy; tiles group by Civitai page, and the
/// library sync fetches a chain's own images.
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
}
