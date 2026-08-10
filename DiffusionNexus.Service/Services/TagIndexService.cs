using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Service.Services;

public sealed class TagIndexService : ITagIndexService
{
    /// <summary>
    /// How many files are staged into the change tracker before it is saved
    /// and cleared. Bounds two separate things: how much a crash partway
    /// through a big folder can lose, and how large the tracked graph gets.
    /// The latter matters more than it looks — every <c>SaveChangesAsync</c>
    /// re-runs <c>DetectChanges</c> over the entire tracked graph, so a
    /// tracker that is never cleared makes a full gallery build (thousands of
    /// images × tens of tags each) quadratic in file count.
    /// </summary>
    private const int FlushBatchSize = 25;

    private readonly IDbContextFactory<DiffusionNexusCoreDbContext> _contextFactory;
    private readonly IImageTaggingService _taggingService;
    private readonly IDownloadCoordinator? _downloadCoordinator;

    public TagIndexService(
        IDbContextFactory<DiffusionNexusCoreDbContext> contextFactory,
        IImageTaggingService taggingService,
        IDownloadCoordinator? downloadCoordinator = null)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _taggingService = taggingService ?? throw new ArgumentNullException(nameof(taggingService));
        _downloadCoordinator = downloadCoordinator;
    }

    public async Task<TagIndexBuildResult> BuildIndexAsync(
        IReadOnlyList<string> filePaths,
        IProgress<TagIndexBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);

        // Filter, normalize and de-duplicate up front — before the download
        // gate, before any DB work — because each step guards a different
        // real failure:
        //  * Non-images: the caller's folder enumeration includes videos
        //    (.mp4/.mov/.webm/…). Every one of them would reach Image.Load and
        //    throw, reporting a spurious failure for a file that was never
        //    eligible for tagging. They are dropped silently, not failed.
        //  * Duplicates: the "already indexed?" check below is a DB query, so
        //    it cannot see rows staged earlier in this same run. Two identical
        //    input paths — very reachable when a user configures nested or
        //    overlapping gallery folders — would stage two inserts for one
        //    unique FilePath and blow up the whole batch on save.
        //  * Normalization here also removes the per-iteration GetFullPath.
        var work = filePaths
            .Where(SupportedMediaTypes.IsImageFile)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var indexed = 0;
        var skipped = 0;
        var failed = 0;
        var nsfwCount = 0;
        var total = work.Count;

        // Nothing taggable was supplied. Return before the download gate:
        // otherwise an empty selection — or a folder that only holds videos —
        // would trigger a ~379 MB model download to index zero files.
        if (total == 0)
            return new TagIndexBuildResult(Indexed: 0, Skipped: 0, Failed: 0, NsfwCount: 0);

        // The model downloads on first use, not eagerly — this is that
        // trigger point. Routed through IDownloadCoordinator when available
        // so it's visible in the Unified Console/status bar exactly like
        // every other model download (see the download-unification plan).
        if (_taggingService.GetModelStatus() != ModelStatus.Ready)
        {
            // Reported as StatusMessage, not CurrentFile: CurrentFile is a
            // path, and a UI that only checks it for null showed
            // "Indexing images… 0/N" for the entire multi-minute download.
            progress?.Report(new TagIndexBuildProgress(0, total, null, "Downloading tagger model…"));

            bool downloaded;
            if (_downloadCoordinator is not null)
            {
                downloaded = await _downloadCoordinator.EnqueueAsync(
                    "WD14 image tagger model",
                    async (taskProgress, ct) =>
                    {
                        var fileProgress = new Progress<ModelDownloadProgress>(p =>
                        {
                            var percent = p.TotalBytes > 0
                                ? (int)((double)p.BytesDownloaded / p.TotalBytes * 100.0)
                                : 0;
                            taskProgress.Report(new DownloadTaskProgress(percent, p.Status));
                        });
                        return await _taggingService.DownloadModelAsync(fileProgress, ct);
                    },
                    cancellationToken);
            }
            else
            {
                downloaded = await _taggingService.DownloadModelAsync(cancellationToken: cancellationToken);
            }

            if (!downloaded)
            {
                Log.Warning("WD14 tagger model download failed or was unavailable; skipping index build");
                return new TagIndexBuildResult(Indexed: 0, Skipped: 0, Failed: total, NsfwCount: 0);
            }
        }

        // One fresh context for the whole run (not per-file): bulk upserts
        // need a shared change tracker, but the run itself is always
        // short-lived and disposed at the end — never held indefinitely. The
        // tracker is emptied every FlushBatchSize files (see FlushAsync), so
        // "shared" does not mean "unboundedly growing".
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Tag identity travels as IDs, not as tracked entities: ChangeTracker
        // .Clear() at every chunk boundary detaches every ImageTag instance,
        // so a dictionary of entity references would hand later files stale,
        // untracked objects and re-insert tags that already exist. IDs survive
        // a clear; references do not.
        var tagIds = await context.ImageTags
            .ToDictionaryAsync(t => t.Name, t => t.Id, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // Tags first seen inside the chunk currently being staged. Their IDs
        // do not exist until that chunk is saved, so their assignments point
        // at the still-Added entity through the navigation property instead;
        // the IDs are promoted into tagIds once the flush has populated them.
        var pendingTags = new Dictionary<string, ImageTag>(StringComparer.OrdinalIgnoreCase);

        // Files staged into the tracker but not yet saved, and how many of
        // them were NSFW — needed to unwind the counters if a flush fails.
        var pendingFiles = 0;
        var pendingNsfw = 0;

        // Persist the staged chunk and reset the tracker. A failure here
        // (WAL-mode contention with the rest of the app, "database is
        // locked", a constraint we did not anticipate) must be reported
        // through TagIndexBuildResult rather than thrown out of
        // BuildIndexAsync: a partially written batch is an outcome the UI can
        // show, not a crash. Cancellation still propagates.
        async Task FlushAsync()
        {
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                foreach (var (name, tag) in pendingTags)
                    tagIds[name] = tag.Id;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Error(ex, "Failed to persist a batch of {Count} tag-index row(s); those files remain unindexed", pendingFiles);
                indexed -= pendingFiles;
                failed += pendingFiles;
                nsfwCount -= pendingNsfw;
            }
            finally
            {
                pendingTags.Clear();
                pendingFiles = 0;
                pendingNsfw = 0;

                // Cleared on success AND failure. On success this is what
                // keeps DetectChanges O(chunk) instead of O(run). On failure
                // it discards the rejected changes, so the next flush is not
                // doomed to replay them and fail again.
                context.ChangeTracker.Clear();
            }
        }

        for (var i = 0; i < work.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = work[i];
            progress?.Report(new TagIndexBuildProgress(i, total, path));

            if (pendingFiles >= FlushBatchSize)
                await FlushAsync();

            try
            {
                if (!File.Exists(path))
                {
                    skipped++;
                    continue;
                }

                var fileInfo = new FileInfo(path);

                var existing = await context.ImageMediaTagIndexes
                    .FirstOrDefaultAsync(e => e.FilePath == path, cancellationToken);

                if (existing is not null
                    && existing.FileSizeBytes == fileInfo.Length
                    && existing.FileLastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
                {
                    skipped++;
                    if (existing.IsNsfw) nsfwCount++;
                    continue;
                }

                // Everything that can realistically fail for this file happens
                // BEFORE the context is mutated: decode, classify,
                // de-duplicate, and read back the assignments this run
                // replaces. That ordering is the whole point — a file that
                // dies here leaves nothing staged, so it stays eligible for
                // the next run. Mutating first (as this loop used to) meant a
                // mid-file failure still committed the row at the terminal
                // save, stamped with the current size/mtime, and every future
                // build then saw it as "unchanged" and skipped it forever
                // while the UI reported it as Failed.
                var (imageData, width, height) = LoadImagePixels(path);
                var result = await _taggingService.TagImageAsync(imageData, width, height, cancellationToken: cancellationToken);

                if (!result.Success || result.RatingLabel is null)
                {
                    Log.Warning("Tagging failed for {Path}: {Error}", path, result.ErrorMessage);
                    failed++;
                    continue;
                }

                // The tagger contract does not promise distinct names, and the
                // tag lookup is case-insensitive — two entries differing only
                // in case would produce two assignments sharing one composite
                // key and throw. Keep the highest confidence per name.
                var distinctTags = result.Tags
                    .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.MaxBy(t => t.Confidence)!)
                    .ToList();

                var replacedAssignments = existing is null
                    ? null
                    : await context.ImageMediaTagAssignments
                        .Where(a => a.ImageMediaTagIndexId == existing.Id)
                        .ToListAsync(cancellationToken);

                // ---- from here on the change tracker is mutated; nothing
                // ---- below this line performs I/O or can meaningfully throw.
                var row = existing ?? new ImageMediaTagIndex { FilePath = path };
                row.FileSizeBytes = fileInfo.Length;
                row.FileLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                row.RatingLabel = result.RatingLabel;
                row.RatingScore = result.RatingScore;
                row.IsNsfw = result.IsNsfw;
                row.IndexedAtUtc = DateTimeOffset.UtcNow;

                if (existing is null)
                    context.ImageMediaTagIndexes.Add(row);
                else
                    context.ImageMediaTagAssignments.RemoveRange(replacedAssignments!);

                foreach (var tag in distinctTags)
                {
                    var assignment = new ImageMediaTagAssignment
                    {
                        ImageMediaTagIndex = row,
                        Confidence = tag.Confidence,
                    };

                    if (tagIds.TryGetValue(tag.Name, out var tagId))
                    {
                        // Already persisted (earlier run or earlier chunk):
                        // reference it by FK, no entity needed.
                        assignment.ImageTagId = tagId;
                    }
                    else
                    {
                        if (!pendingTags.TryGetValue(tag.Name, out var tagEntity))
                        {
                            tagEntity = new ImageTag { Name = tag.Name };
                            pendingTags[tag.Name] = tagEntity;
                            context.ImageTags.Add(tagEntity);
                        }

                        assignment.ImageTag = tagEntity;
                    }

                    context.ImageMediaTagAssignments.Add(assignment);
                }

                if (result.IsNsfw)
                {
                    nsfwCount++;
                    pendingNsfw++;
                }

                indexed++;
                pendingFiles++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Failed to index {Path}", path);
                failed++;
            }
        }

        await FlushAsync();
        progress?.Report(new TagIndexBuildProgress(total, total, null));

        return new TagIndexBuildResult(indexed, skipped, failed, nsfwCount);
    }

    public async Task<IReadOnlyList<TagFrequency>> GetTagCloudAsync(int maxTags = 200, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Grouping by a.ImageTag!.Name (join-then-group-by-navigation) does
        // not translate on the SQLite provider ("could not be translated").
        // Counting the Assignments collection per ImageTag instead compiles
        // to a correlated COUNT(*) subquery, which is well-supported. Tags
        // with zero assignments (all their images were re-indexed with that
        // tag no longer present) are excluded — the cloud should only show
        // tags actually carried by at least one indexed image.
        // Order by the entity's own correlated-count subquery BEFORE
        // projecting into the TagFrequency record: ordering by a property of
        // an already-projected `new TagFrequency(...)` re-expands that whole
        // constructor expression inside ORDER BY, which the SQLite provider
        // cannot translate.
        return await context.ImageTags
            .Where(t => t.Assignments.Any())
            .OrderByDescending(t => t.Assignments.Count)
            .Select(t => new TagFrequency(t.Name, t.Assignments.Count))
            .Take(maxTags)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> SearchAsync(
        IReadOnlyList<string> requiredTags,
        NsfwFilterMode nsfwFilter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requiredTags);
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.ImageMediaTagIndexes.AsQueryable();

        query = nsfwFilter switch
        {
            NsfwFilterMode.HideNsfw => query.Where(e => !e.IsNsfw),
            NsfwFilterMode.NsfwOnly => query.Where(e => e.IsNsfw),
            _ => query,
        };

        // AND semantics: require every requested tag. One Any(...) filter per
        // tag so EF Core translates the whole thing into a single SQL query
        // (N correlated EXISTS subqueries) instead of N round trips.
        foreach (var tag in requiredTags)
        {
            var tagName = tag;
            query = query.Where(e => e.TagAssignments.Any(a => a.ImageTag!.Name == tagName));
        }

        return await query.Select(e => e.FilePath).ToListAsync(cancellationToken);
    }

    public async Task<int> GetIndexedCountAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.ImageMediaTagIndexes.CountAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, ImageTagLookup>> GetTagsForFilesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0)
            return new Dictionary<string, ImageTagLookup>();

        var normalizedPaths = filePaths.Select(Path.GetFullPath).ToList();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Untracked, and projected to exactly what a tile shows (an NSFW flag
        // plus its tag names). The Include/ThenInclude version this replaces
        // materialized a fully tracked graph — every ImageMediaTagIndex row,
        // every assignment on it, and every ImageTag those reach — which for a
        // fully indexed gallery is hundreds of thousands of entities, built on
        // the gallery-load path that already caused a UI freeze once (#397).
        var rows = await context.ImageMediaTagIndexes
            .AsNoTracking()
            .Where(e => normalizedPaths.Contains(e.FilePath))
            .Select(e => new
            {
                e.FilePath,
                e.IsNsfw,
                Tags = e.TagAssignments.Select(a => a.ImageTag!.Name).ToList(),
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            e => e.FilePath,
            e => new ImageTagLookup(e.IsNsfw, e.Tags),
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> RemoveIndexEntriesAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        if (filePaths.Count == 0)
            return 0;

        // Same normalization as the write path, so a caller holding the
        // gallery's own (possibly relative or differently-cased) path still
        // matches the stored row.
        var normalizedPaths = filePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // ExecuteDeleteAsync issues one DELETE instead of loading the rows to
        // delete them. The assignment rows go with it: the FK is configured
        // (and emitted into the migration) as ON DELETE CASCADE, which SQLite
        // enforces because Microsoft.Data.Sqlite turns foreign keys on for
        // every connection. Covered by
        // RemoveIndexEntriesAsync_AlsoRemovesTheTagAssignments.
        return await context.ImageMediaTagIndexes
            .Where(e => normalizedPaths.Contains(e.FilePath))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static (byte[] Data, int Width, int Height) LoadImagePixels(string path)
    {
        using var image = Image.Load<Rgba32>(path);
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return (pixels, image.Width, image.Height);
    }
}
