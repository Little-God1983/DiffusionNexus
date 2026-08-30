using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>One LoRA the sorter may act on — decoupled from the DB graph so the planner is pure.</summary>
public sealed record SortCandidate(
    string FilePath,
    string? BaseModelRaw,
    string CategoryFolderName,   // already resolved via SorterCategoryResolver.ToFolderName
    int? CivitaiVersionId,
    string? Sha256,              // stored DB hash when known; null → hash lazily on collision
    long FileSizeBytes,
    IReadOnlyList<string> SidecarPaths,

    // What the file NAME suggests, when nothing authoritative and no safetensors header could
    // answer. Kept OUT of BaseModelRaw on purpose: the planner turns that value into a physical
    // move, and a name is a guess about a file rather than a reading of it. The sorter's
    // "sort by name" option is what folds this in, and it can be toggled without re-resolving
    // because the guess travels with the candidate instead of being baked into it.
    string? NameGuess = null,

    // What this file actually is. Never decides where it goes — it drives the preview's per-folder
    // labels, so a base-model folder about to receive a VAE says so before anything moves.
    ModelType AssetKind = ModelType.LORA,

    // True once "sort by name" has folded NameGuess into BaseModelRaw. Comparing the two strings
    // would answer the same question most of the time and silently wrongly the rest: a header and a
    // name can agree, and then a read file would be reported as a guessed one. The preview needs
    // the distinction to mark guessed rows apart from confirmed ones.
    bool BaseModelIsGuess = false);

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
