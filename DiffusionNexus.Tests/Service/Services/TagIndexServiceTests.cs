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

    private string CreateFakeImage(string name)
    {
        var path = Path.Combine(_imagesDir, name);
        using var image = new Image<Rgba32>(4, 4);
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
}
