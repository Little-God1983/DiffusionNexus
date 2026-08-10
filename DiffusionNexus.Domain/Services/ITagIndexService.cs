namespace DiffusionNexus.Domain.Services;

public enum NsfwFilterMode { ShowAll, HideNsfw, NsfwOnly }

public sealed record TagFrequency(string Name, int Count);

/// <summary>
/// Progress for a running index build.
/// </summary>
/// <param name="Completed">Files finished so far.</param>
/// <param name="Total">Files eligible for tagging in this run.</param>
/// <param name="CurrentFile">
/// The file currently being tagged — an actual path, or <see langword="null"/>
/// when no single file is in flight (the terminal report). Never a status
/// phrase: overloading this field with human-readable text is how the UI ended
/// up showing "Indexing images… 0/N" for the whole model download.
/// </param>
/// <param name="StatusMessage">
/// Phase-level text for the UI to display verbatim (e.g. "Downloading tagger
/// model…"), or <see langword="null"/> during ordinary per-file progress.
/// </param>
public sealed record TagIndexBuildProgress(
    int Completed,
    int Total,
    string? CurrentFile,
    string? StatusMessage = null);

public sealed record TagIndexBuildResult(int Indexed, int Skipped, int Failed, int NsfwCount);

public sealed record ImageTagLookup(bool IsNsfw, IReadOnlyList<string> Tags);

/// <summary>
/// Builds and queries the searchable local tag index for the Generation
/// Gallery. Indexing is incremental: a file whose size and last-write time
/// match its existing row is skipped without re-running the classifier.
/// This service does not enumerate gallery folders itself — callers supply
/// the file list (the gallery ViewModel already owns folder scanning).
/// </summary>
public interface ITagIndexService
{
    /// <summary>
    /// Tags and indexes the supplied files. Callers do not need to pre-filter
    /// or de-duplicate the list — this method does both itself:
    /// <list type="bullet">
    /// <item><description>Non-image paths (videos, sidecars, anything outside
    /// <see cref="DiffusionNexus.Domain.Enums.SupportedMediaTypes.ImageExtensions"/>) are dropped
    /// silently. They are not counted as
    /// <see cref="TagIndexBuildResult.Failed"/> — they were never eligible for
    /// tagging — and they are not counted in
    /// <see cref="TagIndexBuildResult.Skipped"/> either, which is reserved for
    /// images that were genuinely skipped (missing, or unchanged since their
    /// last index).</description></item>
    /// <item><description>Paths are normalized with
    /// <see cref="Path.GetFullPath(string)"/> and de-duplicated
    /// case-insensitively, so overlapping or nested gallery folders are
    /// safe to pass in.</description></item>
    /// </list>
    /// If nothing taggable remains after filtering, this returns an all-zero
    /// result immediately — notably without triggering the tagger model
    /// download.
    /// </summary>
    /// <remarks>
    /// Per-file failures are reported through
    /// <see cref="TagIndexBuildResult.Failed"/> rather than thrown, and leave
    /// no partial row behind, so a failed file stays eligible for the next
    /// run. Cancellation is the one thing that does propagate, as an
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    Task<TagIndexBuildResult> BuildIndexAsync(
        IReadOnlyList<string> filePaths,
        IProgress<TagIndexBuildProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Distinct tags with counts, most frequent first — powers the clickable tag cloud.</summary>
    Task<IReadOnlyList<TagFrequency>> GetTagCloudAsync(int maxTags = 200, CancellationToken cancellationToken = default);

    /// <summary>AND search: returns file paths carrying every tag in <paramref name="requiredTags"/>.</summary>
    Task<IReadOnlyList<string>> SearchAsync(
        IReadOnlyList<string> requiredTags,
        NsfwFilterMode nsfwFilter,
        CancellationToken cancellationToken = default);

    Task<int> GetIndexedCountAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts how many of the supplied paths have an index row. Unlike the
    /// parameterless overload this is scoped to the caller's file set, so a
    /// "N / M indexed" display stays honest when index rows exist for gallery
    /// folders that are currently disabled.
    /// </summary>
    Task<int> GetIndexedCountAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk lookup for gallery tile hydration: NSFW flag + tag names for
    /// every already-indexed path in <paramref name="filePaths"/>. Paths with
    /// no index row (never indexed, or since deleted) are simply absent from
    /// the result — callers should treat a missing key as "not yet tagged."
    /// </summary>
    Task<IReadOnlyDictionary<string, ImageTagLookup>> GetTagsForFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops the index rows for files that have left the gallery (deleted, or
    /// moved somewhere else). Nothing else prunes the index, so without this
    /// the "N / M indexed" counter and the tag cloud drift permanently stale
    /// after the first delete.
    /// </summary>
    /// <remarks>
    /// Paths are normalized the same way <see cref="BuildIndexAsync"/>
    /// normalizes them, and a path with no row is a no-op rather than an
    /// error — callers are expected to fire this for every removed item
    /// without first checking whether it was ever indexed.
    /// </remarks>
    /// <returns>
    /// How many index rows were actually deleted, so callers can adjust an
    /// indexed-count display without re-querying.
    /// </returns>
    Task<int> RemoveIndexEntriesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default);
}
