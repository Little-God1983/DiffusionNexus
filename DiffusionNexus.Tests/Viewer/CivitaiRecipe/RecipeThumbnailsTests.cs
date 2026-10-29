using Avalonia.Media.Imaging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #667: card thumbnails come from the app's bounded thumbnail LRU at card width; an original replaced
/// under the same name (a re-paste) is decoded again instead of showing the old picture.
/// </summary>
public sealed class RecipeThumbnailsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dn-recipe-thumbs").FullName;
    private readonly Mock<IThumbnailService> _cache = new();

    public RecipeThumbnailsTests()
    {
        _cache.Setup(c => c.LoadThumbnailAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Bitmap?)null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Original()
    {
        var path = Path.Combine(_dir, "original.png");
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task DecodesAtCardWidth_ThroughTheSharedCache()
    {
        var path = Original();

        await new RecipeThumbnails(_cache.Object).LoadAsync(path, default);

        _cache.Verify(c => c.LoadThumbnailAsync(path, RecipeThumbnails.DecodeWidth, It.IsAny<CancellationToken>()), Times.Once);
        RecipeThumbnails.DecodeWidth.Should().BeLessThanOrEqualTo(340, "a card is 168 px wide; the full original can be 50 MB");
    }

    [Fact]
    public async Task UnchangedFile_KeepsItsCacheEntry()
    {
        var path = Original();
        var thumbnails = new RecipeThumbnails(_cache.Object);

        await thumbnails.LoadAsync(path, default);
        await thumbnails.LoadAsync(path, default);

        _cache.Verify(c => c.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ReplacedFile_IsDecodedAgain()
    {
        var path = Original();
        var thumbnails = new RecipeThumbnails(_cache.Object);
        await thumbnails.LoadAsync(path, default);

        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        await thumbnails.LoadAsync(path, default);

        _cache.Verify(c => c.Invalidate(path), Times.Once);
        _cache.Verify(c => c.LoadThumbnailAsync(path, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
