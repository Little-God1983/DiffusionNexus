using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Services.Sync;

// Candidate projections returned by ISyncStateRepository (Task 5). Kept in Domain so Service never sees EF.

/// <summary>A model file eligible for the identify step, with its current sync state.</summary>
public sealed record IdentifyCandidate(int ModelId, int VersionId, int FileId, string Name, string LocalPath, string? Sha256,
    string? BaseModelRaw, SyncOutcome Outcome, DateTimeOffset? CheckedAt, int Attempts, string? SidecarSignature);

/// <summary>A model eligible for the tag-fetch step.</summary>
public sealed record TagCandidate(int ModelId, int CivitaiModelId, string Name, DateTimeOffset? TagsCheckedAt);

/// <summary>A model version eligible for the image-fetch step.</summary>
public sealed record ImageCandidate(int ModelId, int VersionId, int CivitaiVersionId, string Name, DateTimeOffset? ImagesCheckedAt);

/// <summary>
/// Everything <see cref="SyncStateDeriver"/> needs about one legacy model, and nothing else.
/// </summary>
/// <remarks>
/// The backfill used to load each model's whole graph (<c>GetByIdWithIncludesAsync</c>: five split
/// queries, and the images carry their thumbnail BLOBs) purely to answer four questions about it —
/// 200 models per batch, so a first run after the upgrade read hundreds of megabytes of JPEG to
/// decide a handful of timestamps. This record is what those questions actually need, filled by one
/// projected query per batch.
/// </remarks>
/// <param name="ModelId">The model the state row belongs to.</param>
/// <param name="CivitaiId">Non-null when the model was matched on Civitai at some point.</param>
/// <param name="LastSyncedAt">The legacy "we looked at this" stamp, or null if nothing ever did.</param>
/// <param name="Source">Where the model's metadata came from.</param>
/// <param name="HasTags">Whether the model carries at least one tag.</param>
/// <param name="HasImages">Whether any version of it carries at least one image record.</param>
/// <param name="HasRealBaseModel">
/// Whether any version carries a base model that says something — i.e. not blank and not the legacy
/// "???" placeholder. A boolean rather than the string itself because that is the whole question,
/// and because "any version" is the rule: a model whose second version was identified by a sidecar
/// is a sidecar model.
/// </param>
public sealed record SyncDerivationInput(
    int ModelId,
    int? CivitaiId,
    DateTimeOffset? LastSyncedAt,
    DataSource Source,
    bool HasTags,
    bool HasImages,
    bool HasRealBaseModel);
