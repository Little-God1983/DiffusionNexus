using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public sealed class TagIndexServiceTests : IAsyncDisposable
{
    private readonly string _dbDir;
    private readonly string _imagesDir;
    private readonly DbContextOptions<DiffusionNexusCoreDbContext> _options;

    public TagIndexServiceTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "dn-tagindex-" + Guid.NewGuid().ToString("N"));
        _imagesDir = Path.Combine(_dbDir, "images");
        Directory.CreateDirectory(_imagesDir);
        _options = DiffusionNexusCoreDbContext.CreateOptions(_dbDir);

        using var context = new DiffusionNexusCoreDbContext(_options);
        context.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dbDir, recursive: true); } catch { /* best effort */ }
        await ValueTask.CompletedTask;
    }

    private string CreateFakeImage(string name, int width = 4)
    {
        var path = Path.Combine(_imagesDir, name);
        using var image = new Image<Rgba32>(width, 4);
        image.SaveAsPng(path);
        return path;
    }

    private sealed class SingleDbContextFactory : IDbContextFactory<DiffusionNexusCoreDbContext>
    {
        private readonly DbContextOptions<DiffusionNexusCoreDbContext> _options;
        public SingleDbContextFactory(DbContextOptions<DiffusionNexusCoreDbContext> options) => _options = options;
        public DiffusionNexusCoreDbContext CreateDbContext() => new(_options);
        public Task<DiffusionNexusCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Reads normally but fails every write — stands in for the WAL-mode
    /// contention ("database is locked") that a real gallery build can hit
    /// while the rest of the app is using the same file.
    /// </summary>
    private sealed class UnwritableDbContext : DiffusionNexusCoreDbContext
    {
        public UnwritableDbContext(DbContextOptions<DiffusionNexusCoreDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => Task.FromException<int>(new DbUpdateException(
                "An error occurred while saving the entity changes.",
                new InvalidOperationException("SQLite Error 5: 'database is locked'.")));
    }

    private sealed class UnwritableDbContextFactory : IDbContextFactory<DiffusionNexusCoreDbContext>
    {
        private readonly DbContextOptions<DiffusionNexusCoreDbContext> _options;
        public UnwritableDbContextFactory(DbContextOptions<DiffusionNexusCoreDbContext> options) => _options = options;
        public DiffusionNexusCoreDbContext CreateDbContext() => new UnwritableDbContext(_options);
        public Task<DiffusionNexusCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Records how many entities the change tracker held at dispose time. The
    /// service owns its contexts and disposes them before returning, so this
    /// is the only place a test can observe what a read path tracked.
    /// </summary>
    private sealed class TrackingProbeDbContext : DiffusionNexusCoreDbContext
    {
        private readonly List<int> _trackedAtDispose;

        public TrackingProbeDbContext(
            DbContextOptions<DiffusionNexusCoreDbContext> options,
            List<int> trackedAtDispose)
            : base(options) => _trackedAtDispose = trackedAtDispose;

        private void Record() => _trackedAtDispose.Add(ChangeTracker.Entries().Count());

        public override void Dispose()
        {
            Record();
            base.Dispose();
        }

        public override ValueTask DisposeAsync()
        {
            Record();
            return base.DisposeAsync();
        }
    }

    private sealed class TrackingProbeDbContextFactory : IDbContextFactory<DiffusionNexusCoreDbContext>
    {
        private readonly DbContextOptions<DiffusionNexusCoreDbContext> _options;
        private readonly List<int> _trackedAtDispose;

        public TrackingProbeDbContextFactory(
            DbContextOptions<DiffusionNexusCoreDbContext> options,
            List<int> trackedAtDispose)
        {
            _options = options;
            _trackedAtDispose = trackedAtDispose;
        }

        public DiffusionNexusCoreDbContext CreateDbContext() => new TrackingProbeDbContext(_options, _trackedAtDispose);
        public Task<DiffusionNexusCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// Tagger stub keyed on image width, so each test file gets a
    /// deterministic result regardless of the order the service processes
    /// them in. <paramref name="perWidth"/> maps image width to the outcome.
    /// </summary>
    private static Mock<IImageTaggingService> TaggerByWidth(Func<int, ImageTagResult> perWidth)
    {
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(
                It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[] _, int w, int _, float _, CancellationToken _) => perWidth(w));
        return tagging;
    }

    [Fact]
    public async Task BuildIndexAsync_IndexesNewFiles_AndPersistsTagsAndRating()
    {
        var path = CreateFakeImage("a.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(
                new[] { new ImageTagScore("dog", 0.9f), new ImageTagScore("outdoor", 0.6f) },
                "general", 0.8f, isNsfw: false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        var result = await service.BuildIndexAsync(new[] { path });

        result.Indexed.Should().Be(1);
        result.Failed.Should().Be(0);
        var cloud = await service.GetTagCloudAsync();
        cloud.Should().Contain(t => t.Name == "dog" && t.Count == 1);
        cloud.Should().Contain(t => t.Name == "outdoor" && t.Count == 1);
    }

    [Fact]
    public async Task BuildIndexAsync_SkipsUnchangedFiles_OnSecondRun()
    {
        var path = CreateFakeImage("b.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, isNsfw: false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        await service.BuildIndexAsync(new[] { path });
        var second = await service.BuildIndexAsync(new[] { path });

        second.Indexed.Should().Be(0);
        second.Skipped.Should().Be(1);
        tagging.Verify(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SearchAsync_ReturnsOnlyImagesWithAllRequiredTags()
    {
        var pathA = CreateFakeImage("dog-outdoor.png");
        var pathB = CreateFakeImage("dog-only.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.SetupSequence(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(new[] { new ImageTagScore("dog", 0.9f), new ImageTagScore("outdoor", 0.8f) }, "general", 0.9f, false))
            .ReturnsAsync(ImageTagResult.Succeeded(new[] { new ImageTagScore("dog", 0.9f) }, "general", 0.9f, false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { pathA, pathB });

        var matches = await service.SearchAsync(new[] { "dog", "outdoor" }, NsfwFilterMode.ShowAll);

        matches.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(pathA));
    }

    [Fact]
    public async Task SearchAsync_HideNsfw_ExcludesFlaggedImages()
    {
        var pathA = CreateFakeImage("safe.png");
        var pathB = CreateFakeImage("flagged.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.SetupSequence(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, isNsfw: false))
            .ReturnsAsync(ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "explicit", 0.9f, isNsfw: true));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { pathA, pathB });

        var matches = await service.SearchAsync(Array.Empty<string>(), NsfwFilterMode.HideNsfw);

        matches.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(pathA));
    }

    [Fact]
    public async Task GetIndexedCountAsync_ReflectsBuiltIndex()
    {
        var path = CreateFakeImage("c.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        await service.BuildIndexAsync(new[] { path });

        (await service.GetIndexedCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task BuildIndexAsync_ReindexesModifiedFile_WhenNewTagSetOverlapsOldTagSet()
    {
        // Regression probe for the "existing row" update branch, which none
        // of the brief's original 8 tests exercised: re-tagging a file that
        // KEEPS a tag it already had (very much the common case) means the
        // old ImageMediaTagAssignment row being removed and the new one
        // being added share the exact same (ImageMediaTagIndexId, ImageTagId)
        // composite key.
        var path = CreateFakeImage("g.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.SetupSequence(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(new[] { new ImageTagScore("dog", 0.9f), new ImageTagScore("outdoor", 0.7f) }, "general", 0.9f, false))
            .ReturnsAsync(ImageTagResult.Succeeded(new[] { new ImageTagScore("dog", 0.95f), new ImageTagScore("cat", 0.5f) }, "general", 0.9f, false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        await service.BuildIndexAsync(new[] { path });

        // Mutate the file so its size changes and it's detected as modified.
        using (var biggerImage = new Image<Rgba32>(8, 8))
            biggerImage.SaveAsPng(path);

        var act = async () => await service.BuildIndexAsync(new[] { path });

        var second = await act.Should().NotThrowAsync();
        second.Which.Indexed.Should().Be(1);
        second.Which.Failed.Should().Be(0);

        var matches = await service.SearchAsync(new[] { "dog" }, NsfwFilterMode.ShowAll);
        matches.Should().ContainSingle().Which.Should().Be(Path.GetFullPath(path));

        var outdoorMatches = await service.SearchAsync(new[] { "outdoor" }, NsfwFilterMode.ShowAll);
        outdoorMatches.Should().BeEmpty("outdoor was dropped on re-tagging and the assignment should have been replaced, not duplicated");

        var catMatches = await service.SearchAsync(new[] { "cat" }, NsfwFilterMode.ShowAll);
        catMatches.Should().ContainSingle();
    }

    [Fact]
    public async Task BuildIndexAsync_DownloadsModelFirst_WhenNotReady_ThroughDownloadCoordinator()
    {
        var path = CreateFakeImage("d.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.NotDownloaded);
        tagging.Setup(t => t.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<IProgress<ModelDownloadProgress>?, CancellationToken>((p, _) =>
                p?.Report(new ModelDownloadProgress(100, 100, "Download complete")))
            .ReturnsAsync(true);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, false));

        var coordinator = new Mock<IDownloadCoordinator>();
        coordinator
            .Setup(c => c.EnqueueAsync(
                It.IsAny<string>(),
                It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>, CancellationToken>(
                (name, action, ct) => action(new Progress<DownloadTaskProgress>(), ct));

        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object, coordinator.Object);

        var result = await service.BuildIndexAsync(new[] { path });

        coordinator.Verify(c => c.EnqueueAsync(
            It.Is<string>(n => n.Contains("WD14")),
            It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        tagging.Verify(t => t.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Once);
        result.Indexed.Should().Be(1);
    }

    [Fact]
    public async Task BuildIndexAsync_SkipsDownload_WhenModelAlreadyReady()
    {
        var path = CreateFakeImage("e.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        await service.BuildIndexAsync(new[] { path });

        tagging.Verify(t => t.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuildIndexAsync_FailsAllFiles_WhenModelDownloadFails()
    {
        var path = CreateFakeImage("f.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.NotDownloaded);
        tagging.Setup(t => t.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        var result = await service.BuildIndexAsync(new[] { path });

        result.Failed.Should().Be(1);
        result.Indexed.Should().Be(0);
        tagging.Verify(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Fix 1: duplicate input paths ------------------------------------

    [Fact]
    public async Task BuildIndexAsync_DeduplicatesInputPaths_InsteadOfFailingTheWholeBatch()
    {
        // The "already indexed?" check is a DB query, so it cannot see a row
        // staged earlier in the same run. Two identical paths used to stage
        // two inserts against one unique FilePath, and the terminal
        // SaveChanges threw DbUpdateException out of BuildIndexAsync —
        // taking the whole batch down, not just the duplicate. Reachable
        // whenever a user configures nested/overlapping gallery folders.
        var path = CreateFakeImage("dupe.png");
        var tagging = TaggerByWidth(_ => ImageTagResult.Succeeded(
            new[] { new ImageTagScore("dog", 0.9f) }, "general", 0.9f, false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        // Same file three ways: verbatim, relative-round-tripped, and
        // case-varied (Windows paths are case-insensitive).
        var viaDot = Path.Combine(_imagesDir, ".", "dupe.png");
        var act = async () => await service.BuildIndexAsync(new[] { path, path, viaDot, path.ToUpperInvariant() });

        var result = await act.Should().NotThrowAsync();
        result.Which.Indexed.Should().Be(1);
        result.Which.Failed.Should().Be(0);
        (await service.GetIndexedCountAsync()).Should().Be(1);
        tagging.Verify(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Fixes 2 + 3: chunked flush/clear, and failure containment -------

    [Fact]
    public async Task BuildIndexAsync_IndexesEveryFile_AcrossMultipleFlushAndClearBoundaries()
    {
        // 60 files at a flush cadence of 25 crosses two flush/clear
        // boundaries. ChangeTracker.Clear() detaches every tracked ImageTag,
        // so this is what proves the indexer carries tag identity as IDs
        // rather than entity references: the shared "common" tag must be
        // reused by files on both sides of every boundary instead of being
        // re-inserted (which the unique index would reject).
        const int fileCount = 60;
        var paths = new List<string>();
        for (var i = 1; i <= fileCount; i++)
            paths.Add(CreateFakeImage($"batch-{i:D2}.png", width: i));

        var tagging = TaggerByWidth(w => ImageTagResult.Succeeded(
            new[] { new ImageTagScore($"tag{w}", 0.9f), new ImageTagScore("common", 0.5f) },
            "general", 0.9f, isNsfw: false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        var result = await service.BuildIndexAsync(paths);

        result.Indexed.Should().Be(fileCount);
        result.Failed.Should().Be(0);
        result.Skipped.Should().Be(0);
        (await service.GetIndexedCountAsync()).Should().Be(fileCount);

        // Every file kept its own unique tag, including the ones staged in
        // the chunk that was cleared mid-run.
        for (var i = 1; i <= fileCount; i++)
        {
            var matches = await service.SearchAsync(new[] { $"tag{i}" }, NsfwFilterMode.ShowAll);
            matches.Should().ContainSingle($"file {i} should be findable by its own tag")
                .Which.Should().Be(Path.GetFullPath(paths[i - 1]));
        }

        // The shared tag exists exactly once and is attached to all 60 files.
        var cloud = await service.GetTagCloudAsync(maxTags: 500);
        cloud.Should().ContainSingle(t => t.Name == "common")
            .Which.Count.Should().Be(fileCount);
        cloud.Should().HaveCount(fileCount + 1, "60 per-file tags plus one shared tag, with no duplicates across flush boundaries");
        (await service.SearchAsync(new[] { "common" }, NsfwFilterMode.ShowAll)).Should().HaveCount(fileCount);
    }

    [Fact]
    public async Task BuildIndexAsync_NeverPersistsARowForAFileItReportedAsFailed()
    {
        // The permanent-skip bug. A file's row used to be mutated and staged
        // BEFORE its tag loop ran, so a failure partway through that loop
        // still got committed by the terminal SaveChanges — stamped with the
        // file's current size/mtime. The UI reported the file as Failed while
        // every future build saw it as "unchanged" and skipped it forever.
        var ok = CreateFakeImage("contain-ok.png", width: 1);
        var thrower = CreateFakeImage("contain-throw.png", width: 2);
        var poison = CreateFakeImage("contain-poison.png", width: 3);

        var failing = new Mock<IImageTaggingService>();
        failing.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        failing.Setup(t => t.TagImageAsync(
                It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Returns((byte[] _, int w, int _, float _, CancellationToken _) => w switch
            {
                // Fails before the context is touched at all.
                2 => Task.FromException<ImageTagResult>(new InvalidOperationException("tagger exploded")),
                // Duplicate tag names: the second assignment threw on a
                // duplicate tracked key — AFTER the row had been staged.
                // This is the exact composition that froze a file.
                3 => Task.FromResult(ImageTagResult.Succeeded(
                    new[] { new ImageTagScore("dog", 0.9f), new ImageTagScore("dog", 0.5f) }, "general", 0.9f, false)),
                _ => Task.FromResult(ImageTagResult.Succeeded(
                    new[] { new ImageTagScore($"tag{w}", 0.9f) }, "general", 0.9f, false)),
            });

        var service = new TagIndexService(new SingleDbContextFactory(_options), failing.Object);

        var first = await service.BuildIndexAsync(new[] { ok, thrower, poison });

        first.Indexed.Should().Be(2, "the healthy file and the duplicate-tag file both index cleanly now");
        first.Failed.Should().Be(1, "only the throwing file fails");
        (await service.GetIndexedCountAsync()).Should().Be(first.Indexed,
            "a file reported as Failed must leave no row behind — a committed row carries a fresh size/mtime stamp, which makes every future run skip it forever");

        // Second run with a healthy tagger: the failed file must be retried,
        // not silently treated as up to date.
        var healthy = TaggerByWidth(w => ImageTagResult.Succeeded(
            new[] { new ImageTagScore($"tag{w}", 0.9f) }, "general", 0.9f, false));
        var retryService = new TagIndexService(new SingleDbContextFactory(_options), healthy.Object);

        var second = await retryService.BuildIndexAsync(new[] { ok, thrower, poison });

        second.Indexed.Should().Be(1, "only the previously failed file should need work");
        second.Skipped.Should().Be(2);
        second.Failed.Should().Be(0);
        (await retryService.SearchAsync(new[] { "tag2" }, NsfwFilterMode.ShowAll))
            .Should().ContainSingle().Which.Should().Be(Path.GetFullPath(thrower));
    }

    [Fact]
    public async Task BuildIndexAsync_PropagatesCancellation_InsteadOfCountingItAsFailure()
    {
        // Cancellation is the one thing that is an exception, not a Failed
        // count — the per-file catch filter and the flush guard must both
        // let OperationCanceledException through, or a cancelled build would
        // look like a build where every file failed.
        var path = CreateFakeImage("cancel.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(
                It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromException<ImageTagResult>(new OperationCanceledException()));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        var act = async () => await service.BuildIndexAsync(new[] { path });

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Fix 4: duplicate tag names within one result --------------------

    [Fact]
    public async Task BuildIndexAsync_DeduplicatesTagsWithinOneResult_KeepingHighestConfidence()
    {
        // Two entries with the same name (the lookup is case-insensitive)
        // produced two assignments sharing one composite key, which threw
        // mid-staging — the exact trigger for the permanent-skip bug above.
        var path = CreateFakeImage("dupetags.png");
        var tagging = TaggerByWidth(_ => ImageTagResult.Succeeded(
            new[]
            {
                new ImageTagScore("dog", 0.4f),
                new ImageTagScore("Dog", 0.95f),
                new ImageTagScore("dog", 0.7f),
                new ImageTagScore("outdoor", 0.6f),
            },
            "general", 0.9f, false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        var act = async () => await service.BuildIndexAsync(new[] { path });

        var result = await act.Should().NotThrowAsync();
        result.Which.Indexed.Should().Be(1);
        result.Which.Failed.Should().Be(0);

        var cloud = await service.GetTagCloudAsync();
        cloud.Should().ContainSingle(t => string.Equals(t.Name, "dog", StringComparison.OrdinalIgnoreCase))
            .Which.Count.Should().Be(1, "the three 'dog' entries collapse into one assignment");
        cloud.Should().ContainSingle(t => t.Name == "outdoor");

        await using var context = new DiffusionNexusCoreDbContext(_options);
        var confidence = await context.ImageMediaTagAssignments
            .Where(a => a.ImageTag!.Name == "dog")
            .Select(a => a.Confidence)
            .SingleAsync();
        confidence.Should().Be(0.95f, "de-duplication keeps the highest-confidence entry");
    }

    // ---- Fix 5: a failing save is a result, not an exception -------------

    [Fact]
    public async Task BuildIndexAsync_ReportsSaveFailureAsFailedFiles_InsteadOfThrowing()
    {
        var pathA = CreateFakeImage("locked-a.png", width: 1);
        var pathB = CreateFakeImage("locked-b.png", width: 2);
        var tagging = TaggerByWidth(w => ImageTagResult.Succeeded(
            new[] { new ImageTagScore($"tag{w}", 0.9f) }, "general", 0.9f, isNsfw: w == 2));
        var service = new TagIndexService(new UnwritableDbContextFactory(_options), tagging.Object);

        var act = async () => await service.BuildIndexAsync(new[] { pathA, pathB });

        var result = await act.Should().NotThrowAsync();
        result.Which.Failed.Should().Be(2, "both staged files were lost when the flush failed");
        result.Which.Indexed.Should().Be(0, "nothing reached the database");
        result.Which.NsfwCount.Should().Be(0, "the NSFW tally must unwind with the lost files too");
        (await service.GetIndexedCountAsync()).Should().Be(0);
    }

    // ---- Fix 6: non-image inputs -----------------------------------------

    [Fact]
    public async Task BuildIndexAsync_SkipsNonImageFiles_WithoutCountingThemAsFailures()
    {
        // The intended caller enumerates gallery folders, which contain
        // videos. Each one used to reach Image.Load, throw, and be reported
        // as a failure for a file that was never eligible for tagging.
        var image = CreateFakeImage("real.png");
        var video = Path.Combine(_imagesDir, "clip.mp4");
        File.WriteAllBytes(video, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        var sidecar = Path.Combine(_imagesDir, "real.txt");
        File.WriteAllText(sidecar, "caption");

        var tagging = TaggerByWidth(_ => ImageTagResult.Succeeded(
            Array.Empty<ImageTagScore>(), "general", 0.9f, false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        var result = await service.BuildIndexAsync(new[] { image, video, sidecar });

        result.Indexed.Should().Be(1);
        result.Failed.Should().Be(0, "non-images were never eligible, so they are not failures");
        result.Skipped.Should().Be(0, "Skipped is reserved for images that were genuinely skipped");
        tagging.Verify(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- Fix 10: nothing to do means no 379 MB download ------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildIndexAsync_ReturnsEmptyResult_WithoutDownloadingTheModel_WhenNothingIsTaggable(bool useVideoOnlyList)
    {
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.NotDownloaded);
        tagging.Setup(t => t.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var coordinator = new Mock<IDownloadCoordinator>();
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object, coordinator.Object);

        string[] input;
        if (useVideoOnlyList)
        {
            var video = Path.Combine(_imagesDir, "only.mp4");
            File.WriteAllBytes(video, new byte[] { 0x00 });
            input = new[] { video };
        }
        else
        {
            input = Array.Empty<string>();
        }

        var result = await service.BuildIndexAsync(input);

        result.Should().Be(new TagIndexBuildResult(0, 0, 0, 0));
        tagging.Verify(t => t.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        coordinator.Verify(c => c.EnqueueAsync(
            It.IsAny<string>(),
            It.IsAny<Func<IProgress<DownloadTaskProgress>, CancellationToken, Task<bool>>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- Fix 11: tag cloud ordering and zero-assignment exclusion --------

    [Fact]
    public async Task GetTagCloudAsync_OrdersByCountDescending_AndExcludesTagsWithNoAssignments()
    {
        // Both behaviors are deliberate and both are load-bearing: the query
        // filters, then orders by the entity's own correlated-count subquery,
        // then projects — reordering those steps reintroduces a real SQLite
        // translation failure. And a tag whose last assignment was dropped by
        // a re-index must vanish from the cloud rather than linger at zero.
        //
        // The fixture is built so that insertion order and count order
        // DISAGREE. That matters: an unordered SQLite scan comes back in
        // rowid (i.e. insertion) order, so a fixture whose most-used tag also
        // happens to be its first-inserted one would pass with the
        // OrderByDescending deleted from the query. Here "delta" is inserted
        // last and used most, so only a real ORDER BY can produce the
        // expected sequence.
        //   insertion order : alpha(2), beta(1), gamma(0), delta(3)
        //   expected order  : delta(3), alpha(2), beta(1)
        var twice = CreateFakeImage("cloud-a.png", width: 1);
        var once = CreateFakeImage("cloud-b.png", width: 2);
        var orphaned = CreateFakeImage("cloud-c.png", width: 3);
        var thrice1 = CreateFakeImage("cloud-d.png", width: 4);
        var thrice2 = CreateFakeImage("cloud-e.png", width: 5);
        var thrice3 = CreateFakeImage("cloud-f.png", width: 6);

        var tagging = TaggerByWidth(w => w switch
        {
            1 => ImageTagResult.Succeeded(new[] { new ImageTagScore("alpha", 0.9f), new ImageTagScore("beta", 0.8f) }, "general", 0.9f, false),
            2 => ImageTagResult.Succeeded(new[] { new ImageTagScore("alpha", 0.7f) }, "general", 0.9f, false),
            3 => ImageTagResult.Succeeded(new[] { new ImageTagScore("gamma", 0.6f) }, "general", 0.9f, false),
            _ => ImageTagResult.Succeeded(new[] { new ImageTagScore("delta", 0.5f) }, "general", 0.9f, false),
        });
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { twice, once, orphaned, thrice1, thrice2, thrice3 });

        (await service.GetTagCloudAsync()).Should().Contain(t => t.Name == "gamma");

        // Re-index the third file with no tags at all, orphaning "gamma".
        using (var changed = new Image<Rgba32>(3, 9)) changed.SaveAsPng(orphaned);
        var retagged = TaggerByWidth(_ => ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, false));
        await new TagIndexService(new SingleDbContextFactory(_options), retagged.Object)
            .BuildIndexAsync(new[] { orphaned });

        var cloud = await service.GetTagCloudAsync();

        cloud.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { "delta", "alpha", "beta" },
            options => options.WithStrictOrdering(),
            "the cloud is ordered strictly by descending assignment count, not by insertion order");
        cloud.Select(t => t.Count).Should().BeEquivalentTo(new[] { 3, 2, 1 }, options => options.WithStrictOrdering());
        cloud.Should().NotContain(t => t.Name == "gamma", "a tag with zero assignments is excluded, not shown at zero");
    }

    // ---- GetTagsForFilesAsync: bulk tile hydration ------------------------

    [Fact]
    public async Task GetTagsForFilesAsync_ReturnsNsfwFlagAndTagsKeyedByNormalizedPath()
    {
        var path = CreateFakeImage("lookup.png");
        var tagging = new Mock<IImageTaggingService>();
        // Brief's original test omitted this stub. Without it Moq's
        // unconfigured GetModelStatus() returns default(ModelStatus), which
        // is NotDownloaded (the first enum member, = 0) — sending
        // BuildIndexAsync down the model-download gate, where the also-
        // unstubbed DownloadModelAsync() returns Moq's built-in Task<bool>
        // default (false), so the build bails out with everything Failed and
        // no row is ever written. Confirmed empirically: the test failed with
        // KeyNotFoundException before this stub was added. Every other test
        // in this file sets this up explicitly; this one follows suit.
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(
                new[] { new ImageTagScore("dog", 0.9f), new ImageTagScore("outdoor", 0.6f) },
                "explicit", 0.7f, isNsfw: true));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { path });

        var lookup = await service.GetTagsForFilesAsync(new[] { path });

        var entry = lookup[Path.GetFullPath(path)];
        entry.IsNsfw.Should().BeTrue();
        entry.Tags.Should().BeEquivalentTo(new[] { "dog", "outdoor" });
    }

    [Fact]
    public async Task GetTagsForFilesAsync_OmitsUnindexedPaths()
    {
        var service = new TagIndexService(new SingleDbContextFactory(_options), new Mock<IImageTaggingService>().Object);

        var lookup = await service.GetTagsForFilesAsync(new[] { @"C:\never\indexed.png" });

        lookup.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTagsForFilesAsync_KeepsEachFilesTagsSeparate_AcrossManyFilesAndSharedTags()
    {
        // Guards the projected (Select-per-row) form of the query. A collection
        // projection is where a rewrite most plausibly goes wrong — a botched
        // join can bleed one file's tags onto another, or drop the row for a
        // file that carries no tags at all — and neither failure is visible in
        // a single-file test.
        var multi = CreateFakeImage("proj-multi.png", width: 1);
        var shared = CreateFakeImage("proj-shared.png", width: 2);
        var untagged = CreateFakeImage("proj-untagged.png", width: 3);
        var unrequested = CreateFakeImage("proj-unrequested.png", width: 4);

        var tagging = TaggerByWidth(w => w switch
        {
            1 => ImageTagResult.Succeeded(
                new[] { new ImageTagScore("dog", 0.9f), new ImageTagScore("outdoor", 0.8f), new ImageTagScore("sunset", 0.7f) },
                "general", 0.9f, isNsfw: false),
            2 => ImageTagResult.Succeeded(
                new[] { new ImageTagScore("dog", 0.6f) }, "explicit", 0.9f, isNsfw: true),
            3 => ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, isNsfw: false),
            _ => ImageTagResult.Succeeded(
                new[] { new ImageTagScore("cat", 0.9f) }, "general", 0.9f, isNsfw: false),
        });
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { multi, shared, untagged, unrequested });

        var lookup = await service.GetTagsForFilesAsync(
            new[] { multi, shared, untagged, @"C:\never\indexed.png" });

        lookup.Should().HaveCount(3, "the unrequested indexed file and the unindexed path are both absent");
        lookup[Path.GetFullPath(multi)].IsNsfw.Should().BeFalse();
        lookup[Path.GetFullPath(multi)].Tags.Should().BeEquivalentTo(new[] { "dog", "outdoor", "sunset" });
        lookup[Path.GetFullPath(shared)].IsNsfw.Should().BeTrue();
        lookup[Path.GetFullPath(shared)].Tags.Should().BeEquivalentTo(new[] { "dog" },
            "sharing the 'dog' tag with another file must not pull that file's other tags across");
        lookup[Path.GetFullPath(untagged)].Tags.Should().BeEmpty();
        lookup[Path.GetFullPath(untagged)].IsNsfw.Should().BeFalse(
            "an indexed file with no tags still has a row and still reports its rating — an inner join would have dropped it");
        lookup.Should().NotContainKey(Path.GetFullPath(unrequested));
    }

    [Fact]
    public async Task GetTagsForFilesAsync_DoesNotMaterializeATrackedEntityGraph()
    {
        // This lookup runs on the gallery-load path for every visible tile. The
        // Include/ThenInclude version it replaced tracked every index row, every
        // assignment and every tag it touched — hundreds of thousands of
        // entities for a fully indexed gallery, on the same thread that once
        // froze the window for ~20s (issue #397).
        var pathA = CreateFakeImage("track-a.png", width: 1);
        var pathB = CreateFakeImage("track-b.png", width: 2);
        var tagging = TaggerByWidth(w => ImageTagResult.Succeeded(
            new[] { new ImageTagScore($"tag{w}", 0.9f), new ImageTagScore("common", 0.5f) },
            "general", 0.9f, isNsfw: false));
        await new TagIndexService(new SingleDbContextFactory(_options), tagging.Object)
            .BuildIndexAsync(new[] { pathA, pathB });

        var trackedAtDispose = new List<int>();
        var probed = new TagIndexService(
            new TrackingProbeDbContextFactory(_options, trackedAtDispose), tagging.Object);

        var lookup = await probed.GetTagsForFilesAsync(new[] { pathA, pathB });

        lookup.Should().HaveCount(2, "the assertion below is only meaningful if the query actually returned rows");
        lookup[Path.GetFullPath(pathA)].Tags.Should().BeEquivalentTo(new[] { "tag1", "common" });
        trackedAtDispose.Should().ContainSingle().Which.Should().Be(0,
            "the tile lookup projects to plain values and must leave the change tracker empty");
    }

    [Fact]
    public async Task RemoveIndexEntriesAsync_DeletesTheRowsForTheGivenPaths_AndLeavesTheRest()
    {
        // Nothing else prunes the index, so a file deleted (or moved away)
        // through the gallery would otherwise keep inflating "N / M indexed"
        // and keep feeding the tag cloud forever.
        var kept = CreateFakeImage("keep.png", width: 1);
        var removed = CreateFakeImage("remove.png", width: 2);
        var tagging = TaggerByWidth(w => ImageTagResult.Succeeded(
            new[] { new ImageTagScore($"tag{w}", 0.9f) }, "general", 0.9f, isNsfw: false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { kept, removed });
        (await service.GetIndexedCountAsync()).Should().Be(2);

        var deleted = await service.RemoveIndexEntriesAsync(new[] { removed });

        deleted.Should().Be(1, "the caller decrements its indexed counter by whatever this reports");
        (await service.GetIndexedCountAsync()).Should().Be(1);
        (await service.GetTagsForFilesAsync(new[] { removed })).Should().BeEmpty(
            "the pruned file must look unindexed afterwards");
        (await service.GetTagsForFilesAsync(new[] { kept })).Should().ContainKey(Path.GetFullPath(kept));
    }

    [Fact]
    public async Task RemoveIndexEntriesAsync_AlsoRemovesTheTagAssignments()
    {
        // The prune deletes with ExecuteDeleteAsync, which does no client-side
        // cascade — it leans entirely on the ON DELETE CASCADE the migration
        // emits and SQLite enforcing foreign keys. Verify that empirically
        // rather than trusting the configuration, because orphaned assignment
        // rows would keep the deleted file's tags alive in the tag cloud.
        var path = CreateFakeImage("cascade.png");
        var tagging = TaggerByWidth(_ => ImageTagResult.Succeeded(
            new[] { new ImageTagScore("lonelytag", 0.9f) }, "general", 0.9f, isNsfw: false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);
        await service.BuildIndexAsync(new[] { path });

        await using (var seeded = new DiffusionNexusCoreDbContext(_options))
        {
            (await seeded.ImageMediaTagAssignments.CountAsync()).Should().Be(1);
        }

        await service.RemoveIndexEntriesAsync(new[] { path });

        await using var context = new DiffusionNexusCoreDbContext(_options);
        (await context.ImageMediaTagAssignments.CountAsync()).Should().Be(0,
            "the assignment rows must cascade away with their index row");
        (await service.GetTagCloudAsync()).Should().BeEmpty(
            "a tag with no surviving assignment must drop out of the cloud");
    }

    [Fact]
    public async Task RemoveIndexEntriesAsync_IgnoresUnknownPaths_AndAnEmptyList()
    {
        // Callers fire this for every removed gallery tile without first
        // checking whether the file was ever indexed, so "no such row" has to
        // be a no-op rather than an error.
        var tagging = TaggerByWidth(_ => ImageTagResult.Succeeded(
            Array.Empty<ImageTagScore>(), "general", 0.9f, isNsfw: false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        (await service.RemoveIndexEntriesAsync(Array.Empty<string>())).Should().Be(0);
        (await service.RemoveIndexEntriesAsync(new[] { Path.Combine(_imagesDir, "never-indexed.png") }))
            .Should().Be(0);
    }

    [Fact]
    public async Task BuildIndexAsync_ReportsTheModelDownloadAsAStatusMessage_NotAsACurrentFile()
    {
        // The download is a phase, not a file. Reporting it through CurrentFile
        // (which is what a UI treats as "we are chewing through files now")
        // left the overlay frozen on "Indexing images… 0/N" for the whole
        // multi-minute, 379 MB download.
        var path = CreateFakeImage("download-phase.png");
        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.NotDownloaded);
        tagging.Setup(t => t.DownloadModelAsync(It.IsAny<IProgress<ModelDownloadProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<byte[]>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(Array.Empty<ImageTagScore>(), "general", 0.9f, isNsfw: false));
        var service = new TagIndexService(new SingleDbContextFactory(_options), tagging.Object);

        var reports = new List<TagIndexBuildProgress>();
        await service.BuildIndexAsync(new[] { path }, new SynchronousProgress<TagIndexBuildProgress>(reports.Add));

        var download = reports.Should().ContainSingle(r => r.StatusMessage != null).Subject;
        download.StatusMessage.Should().Contain("Downloading");
        download.CurrentFile.Should().BeNull("CurrentFile is reserved for real file paths");
        reports.Should().Contain(r => r.CurrentFile == Path.GetFullPath(path),
            "per-file progress still reports the path it is working on");
    }

    /// <summary>
    /// <see cref="Progress{T}"/> hops to the captured synchronization context
    /// (or the thread pool), which makes a report list racy to assert on.
    /// This one invokes the handler inline, on the reporting thread.
    /// </summary>
    private sealed class SynchronousProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SynchronousProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
