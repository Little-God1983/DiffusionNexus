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
