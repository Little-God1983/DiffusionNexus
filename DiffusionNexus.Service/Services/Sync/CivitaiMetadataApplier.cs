using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// Writes a Civitai API response into the local DB graph of a model.
/// Moved verbatim out of <c>LoraViewerViewModel.UpdateModelFromCivitaiAsync</c> (#521 WP2)
/// so the sync pipeline — not a ViewModel — owns the response → entity mapping.
/// </summary>
/// <remarks>
/// The caller owns the unit of work (and therefore the DbContext scope): this class only
/// mutates the graph and saves. All entry points honor <see cref="Model.IsUserEdited"/> /
/// <see cref="ModelVersion.IsUserEdited"/> so a user's own metadata is never overwritten.
/// </remarks>
public sealed class CivitaiMetadataApplier
{
    private const string LogSource = "LibrarySync";

    private readonly ICivitaiClient _client;
    private readonly IUnifiedLogger? _logger;

    public CivitaiMetadataApplier(ICivitaiClient client, IUnifiedLogger? logger = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger;
    }

    /// <summary>
    /// Fetches the full model (for tags/images/versions) and writes the Civitai response into
    /// the DB graph of <paramref name="modelId"/>. Honors <c>IsUserEdited</c> on model and
    /// version. Saves. Returns <c>false</c> when the model row no longer exists.
    /// </summary>
    /// <param name="uow">Unit of work owning the DbContext the graph is loaded into.</param>
    /// <param name="modelId">Local model id to write into.</param>
    /// <param name="fileId">Local <see cref="ModelFile"/> id identifying which version to update.</param>
    /// <param name="version">The version as returned by the hash lookup (fallback source).</param>
    /// <param name="apiKey">Optional Civitai API key.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> ApplyAsync(
        IUnitOfWork uow,
        int modelId,
        int fileId,
        CivitaiModelVersion version,
        string? apiKey,
        CancellationToken ct = default)
    {
        // Fetch the full model to get all versions (richer data including images)
        CivitaiModel? civitaiModel = null;
        if (version.ModelId > 0)
        {
            civitaiModel = await _client.GetModelAsync(version.ModelId, apiKey, ct);
        }

        // Resolve the best image source: the full model response is more reliable than
        // the hash-lookup response which often returns an empty images array.
        var bestCivitaiVersion = civitaiModel?.ModelVersions
            .FirstOrDefault(v => v.Id == version.Id) ?? version;

        // Load ONLY the target model — not the entire database (was the #1 cause of OOM)
        var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
        if (dbModel is null) return false;

        // Update model-level fields from Civitai
        // Skip overwriting user-edited fields (description, tags, etc.) — IsUserEdited is sticky.
        var preserveModelEdits = dbModel.IsUserEdited;
        if (civitaiModel is not null)
        {
            // Always set the grouping key — not unique, safe for all models sharing the same Civitai page
            dbModel.CivitaiModelPageId = civitaiModel.Id;

            // Only assign CivitaiId if no other model already owns it (prevents UNIQUE constraint violation)
            var civitaiIdTaken = await uow.Models.IsCivitaiIdTakenAsync(civitaiModel.Id, dbModel.Id, ct);
            if (!civitaiIdTaken)
            {
                dbModel.CivitaiId = civitaiModel.Id;
            }
            else
            {
                _logger?.Warn(LogCategory.Network, LogSource,
                    $"Skipping CivitaiId {civitaiModel.Id} for model '{dbModel.Name}' (Id={dbModel.Id}): " +
                    "already assigned to another model");
            }

            if (!preserveModelEdits)
            {
                dbModel.Name = civitaiModel.Name;
                dbModel.Description = civitaiModel.Description;
            }
            dbModel.IsNsfw = civitaiModel.Nsfw;
            dbModel.IsPoi = civitaiModel.Poi;
            dbModel.Source = DataSource.CivitaiApi;
            dbModel.LastSyncedAt = DateTimeOffset.UtcNow;
            // Capture version-availability data from the same response so the
            // "+N more versions" badge can be shown without an additional HTTP call.
            dbModel.TotalVersionCount = civitaiModel.ModelVersions?.Count ?? 0;
            dbModel.LastCheckedForUpdatesUtc = DateTime.UtcNow;
            dbModel.AllowNoCredit = civitaiModel.AllowNoCredit;
            dbModel.AllowDerivatives = civitaiModel.AllowDerivatives;
            dbModel.AllowDifferentLicense = civitaiModel.AllowDifferentLicense;

            // Update or create creator — reuse existing Creator entity by
            // Username to avoid UNIQUE constraint violations.
            if (civitaiModel.Creator is not null)
            {
                if (dbModel.Creator is not null)
                {
                    dbModel.Creator.Username = civitaiModel.Creator.Username;
                    dbModel.Creator.AvatarUrl ??= civitaiModel.Creator.Image;
                }
                else
                {
                    var existingCreator = await uow.Models
                        .FindCreatorByUsernameAsync(civitaiModel.Creator.Username, ct);

                    dbModel.Creator = existingCreator ?? new Creator
                    {
                        Username = civitaiModel.Creator.Username,
                        AvatarUrl = civitaiModel.Creator.Image,
                    };
                }
            }
        }

        // Update version-level fields for the matched version
        var dbVersion = dbModel.Versions.FirstOrDefault(v =>
            v.Files.Any(f => f.Id == fileId));

        if (dbVersion is not null)
        {
            // Only assign CivitaiId if no other version already owns it (prevents UNIQUE constraint violation)
            var versionIdTaken = await uow.Models
                .IsVersionCivitaiIdTakenAsync(bestCivitaiVersion.Id, dbVersion.Id, ct);
            if (!versionIdTaken)
            {
                dbVersion.CivitaiId = bestCivitaiVersion.Id;
            }
            else
            {
                _logger?.Warn(LogCategory.Network, LogSource,
                    $"Skipping CivitaiId {bestCivitaiVersion.Id} for version '{dbVersion.Name}' (Id={dbVersion.Id}): " +
                    "already assigned to another version");
            }

            dbVersion.Name = bestCivitaiVersion.Name;
            dbVersion.Description = bestCivitaiVersion.Description;
            dbVersion.BaseModelRaw = bestCivitaiVersion.BaseModel;
            dbVersion.DownloadUrl = bestCivitaiVersion.DownloadUrl;
            dbVersion.DownloadCount = bestCivitaiVersion.Stats?.DownloadCount ?? 0;
            dbVersion.PublishedAt = bestCivitaiVersion.PublishedAt;
            dbVersion.EarlyAccessDays = bestCivitaiVersion.EarlyAccessTimeFrame;

            // Update trigger words — skip when this version was user-edited
            if (!dbVersion.IsUserEdited)
            {
                dbVersion.TriggerWords.Clear();
                var order = 0;
                foreach (var word in bestCivitaiVersion.TrainedWords)
                {
                    dbVersion.TriggerWords.Add(new TriggerWord
                    {
                        Word = word,
                        Order = order++
                    });
                }
            }

            // Add images from Civitai (use the version with the richest data)
            AppendImages(dbVersion, bestCivitaiVersion.Images);

            // Update file hashes from Civitai data
            var civFile = bestCivitaiVersion.Files.FirstOrDefault(f => f.Primary == true);
            if (civFile?.Hashes is not null)
            {
                var dbFile = dbVersion.PrimaryFile;
                if (dbFile is not null)
                {
                    dbFile.CivitaiId = civFile.Id;
                    dbFile.HashSHA256 ??= civFile.Hashes.SHA256;
                    dbFile.HashAutoV2 ??= civFile.Hashes.AutoV2;
                    dbFile.HashCRC32 ??= civFile.Hashes.CRC32;
                    dbFile.HashBLAKE3 ??= civFile.Hashes.BLAKE3;
                }
            }
        }

        // Sync tags from Civitai model response (skip when user has edited tags)
        if (!preserveModelEdits && civitaiModel?.Tags is { Count: > 0 } civitaiTags)
        {
            var tagLookup = await uow.Models.GetAllTagsLookupAsync(ct);
            SyncTags(dbModel, civitaiTags, tagLookup);
        }

        await uow.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Tags only (FetchTags step): fetch the model page and replace the model's tags unless
    /// the model is user-edited. Saves. Returns the number of tags on the model afterwards —
    /// <c>0</c> is a valid, final answer for a model Civitai has no tags for — or <c>null</c>
    /// when Civitai had no model to answer with at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>null</c> / <c>0</c> split is the whole point of the entry point: "the page is gone"
    /// and "the page lists no tags" are different facts, and the caller stamps them differently.
    /// An empty tag list from a page that *did* respond is authoritative, so tags removed upstream
    /// are removed here too — except on a user-edited model, whose tags are never ours to touch,
    /// and except on a degraded payload (<c>"tags": null</c>), which is not an answer at all.
    /// </para>
    /// <para>
    /// The authoritative-clear branch is currently unreachable from <c>FetchTagsStep</c>:
    /// <c>SelectTagCandidatesAsync</c> only offers models with no tags, and <c>ForceTags</c>
    /// bypasses the "already checked" policy, not that predicate. That is <i>intentional</i> — a
    /// forced re-sync must never be able to turn into a bulk tag-wipe driven by one degraded
    /// Civitai response. Re-pulling tags for models that already have some is separate design
    /// work, and would start at the repository query, not here.
    /// </para>
    /// </remarks>
    public async Task<int?> ApplyTagsAsync(
        IUnitOfWork uow,
        int modelId,
        int civitaiModelId,
        string? apiKey,
        CancellationToken ct = default)
    {
        var civitaiModel = await _client.GetModelAsync(civitaiModelId, apiKey, ct);

        // A 404 says nothing about tags: the model page itself is gone. Reporting that as
        // "zero tags" would let a dead page silently wipe the tags we already hold.
        if (civitaiModel is null) return null;

        var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
        if (dbModel is null) return 0;

        // Same guard as ApplyAsync: never overwrite a user's own tags.
        if (dbModel.IsUserEdited) return dbModel.Tags.Count;

        // Tags is annotated non-nullable and initialized to [], but System.Text.Json writes null
        // straight through for an explicit `"tags": null` — the client does not enable
        // RespectNullableAnnotations, and Civitai's response shapes have drifted under us twice
        // before. A missing list is a degraded payload, NOT an authoritative empty one: it must not
        // clear tags. It still counts as answered (non-null), so the caller stops re-asking.
        if (civitaiModel.Tags is null) return dbModel.Tags.Count;

        var tagLookup = await uow.Models.GetAllTagsLookupAsync(ct);
        SyncTags(dbModel, civitaiModel.Tags, tagLookup);
        await uow.SaveChangesAsync(ct);

        return dbModel.Tags.Count;
    }

    /// <summary>
    /// Images only (FetchImages step): fetch the version and append every image not yet
    /// present by <see cref="ModelImage.CivitaiId"/>. Saves. Returns the number added, or
    /// <c>null</c> when Civitai has no such version any more.
    /// </summary>
    public async Task<int?> ApplyImagesAsync(
        IUnitOfWork uow,
        int modelId,
        int versionId,
        int civitaiVersionId,
        string? apiKey,
        CancellationToken ct = default)
    {
        var civitaiVersion = await _client.GetModelVersionAsync(civitaiVersionId, apiKey, ct);

        // As above: a version that 404s is not a version with no images.
        if (civitaiVersion is null) return null;

        var dbModel = await uow.Models.GetByIdWithIncludesAsync(modelId, ct);
        var dbVersion = dbModel?.Versions.FirstOrDefault(v => v.Id == versionId);
        if (dbVersion is null) return 0;

        var added = AppendImages(dbVersion, civitaiVersion.Images);
        if (added > 0)
        {
            await uow.SaveChangesAsync(ct);
        }

        return added;
    }

    /// <summary>
    /// Appends the Civitai images that are not already present by <see cref="ModelImage.CivitaiId"/>,
    /// continuing the existing <see cref="ModelImage.SortOrder"/>. Returns how many were added.
    /// </summary>
    private static int AppendImages(ModelVersion dbVersion, IReadOnlyList<CivitaiModelImage> civitaiImages)
    {
        var existingImageIds = dbVersion.Images
            .Where(i => i.CivitaiId.HasValue)
            .Select(i => i.CivitaiId!.Value)
            .ToHashSet();
        var sortOrder = dbVersion.Images.Count;
        var added = 0;

        foreach (var civImage in civitaiImages)
        {
            if (string.IsNullOrEmpty(civImage.Url))
                continue;

            if (civImage.Id.HasValue && existingImageIds.Contains(civImage.Id.Value))
                continue;

            dbVersion.Images.Add(new ModelImage
            {
                ModelVersionId = dbVersion.Id,
                CivitaiId = civImage.Id,
                Url = civImage.Url,
                MediaType = civImage.Type,
                IsNsfw = civImage.Nsfw,
                Width = civImage.Width ?? 0,
                Height = civImage.Height ?? 0,
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
            added++;
        }

        return added;
    }

    /// <summary>
    /// Replaces a model's tags with the Civitai tag list, reusing existing <see cref="Tag"/>
    /// rows by <see cref="Tag.NormalizedName"/> to avoid duplicates in the Tags table.
    /// </summary>
    /// <remarks>
    /// Civitai tag lists are not normalized — "Anime" and "anime" both occur, on the same model —
    /// and two spellings that fold to one <see cref="Tag"/> would produce two <see cref="ModelTag"/>
    /// rows with the same composite key <c>(ModelId, TagId)</c>. That is not a cosmetic duplicate:
    /// the change tracker rejects the second one outright, so the model's whole tag list is lost and
    /// the item fails. One row per distinct normalized name, first spelling wins.
    /// </remarks>
    public static void SyncTags(
        Model dbModel,
        IReadOnlyList<string> civitaiTags,
        Dictionary<string, Tag> knownTags)
    {
        dbModel.Tags.Clear();

        var applied = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tagName in civitaiTags)
        {
            if (string.IsNullOrWhiteSpace(tagName)) continue;

            var normalized = tagName.Trim().ToLowerInvariant();
            if (!applied.Add(normalized)) continue;

            if (!knownTags.TryGetValue(normalized, out var tag))
            {
                tag = new Tag { Name = tagName, NormalizedName = normalized };
                knownTags[normalized] = tag;
            }

            dbModel.Tags.Add(new ModelTag { Tag = tag });
        }
    }
}
