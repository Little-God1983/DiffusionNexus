using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Service.Services;

public sealed class TagIndexService : ITagIndexService
{
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

        var indexed = 0;
        var skipped = 0;
        var failed = 0;
        var nsfwCount = 0;
        var total = filePaths.Count;

        // The model downloads on first use, not eagerly — this is that
        // trigger point. Routed through IDownloadCoordinator when available
        // so it's visible in the Unified Console/status bar exactly like
        // every other model download (see the download-unification plan).
        if (_taggingService.GetModelStatus() != ModelStatus.Ready)
        {
            progress?.Report(new TagIndexBuildProgress(0, total, "Downloading tagger model…"));

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
        // short-lived and disposed at the end — never held indefinitely.
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var existingTags = await context.ImageTags
            .ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);

        for (var i = 0; i < filePaths.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = filePaths[i];
            progress?.Report(new TagIndexBuildProgress(i, total, path));

            try
            {
                if (!File.Exists(path))
                {
                    skipped++;
                    continue;
                }

                var fileInfo = new FileInfo(path);
                var normalizedPath = Path.GetFullPath(path);

                var existing = await context.ImageMediaTagIndexes
                    .FirstOrDefaultAsync(e => e.FilePath == normalizedPath, cancellationToken);

                if (existing is not null
                    && existing.FileSizeBytes == fileInfo.Length
                    && existing.FileLastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
                {
                    skipped++;
                    if (existing.IsNsfw) nsfwCount++;
                    continue;
                }

                var (imageData, width, height) = LoadImagePixels(path);
                var result = await _taggingService.TagImageAsync(imageData, width, height, cancellationToken: cancellationToken);

                if (!result.Success || result.RatingLabel is null)
                {
                    Log.Warning("Tagging failed for {Path}: {Error}", path, result.ErrorMessage);
                    failed++;
                    continue;
                }

                var row = existing ?? new ImageMediaTagIndex { FilePath = normalizedPath };
                row.FileSizeBytes = fileInfo.Length;
                row.FileLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
                row.RatingLabel = result.RatingLabel;
                row.RatingScore = result.RatingScore;
                row.IsNsfw = result.IsNsfw;
                row.IndexedAtUtc = DateTimeOffset.UtcNow;

                if (existing is null)
                {
                    context.ImageMediaTagIndexes.Add(row);
                }
                else
                {
                    var oldAssignments = await context.ImageMediaTagAssignments
                        .Where(a => a.ImageMediaTagIndexId == existing.Id)
                        .ToListAsync(cancellationToken);
                    context.ImageMediaTagAssignments.RemoveRange(oldAssignments);
                }

                foreach (var tag in result.Tags)
                {
                    if (!existingTags.TryGetValue(tag.Name, out var tagEntity))
                    {
                        tagEntity = new ImageTag { Name = tag.Name };
                        existingTags[tag.Name] = tagEntity;
                        context.ImageTags.Add(tagEntity);
                    }

                    context.ImageMediaTagAssignments.Add(new ImageMediaTagAssignment
                    {
                        ImageMediaTagIndex = row,
                        ImageTag = tagEntity,
                        Confidence = tag.Confidence,
                    });
                }

                if (result.IsNsfw) nsfwCount++;
                indexed++;

                // Flush periodically on large batches: bounds how much a crash
                // partway through a big folder can lose, and gives new
                // ImageTag rows real IDs before later iterations reuse them.
                if (indexed % 25 == 0)
                    await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "Failed to index {Path}", path);
                failed++;
            }
        }

        await context.SaveChangesAsync(cancellationToken);
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

    private static (byte[] Data, int Width, int Height) LoadImagePixels(string path)
    {
        using var image = Image.Load<Rgba32>(path);
        var pixels = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixels);
        return (pixels, image.Width, image.Height);
    }
}
