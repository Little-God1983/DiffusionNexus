using System.Text.Json;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Sync.Thumbnails;

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
/// <paramref name="Applied"/> means <i>metadata was applied</i> — not merely that a sidecar file
/// existed: a <c>.json</c> holding a training config, or one that could not be parsed at all, is
/// <c>false</c>. It covers the sidecar metadata only; a local preview image can still have been
/// stored (<paramref name="ThumbnailApplied"/>), including when there is no sidecar.
/// <paramref name="Signature"/> is the file's current signature whenever a sidecar exists — also
/// when nothing was applied — so an unreadable file is not re-read until it changes.
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
/// Moved out of <c>LoraViewerViewModel.TryApplyLocalMetadataFallbackAsync</c> (#521 WP2) so the
/// sync pipeline — not a ViewModel — owns the sidecar → entity mapping. The caller owns the unit
/// of work (and therefore the DbContext scope): this class only mutates the graph and saves.
/// All three formats honor <see cref="Model.IsUserEdited"/> / <see cref="ModelVersion.IsUserEdited"/>
/// via <c>CanWriteModelText</c> / <c>CanWriteVersionText</c>, so a user's own metadata is never
/// overwritten by a file someone else wrote.
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

            // Two exact probes instead of two pattern enumerations: the identify step calls this
            // once per candidate while *planning*, and a source folder holding thousands of LoRAs
            // (plus their sidecars and previews) made those enumerations the dominant cost of
            // producing a plan — 16 s on the live library (#521 WP2 acceptance, R3). An exact probe
            // is also stricter: GetFiles matches Windows 8.3 short names, an exact path does not.
            //
            // Prefer .civitai.info (richer version-level data) over .json.
            var candidate = Path.Combine(directory.FullName, baseName + ".civitai.info");
            if (!File.Exists(candidate)) candidate = Path.Combine(directory.FullName, baseName + ".json");
            if (!File.Exists(candidate)) return none;

            var sidecar = new FileInfo(candidate);
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
        // Looked up first, before anything that can fail: the signature a failure reports has to be
        // the one the file actually has. Every failure path used to report "", the caller stored
        // that, and the next plan then computed the real signature, saw a difference, and re-hashed
        // and re-queried the same model — on every run, for as long as the bad file sat there.
        // A malformed sidecar is now left alone until it changes (or the 30-day window comes round).
        // Find never throws; a missing directory is not a failure, it is simply no sidecar.
        var lookup = Find(modelFilePath);
        var notApplied = new SidecarApplyResult(false, lookup.SidecarPath, lookup.Signature, false);

        try
        {
            var fileInfo = new FileInfo(modelFilePath);
            var directory = fileInfo.Directory;
            if (directory is null || !directory.Exists) return notApplied;

            if (lookup.SidecarPath is null)
            {
                // No sidecar metadata — still try local thumbnail
                var thumbnailOnly = await TryApplyLocalThumbnailAsync(uow, modelId, modelFilePath, ct);
                return notApplied with { ThumbnailApplied = thumbnailOnly };
            }

            var isCivitaiInfoFormat = lookup.SidecarPath.EndsWith(
                ".civitai.info", StringComparison.OrdinalIgnoreCase);

            var json = await File.ReadAllTextAsync(lookup.SidecarPath, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Load ONLY the target model — not the entire database
            var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
            if (dbModel is null)
            {
                _logger?.Debug(LogCategory.General, LogSource,
                    $"No model {modelId} to apply '{Path.GetFileName(lookup.SidecarPath)}' to — it was deleted during the run");
                return notApplied;
            }

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

            // Only a sidecar something was actually read from makes this model locally sourced. A
            // .json next to a LoRA is as often a kohya training config as it is metadata, and
            // stamping Source/LastSyncedAt unconditionally claimed a file nothing was read from as
            // the origin of the model's data — and told the caller "applied", which the step turns
            // into a Sidecar outcome.
            if (dirty)
            {
                dbModel.Source = DataSource.LocalFile;
                dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
                await uow.SaveChangesAsync(ct);
            }

            // Try to find and apply a local thumbnail image
            var thumbnailApplied = await TryApplyLocalThumbnailAsync(uow, modelId, modelFilePath, ct);

            return new SidecarApplyResult(dirty, lookup.SidecarPath, lookup.Signature, thumbnailApplied);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user cancelled the sync — not a failure worth a warning row.
            return notApplied;
        }
        catch (Exception ex)
        {
            // One line, naming the sidecar that could not be read — this is the only place the
            // failure is reported, because the result itself is indistinguishable from "no metadata
            // in that file" by design (see notApplied above).
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"{Path.GetFileName(modelFilePath)}: sidecar " +
                $"'{Path.GetFileName(lookup.SidecarPath) ?? "(none)"}' could not be applied: {ex.Message}");
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
            if (CanWriteModelText(dbModel)
                && modelProp.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
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
            if (CanWriteVersionText(dbVersion)
                && root.TryGetProperty("name", out var vName) && vName.GetString() is { } vNameStr)
            {
                dbVersion.Name = vNameStr;
                dirty = true;
            }

            // Base model — authored, so it hangs off the version guard like the name below it.
            if (CanWriteVersionText(dbVersion)
                && root.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
            {
                dirty |= WriteBaseModel(dbVersion, baseModelStr);
            }

            // Trained words / trigger words
            if (CanWriteVersionText(dbVersion)
                && root.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == JsonValueKind.Array)
            {
                dirty |= ReplaceTriggerWords(dbVersion, trained);
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
        if (CanWriteModelText(dbModel)
            && root.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
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

                if (CanWriteVersionText(dbVersion)
                    && versionElement.TryGetProperty("name", out var vName) && vName.GetString() is { } vNameStr)
                {
                    dbVersion.Name = vNameStr;
                    dirty = true;
                }

                if (CanWriteVersionText(dbVersion)
                    && versionElement.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
                {
                    dirty |= WriteBaseModel(dbVersion, baseModelStr);
                }

                if (CanWriteVersionText(dbVersion)
                    && versionElement.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == JsonValueKind.Array)
                {
                    dirty |= ReplaceTriggerWords(dbVersion, trained);
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
            if (CanWriteModelText(dbModel)
                && modelProp.TryGetProperty("name", out var name) && name.GetString() is { } nameStr)
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
            if (CanWriteVersionText(dbVersion)
                && root.TryGetProperty("baseModel", out var baseModel) && baseModel.GetString() is { } baseModelStr)
            {
                dirty |= WriteBaseModel(dbVersion, baseModelStr);
            }

            // Fallback: "sd version" for very old sidecar format
            if (CanWriteVersionText(dbVersion)
                && dbVersion.BaseModelRaw is null or "???"
                && root.TryGetProperty("sd version", out var sdVer) && sdVer.GetString() is { } sdVerStr)
            {
                dirty |= WriteBaseModel(dbVersion, sdVerStr);
            }

            if (CanWriteVersionText(dbVersion)
                && root.TryGetProperty("trainedWords", out var trained) && trained.ValueKind == JsonValueKind.Array)
            {
                dirty |= ReplaceTriggerWords(dbVersion, trained);
            }
        }

        return dirty;
    }

    /// <summary>
    /// Whether the model's own text — its name — may be written from a sidecar. A model the user
    /// has edited answers no: <see cref="Model.IsUserEdited"/> is sticky, and a file someone else
    /// wrote is not more authoritative than what the user typed. The single place the rule lives,
    /// referenced from all three sidecar formats.
    /// </summary>
    private static bool CanWriteModelText(Model dbModel) => !dbModel.IsUserEdited;

    /// <summary>
    /// The same rule for a version's authored answers — its name, trigger words and base model.
    /// Version edits are tracked separately from the model's, so a user who renamed one version
    /// keeps the rest of the model syncing normally.
    /// </summary>
    private static bool CanWriteVersionText(ModelVersion dbVersion) => !dbVersion.IsUserEdited;

    /// <summary>
    /// Writes both spellings of the base model — the raw upstream string and the
    /// <see cref="BaseModelType"/> the viewer's filter reads — using the same parser the detail
    /// view's editor uses. Writing only the raw one left the enum reporting the previous base
    /// model forever. Callers must check <see cref="CanWriteVersionText"/> first.
    /// </summary>
    /// <returns>Whether anything was written.</returns>
    /// <remarks>
    /// A blank <c>baseModel</c> is a missing answer, not an instruction to forget the stored one
    /// (F6). The call sites only rejected an <i>absent</i> key, so a sidecar carrying
    /// <c>"baseModel": ""</c> blanked the raw string and set the enum to <c>Unknown</c> on a version
    /// nobody had edited. The check lives here so all four of them inherit it.
    /// </remarks>
    private static bool WriteBaseModel(ModelVersion dbVersion, string? baseModelRaw)
    {
        if (string.IsNullOrWhiteSpace(baseModelRaw)) return false;

        dbVersion.BaseModelRaw = baseModelRaw;
        dbVersion.BaseModel = BaseModelTypeExtensions.ParseCivitai(baseModelRaw);
        return true;
    }

    /// <summary>
    /// Replaces a version's trigger words with the sidecar's <c>trainedWords</c>, returning whether
    /// anything was written. Callers must check <see cref="CanWriteVersionText"/> first.
    /// </summary>
    /// <remarks>
    /// An array holding no usable strings is not an answer worth clearing a version's trigger words
    /// over — and clearing without reporting it would leave an unsaved mutation on a tracked entity
    /// that the thumbnail save later commits behind the caller's back.
    /// </remarks>
    private static bool ReplaceTriggerWords(ModelVersion dbVersion, JsonElement trainedWords)
    {
        var words = trainedWords.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();

        if (words.Count == 0) return false;

        dbVersion.TriggerWords.Clear();
        var order = 0;
        foreach (var word in words)
        {
            dbVersion.TriggerWords.Add(new TriggerWord { Word = word, Order = order++ });
        }

        return true;
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
                // SHA256 is uppercased on the way in (sidecars store it lowercase): a stored
                // lowercase digest breaks SQL equality against every hash the app writes itself,
                // which is the invariant the startup repair pass exists to restore. The other
                // columns have no such invariant and are stored as found.
                if (hashes.TryGetProperty("SHA256", out var sha256) && sha256.GetString() is { } sha256Str)
                { dbFile.HashSHA256 ??= FileHasher.NormalizeSha256(sha256Str); dirty = true; }

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
    /// Searches for a local preview image next to the model file (same base name, image extension)
    /// and stores it as a thumbnail BLOB on the primary image entity.
    /// Never overwrites an existing thumbnail (spec S4).
    /// </summary>
    /// <remarks>
    /// The applier owns none of the byte-work any more: <see cref="LocalPreviewFiles.FindSibling"/>
    /// is the extension ladder, <see cref="ThumbnailCodec"/> is what a thumbnail looks like, and
    /// <see cref="ThumbnailWriter"/> is which of the six columns a verdict touches. Two consequences
    /// of that, both intended: a preview found here is now the same 450px JPEG the sync step and the
    /// tile produce (it used to be its own 340px transcode), and the attempt is stamped, so a sibling
    /// that cannot be decoded is read and decoded once rather than on every run for the life of the
    /// file. What stays local is the decision-making — spec S4, the synthetic row, and the contract
    /// that nothing in here throws.
    /// </remarks>
    /// <returns>True if a local thumbnail was found and applied.</returns>
    private async Task<bool> TryApplyLocalThumbnailAsync(
        IUnitOfWork uow,
        int modelId,
        string modelFilePath,
        CancellationToken ct)
    {
        try
        {
            var localImagePath = LocalPreviewFiles.FindSibling(modelFilePath);
            if (localImagePath is null) return false;

            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"Found local preview for '{Path.GetFileName(modelFilePath)}': {Path.GetFileName(localImagePath)}");

            // Resolve the target version BEFORE the transcode: a version that already carries a
            // thumbnail keeps it (spec S4), and decoding a full-size preview only to throw the
            // result away is the expensive part of this method.
            if (modelId == 0) return false;

            var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
            var dbVersion = dbModel?.Versions.FirstOrDefault(v =>
                v.Files.Any(f => string.Equals(f.LocalPath, modelFilePath, StringComparison.OrdinalIgnoreCase)));

            if (dbVersion is null) return false;

            // Spec S4 — never overwrite: a version that already carries a thumbnail keeps it.
            if (dbVersion.Images.Any(i => i.ThumbnailData is { Length: > 0 })) return false;

            var imageBytes = await File.ReadAllBytesAsync(localImagePath, ct);
            var payload = imageBytes.Length == 0 ? null : ThumbnailCodec.Encode(imageBytes);
            var now = DateTimeOffset.UtcNow;

            var primaryImage = dbVersion.Images.FirstOrDefault();

            if (payload is null)
            {
                // The sibling is there and unusable. Worth recording — but only on a row that
                // already exists. Inventing a file:// row purely to carry a failure would make the
                // version's primary image a file nothing can read, and the sync step already
                // records LocalFileMissing for the file:// rows it selects.
                if (primaryImage is null) return false;

                ThumbnailWriter.ApplyFailure(primaryImage, ThumbnailFailureReason.NotDecodable, now);
                await uow.SaveChangesAsync(ct);
                return false;
            }

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

            ThumbnailWriter.ApplySuccess(primaryImage, payload, now);

            await uow.SaveChangesAsync(ct);

            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user cancelled the sync — not a failure worth logging.
            return false;
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.FileSystem, LogSource,
                $"Failed to apply local thumbnail for '{Path.GetFileName(modelFilePath)}': {ex.Message}");
            return false;
        }
    }
}
