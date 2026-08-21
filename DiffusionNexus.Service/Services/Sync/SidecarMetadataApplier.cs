using System.Text.Json;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using SkiaSharp;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// The sidecar file discovered next to a model file, plus a cheap change signature
/// (<c>{fullPath}|{lastWriteUtc.Ticks}|{length}</c>) callers can persist to detect
/// that the sidecar changed since the last sync. Both members describe "no sidecar"
/// as <c>null</c> path and an empty signature.
/// </summary>
public sealed record SidecarLookup(string? SidecarPath, string Signature);

/// <summary>
/// Outcome of <see cref="SidecarMetadataApplier.ApplyAsync"/>.
/// <paramref name="Applied"/> covers the sidecar metadata only; a local preview image
/// can still have been stored (<paramref name="ThumbnailApplied"/>) when no sidecar exists.
/// </summary>
public sealed record SidecarApplyResult(bool Applied, string? SidecarPath, string Signature, bool ThumbnailApplied);

/// <summary>
/// Reads local <c>.civitai.info</c> / <c>.json</c> metadata files next to a model file and
/// applies the discovered metadata to the model entity in the database. Also discovers local
/// preview images (same base name, image extension) and stores them as thumbnail BLOBs so
/// tiles show a preview without Civitai.
/// <para>
/// Two sidecar formats are supported:
/// <list type="bullet">
///   <item><c>.civitai.info</c> — version-level Civitai response (top-level: id, modelId, baseModel, trainedWords, model{}, images[], files[])</item>
///   <item><c>.json</c> — model-level Civitai response (top-level: id, name, type, nsfw, modelVersions[]) OR simple metadata (sd version, tags)</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// Moved verbatim out of <c>LoraViewerViewModel.TryApplyLocalMetadataFallbackAsync</c> (#521 WP2)
/// so the sync pipeline — not a ViewModel — owns the sidecar → entity mapping. The caller owns
/// the unit of work (and therefore the DbContext scope): this class only mutates the graph and saves.
/// </remarks>
public sealed class SidecarMetadataApplier
{
    private const string LogSource = "LibrarySync";

    private readonly IUnifiedLogger? _logger;

