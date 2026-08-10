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
