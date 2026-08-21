namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>One LoRA the sorter may act on — decoupled from the DB graph so the planner is pure.</summary>
public sealed record SortCandidate(
    string FilePath,
    string? BaseModelRaw,
    string CategoryFolderName,   // already resolved via SorterCategoryResolver.ToFolderName
    int? CivitaiVersionId,
    string? Sha256,              // stored DB hash when known; null → hash lazily on collision
    long FileSizeBytes,
    IReadOnlyList<string> SidecarPaths);

public enum PlannedAction { Transfer, AlreadyInPlace, SkippedDuplicate }

public sealed record PlannedMove(
    SortCandidate Candidate,
    string TargetDirectory,
    string TargetFilePath,       // includes any collision rename
    PlannedAction Action,
    bool WasRenamed);

public sealed record LoraSortPlan(
    IReadOnlyList<PlannedMove> Moves,
    string SourceRoot,
    string TargetRoot,
    bool IsMove,
    // Snapshotted from the options alongside IsMove, so the post-run "delete empty source folders"
    // step is decided by the plan that actually ran rather than by live UI state. The busy overlay
    // blocking the toggles was the only thing keeping that latent.
    bool DeleteEmptySourceFolders,
    long RequiredBytes,          // per spec §6: copy = all planned bytes; move = cross-volume bytes only
    int TransferCount,
    int AlreadyInPlaceCount,
    int RenamedCount,
    int SkippedDuplicateCount);

/// <summary>Options captured from the UI.</summary>
public sealed record LoraSortOptions(
    string SourceRoot,
    string TargetRoot,
    bool IncludeCategory,
    bool IsMove,
    bool DeleteEmptySourceFolders);
