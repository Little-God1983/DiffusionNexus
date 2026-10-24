using DiffusionNexus.Domain.Entities;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// The record a copy sort gives each copy (#653): one more file of the original's version, the way
/// a re-download of a known version is attached (#380, <c>LoraDownloadService</c>). The schema
/// allows many files per version, and the Installed tab builds one tile per (Civitai page, source
/// folder) with a version → file map — so the copy shares everything the original has (Civitai
/// ids, tags, images, sync state, the user's edits, and any later change to them) with nothing
/// duplicated.
/// </summary>
public static class SortCopyRecords
{
    /// <param name="original">The original's file record.</param>
    public static ModelFile CreateFileFor(ModelFile original, string copyPath) => new()
    {
        ModelVersionId = original.ModelVersionId,
        CivitaiId = original.CivitaiId,
        FileName = Path.GetFileName(copyPath),
        SizeKB = original.SizeKB,
        FileSizeBytes = original.FileSizeBytes,
        FileType = original.FileType,
        // The original stays the version's primary file; the copy is one more location of it.
        IsPrimary = false,
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
    };
}