    public SidecarMetadataApplier(IUnifiedLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Locates the sidecar for <paramref name="modelFilePath"/>, preferring the richer
    /// <c>{base}.civitai.info</c> over <c>{base}.json</c> (base = file name without extension).
    /// Returns a <c>null</c> path and an empty signature when neither exists.
    /// </summary>
    public static SidecarLookup Find(string modelFilePath)
    {
        var none = new SidecarLookup(null, string.Empty);

        try
        {
            var fileInfo = new FileInfo(modelFilePath);
            var directory = fileInfo.Directory;
            if (directory is null || !directory.Exists) return none;

            var baseName = Path.GetFileNameWithoutExtension(fileInfo.Name);

            // Look for .civitai.info or .json sidecar files
            var civitaiInfoFile = directory.GetFiles($"{baseName}.civitai.info").FirstOrDefault();
            var jsonFile = directory.GetFiles($"{baseName}.json").FirstOrDefault();

            // Prefer .civitai.info (richer version-level data)
            var sidecar = civitaiInfoFile ?? jsonFile;
            if (sidecar is null) return none;

            return new SidecarLookup(
                sidecar.FullName,
                $"{sidecar.FullName}|{sidecar.LastWriteTimeUtc.Ticks}|{sidecar.Length}");
        }
        catch
        {
            return none;
        }
    }

    /// <summary>
    /// Parses the sidecar (if any) into the DB graph of <paramref name="modelId"/> / the version
    /// owning <paramref name="modelFilePath"/>, then applies a local preview image next to the file
    /// as the version's thumbnail BLOB when no thumbnail exists yet. Saves. Never throws.
    /// </summary>
    public async Task<SidecarApplyResult> ApplyAsync(
        IUnitOfWork uow,
        int modelId,
        string modelFilePath,
        CancellationToken ct = default)
    {
        var notApplied = new SidecarApplyResult(false, null, string.Empty, false);

        try
        {
            var fileInfo = new FileInfo(modelFilePath);
            var directory = fileInfo.Directory;
            if (directory is null || !directory.Exists) return notApplied;

            var baseName = Path.GetFileNameWithoutExtension(fileInfo.Name);
            var lookup = Find(modelFilePath);

            if (lookup.SidecarPath is null)
            {
                // No sidecar metadata — still try local thumbnail
                var thumbnailOnly = await TryApplyLocalThumbnailAsync(
                    uow, modelId, modelFilePath, directory, baseName, ct);
                return new SidecarApplyResult(false, null, string.Empty, thumbnailOnly);
            }

            var isCivitaiInfoFormat = lookup.SidecarPath.EndsWith(
                ".civitai.info", StringComparison.OrdinalIgnoreCase);

            var json = await File.ReadAllTextAsync(lookup.SidecarPath, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Load ONLY the target model — not the entire database
            var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
            if (dbModel is null) return notApplied;

            var dirty = false;

            // Locate the DB version that owns the file at modelFilePath
            var dbVersion = dbModel.Versions.FirstOrDefault(v =>
                v.Files.Any(f => string.Equals(f.LocalPath, modelFilePath, StringComparison.OrdinalIgnoreCase)));

            if (isCivitaiInfoFormat)
            {
                dirty = await ApplyCivitaiInfoFormatAsync(root, dbModel, dbVersion, uow.Models, modelFilePath, ct);
            }
            else
            {
                // .json may be model-level (has "modelVersions") or simple metadata (has "sd version")
                dirty = root.TryGetProperty("modelVersions", out _)
                    ? await ApplyModelLevelJsonFormatAsync(root, dbModel, dbVersion, uow.Models, modelFilePath, ct)
                    : ApplySimpleJsonFormat(root, dbModel, dbVersion);
            }

            // Always mark source and sync time
            dbModel.Source = DataSource.LocalFile;
            dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
            dirty = true;

            if (dirty)
            {
                await uow.SaveChangesAsync(ct);
            }

            // Try to find and apply a local thumbnail image
            var thumbnailApplied = await TryApplyLocalThumbnailAsync(
                uow, modelId, modelFilePath, directory, baseName, ct);

            return new SidecarApplyResult(dirty, lookup.SidecarPath, lookup.Signature, thumbnailApplied);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Failed to read local metadata for '{Path.GetFileName(modelFilePath)}': {ex.Message}");
            return notApplied;
        }
    }

    /// <summary>
    /// Applies metadata from a .civitai.info sidecar (version-level Civitai response).
    /// Extracts: model name, NSFW, modelId → CivitaiId/CivitaiModelPageId,
    /// version id → CivitaiId, baseModel, trainedWords, image URLs, file hashes.
    /// </summary>
    private static async Task<bool> ApplyCivitaiInfoFormatAsync(
        JsonElement root,
        Model dbModel,
        ModelVersion? dbVersion,
        IModelRepository modelRepo,
        string localPath,
        CancellationToken ct)
    {
        var dirty = false;

        // ── Model-level fields from nested "model" object ──
        if (root.TryGetProperty("model", out var modelProp))
        {
            if (modelProp.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
            {
                dbModel.Name = nameStr;
                dirty = true;
            }

            if (modelProp.TryGetProperty("nsfw", out var nsfw)
                && nsfw.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                dbModel.IsNsfw = nsfw.GetBoolean();
                dirty = true;
            }
        }

        // ── CivitaiId / CivitaiModelPageId from "modelId" ──
        if (root.TryGetProperty("modelId", out var modelIdProp) && modelIdProp.TryGetInt32(out var civitaiModelId))
        {
            dbModel.CivitaiModelPageId = civitaiModelId;

            var civitaiIdTaken = await modelRepo.IsCivitaiIdTakenAsync(civitaiModelId, dbModel.Id, ct);
            if (!civitaiIdTaken)
            {
                dbModel.CivitaiId = civitaiModelId;
            }

            dirty = true;
        }

        // ── Version-level fields ──
        if (dbVersion is not null)
        {
            // Version CivitaiId from top-level "id"
            if (root.TryGetProperty("id", out var versionIdProp) && versionIdProp.TryGetInt32(out var civitaiVersionId))
            {
                var versionIdTaken = await modelRepo.IsVersionCivitaiIdTakenAsync(civitaiVersionId, dbVersion.Id, ct);
                if (!versionIdTaken)
                {
                    dbVersion.CivitaiId = civitaiVersionId;
                    dirty = true;
                }
            }

            // Version name
            if (root.TryGetProperty("name", out var vName) && vName.GetString() is { } vNameStr)
            {
                dbVersion.Name = vNameStr;
                dirty = true;
            }

            // Base model
            if (root.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
            {
                dbVersion.BaseModelRaw = baseModelStr;
                dirty = true;
            }

            // Trained words / trigger words
            if (root.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == JsonValueKind.Array)
            {
                dbVersion.TriggerWords.Clear();
                var order = 0;
                foreach (var wordElement in trained.EnumerateArray())
                {
                    if (wordElement.ValueKind == JsonValueKind.String && wordElement.GetString() is { } word)
                    {
                        dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
                        dirty = true;
                    }
                }
            }

            // Download URL
            if (root.TryGetProperty("downloadUrl", out var dlUrl) && dlUrl.GetString() is { } dlUrlStr)
            {
                dbVersion.DownloadUrl = dlUrlStr;
                dirty = true;
            }

            // ── Image URLs from "images" array ──
            if (root.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
            {
                dirty |= ApplyImagesFromJson(dbVersion, images);
            }

            // ── File hashes from "files" array ──
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                dirty |= ApplyFileHashesFromJson(dbVersion, files, localPath);
            }
        }

        return dirty;
    }

    /// <summary>
    /// Applies metadata from a .json sidecar that is a model-level Civitai response
    /// (has top-level id, name, type, nsfw, modelVersions[]).
    /// Finds the matching version by filename in the modelVersions array.
    /// </summary>
    private static async Task<bool> ApplyModelLevelJsonFormatAsync(
        JsonElement root,
        Model dbModel,
        ModelVersion? dbVersion,
        IModelRepository modelRepo,
        string localPath,
        CancellationToken ct)
    {
        var dirty = false;
        var localFileName = Path.GetFileName(localPath);

        // ── Model-level fields ──
        if (root.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
        {
            dbModel.Name = nameStr;
            dirty = true;
        }

        if (root.TryGetProperty("nsfw", out var nsfw)
            && nsfw.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            dbModel.IsNsfw = nsfw.GetBoolean();
            dirty = true;
        }

        // CivitaiId / CivitaiModelPageId from top-level "id"
        if (root.TryGetProperty("id", out var modelIdProp) && modelIdProp.TryGetInt32(out var civitaiModelId))
        {
            dbModel.CivitaiModelPageId = civitaiModelId;

            var civitaiIdTaken = await modelRepo.IsCivitaiIdTakenAsync(civitaiModelId, dbModel.Id, ct);
            if (!civitaiIdTaken)
            {
                dbModel.CivitaiId = civitaiModelId;
            }

            dirty = true;
        }

        // ── Find the matching version in modelVersions[] by filename ──
        if (dbVersion is not null
            && root.TryGetProperty("modelVersions", out var versions)
            && versions.ValueKind == JsonValueKind.Array)
        {
            foreach (var versionElement in versions.EnumerateArray())
            {
                // Match by filename in the "files" array
                var matchedByFile = false;
                if (versionElement.TryGetProperty("files", out var vFiles) && vFiles.ValueKind == JsonValueKind.Array)
                {
                    foreach (var fileElement in vFiles.EnumerateArray())
                    {
                        if (fileElement.TryGetProperty("name", out var fName)
                            && string.Equals(fName.GetString(), localFileName, StringComparison.OrdinalIgnoreCase))
                        {
                            matchedByFile = true;
                            break;
                        }
                    }
                }

                if (!matchedByFile) continue;

                // Found the matching version — extract all data
                if (versionElement.TryGetProperty("id", out var vIdProp) && vIdProp.TryGetInt32(out var civitaiVersionId))
                {
                    var versionIdTaken = await modelRepo.IsVersionCivitaiIdTakenAsync(civitaiVersionId, dbVersion.Id, ct);
                    if (!versionIdTaken)
                    {
                        dbVersion.CivitaiId = civitaiVersionId;
                        dirty = true;
                    }
                }

                if (versionElement.TryGetProperty("name", out var vName) && vName.GetString() is { } vNameStr)
                {
                    dbVersion.Name = vNameStr;
                    dirty = true;
                }

                if (versionElement.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
                {
                    dbVersion.BaseModelRaw = baseModelStr;
                    dirty = true;
                }

                if (versionElement.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == JsonValueKind.Array)
                {
                    dbVersion.TriggerWords.Clear();
                    var order = 0;
                    foreach (var wordElement in trained.EnumerateArray())
                    {
                        if (wordElement.ValueKind == JsonValueKind.String && wordElement.GetString() is { } word)
                        {
                            dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
                            dirty = true;
                        }
                    }
                }

                if (versionElement.TryGetProperty("downloadUrl", out var dlUrl) && dlUrl.GetString() is { } dlUrlStr)
                {
                    dbVersion.DownloadUrl = dlUrlStr;
                    dirty = true;
                }

                // Images from this version
                if (versionElement.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    dirty |= ApplyImagesFromJson(dbVersion, images);
                }

                // File hashes from this version
                if (versionElement.TryGetProperty("files", out var filesArr) && filesArr.ValueKind == JsonValueKind.Array)
                {
                    dirty |= ApplyFileHashesFromJson(dbVersion, filesArr, localPath);
                }

                break; // Only match one version per file
            }
        }

        return dirty;
    }

    /// <summary>
    /// Applies metadata from a simple .json sidecar (has "sd version", "type", "tags" — not a full Civitai response).
    /// </summary>
    private static bool ApplySimpleJsonFormat(
        JsonElement root,
        Model dbModel,
        ModelVersion? dbVersion)
    {
        var dirty = false;

        if (root.TryGetProperty("model", out var modelProp))
        {
            if (modelProp.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
            {
                dbModel.Name = nameStr;
                dirty = true;
            }

            if (modelProp.TryGetProperty("nsfw", out var nsfw)
                && nsfw.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                dbModel.IsNsfw = nsfw.GetBoolean();
                dirty = true;
            }
        }

        if (dbVersion is not null)
        {
            if (root.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
            {
                dbVersion.BaseModelRaw = baseModelStr;
                dirty = true;
            }

            // Fallback: "sd version" for very old sidecar format
            if (dbVersion.BaseModelRaw is null or "???"
                && root.TryGetProperty("sd version", out var sdVer) && sdVer.GetString() is { } sdVerStr)
            {
                dbVersion.BaseModelRaw = sdVerStr;
                dirty = true;
            }

            if (root.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == JsonValueKind.Array)
            {
                dbVersion.TriggerWords.Clear();
                var order = 0;
                foreach (var wordElement in trained.EnumerateArray())
                {
                    if (wordElement.ValueKind == JsonValueKind.String && wordElement.GetString() is { } word)
                    {
                        dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
                        dirty = true;
                    }
                }
            }
        }

        return dirty;
    }

    /// <summary>
    /// Creates <see cref="ModelImage"/> entities from a JSON "images" array found in sidecar files.
    /// Skips images that already exist by CivitaiId or URL.
    /// </summary>
    private static bool ApplyImagesFromJson(ModelVersion dbVersion, JsonElement imagesArray)
    {
        var dirty = false;
        var existingUrls = dbVersion.Images
            .Select(i => i.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sortOrder = dbVersion.Images.Count;

        foreach (var imgEl in imagesArray.EnumerateArray())
        {
            var url = imgEl.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null;
            if (string.IsNullOrEmpty(url)) continue;

            // Skip if we already have this URL
            if (existingUrls.Contains(url)) continue;

            var image = new ModelImage
            {
                ModelVersionId = dbVersion.Id,
                Url = url,
                SortOrder = sortOrder++,
            };

            if (imgEl.TryGetProperty("width", out var w) && w.TryGetInt32(out var width)) image.Width = width;
            if (imgEl.TryGetProperty("height", out var h) && h.TryGetInt32(out var height)) image.Height = height;
            if (imgEl.TryGetProperty("hash", out var hash) && hash.GetString() is { } hashStr) image.BlurHash = hashStr;
            if (imgEl.TryGetProperty("type", out var type) && type.GetString() is { } typeStr) image.MediaType = typeStr;

            if (imgEl.TryGetProperty("nsfwLevel", out var nsfwLvl) && nsfwLvl.TryGetInt32(out var nsfwLevel))
            {
                image.IsNsfw = nsfwLevel > 1;
            }

            dbVersion.Images.Add(image);
            existingUrls.Add(url);
            dirty = true;
        }

        return dirty;
    }

    /// <summary>
    /// Applies file hashes from a JSON "files" array to the matching <see cref="ModelFile"/> entity.
    /// Matches by filename comparison with the local file.
    /// </summary>
    private static bool ApplyFileHashesFromJson(
        ModelVersion dbVersion,
        JsonElement filesArray,
        string localPath)
    {
        var dirty = false;
        var localFileName = Path.GetFileName(localPath);

        foreach (var fileEl in filesArray.EnumerateArray())
        {
            var fileName = fileEl.TryGetProperty("name", out var fName) ? fName.GetString() : null;
            if (!string.Equals(fileName, localFileName, StringComparison.OrdinalIgnoreCase)) continue;

            var dbFile = dbVersion.Files.FirstOrDefault(f =>
                string.Equals(f.FileName, localFileName, StringComparison.OrdinalIgnoreCase))
                ?? dbVersion.PrimaryFile;

            if (dbFile is null) break;

            // File CivitaiId
            if (fileEl.TryGetProperty("id", out var fId) && fId.TryGetInt32(out var fileId))
            {
                dbFile.CivitaiId = fileId;
                dirty = true;
            }

            // File size
            if (fileEl.TryGetProperty("sizeKB", out var sizeKb) && sizeKb.TryGetDouble(out var sizeVal))
            {
                dbFile.SizeKB = sizeVal;
                dirty = true;
            }

            // Hashes
            if (fileEl.TryGetProperty("hashes", out var hashes))
            {
                if (hashes.TryGetProperty("SHA256", out var sha256) && sha256.GetString() is { } sha256Str)
                { dbFile.HashSHA256 ??= sha256Str; dirty = true; }

                if (hashes.TryGetProperty("AutoV2", out var autoV2) && autoV2.GetString() is { } autoV2Str)
                { dbFile.HashAutoV2 ??= autoV2Str; dirty = true; }

                if (hashes.TryGetProperty("CRC32", out var crc32) && crc32.GetString() is { } crc32Str)
                { dbFile.HashCRC32 ??= crc32Str; dirty = true; }

                if (hashes.TryGetProperty("BLAKE3", out var blake3) && blake3.GetString() is { } blake3Str)
                { dbFile.HashBLAKE3 ??= blake3Str; dirty = true; }

                if (hashes.TryGetProperty("AutoV1", out var autoV1) && autoV1.GetString() is { } autoV1Str)
                { dbFile.HashAutoV1 ??= autoV1Str; dirty = true; }
            }

            // Download URL
            if (fileEl.TryGetProperty("downloadUrl", out var dlUrl) && dlUrl.GetString() is { } dlUrlStr)
            {
                dbFile.DownloadUrl ??= dlUrlStr;
                dirty = true;
            }

            break; // Only match one file entry per local file
        }

        return dirty;
    }

    /// <summary>
    /// Image extensions to search for when looking for local preview images alongside model files.
    /// Ordered by preference (preview-specific suffixes first, then common formats).
    /// </summary>
    private static readonly string[] LocalPreviewExtensions =
    [
        ".preview.png", ".preview.jpg", ".preview.jpeg", ".preview.webp",
        ".thumb.jpg",
        ".png", ".jpg", ".jpeg", ".webp"
    ];

    /// <summary>
    /// Searches for a local preview image next to the model file (same base name, image extension)
    /// and stores it as a thumbnail BLOB on the primary image entity.
    /// Never overwrites an existing thumbnail (spec S4).
    /// </summary>
    /// <returns>True if a local thumbnail was found and applied.</returns>
    private async Task<bool> TryApplyLocalThumbnailAsync(
        IUnitOfWork uow,
        int modelId,
        string modelFilePath,
        DirectoryInfo directory,
        string baseName,
        CancellationToken ct)
    {
        try
        {
            // Search for local preview images by base name + known image extensions
            string? localImagePath = null;
            foreach (var ext in LocalPreviewExtensions)
            {
                var candidate = Path.Combine(directory.FullName, baseName + ext);
                if (File.Exists(candidate))
                {
                    localImagePath = candidate;
                    break;
                }
            }

            if (localImagePath is null) return false;

            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"Found local preview for '{Path.GetFileName(modelFilePath)}': {Path.GetFileName(localImagePath)}");

            // Read and transcode the local image to JPEG for BLOB storage
            var imageBytes = await File.ReadAllBytesAsync(localImagePath, ct);
            if (imageBytes.Length == 0) return false;

            byte[] thumbnailBytes;
            using (var skBitmap = SKBitmap.Decode(imageBytes))
            {
                if (skBitmap is null) return false;

                // Resize to thumbnail width (340px) to keep BLOB small
                var targetWidth = 340;
                var scale = (float)targetWidth / skBitmap.Width;
                var targetHeight = (int)(skBitmap.Height * scale);
                using var resized = skBitmap.Resize(new SKImageInfo(targetWidth, targetHeight), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                if (resized is null) return false;

                using var skImage = SKImage.FromBitmap(resized);
                using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, 85);
                thumbnailBytes = encoded.ToArray();
            }

            if (thumbnailBytes.Length == 0) return false;

            // Store in DB on the primary image (create one if needed)
            if (modelId == 0) return false;

            var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
            var dbVersion = dbModel?.Versions.FirstOrDefault(v =>
                v.Files.Any(f => string.Equals(f.LocalPath, modelFilePath, StringComparison.OrdinalIgnoreCase)));

            if (dbVersion is null) return false;

            // Spec S4 — never overwrite: a version that already carries a thumbnail keeps it.
            if (dbVersion.Images.Any(i => i.ThumbnailData is { Length: > 0 })) return false;

            var primaryImage = dbVersion.Images.FirstOrDefault();
            if (primaryImage is null)
            {
                // Create a new image entity for the local thumbnail
                primaryImage = new ModelImage
                {
                    ModelVersionId = dbVersion.Id,
                    Url = $"file://{localImagePath}",
                    SortOrder = 0,
                };
                dbVersion.Images.Add(primaryImage);
            }

            primaryImage.ThumbnailData = thumbnailBytes;
            primaryImage.ThumbnailMimeType = "image/jpeg";

            await uow.SaveChangesAsync(ct);

            return true;
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"Failed to apply local thumbnail for '{Path.GetFileName(modelFilePath)}': {ex.Message}");
            return false;
        }
    }
}
