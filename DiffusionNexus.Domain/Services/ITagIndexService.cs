namespace DiffusionNexus.Domain.Services;

public enum NsfwFilterMode { ShowAll, HideNsfw, NsfwOnly }

public sealed record TagFrequency(string Name, int Count);

public sealed record TagIndexBuildProgress(int Completed, int Total, string? CurrentFile);

public sealed record TagIndexBuildResult(int Indexed, int Skipped, int Failed, int NsfwCount);

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
}
