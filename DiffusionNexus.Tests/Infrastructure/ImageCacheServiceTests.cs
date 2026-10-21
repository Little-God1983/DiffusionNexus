using System.Net;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Service.Services.Imaging;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace DiffusionNexus.Tests.Infrastructure;

public class ImageCacheServiceTests : IDisposable
{
    private readonly string _cacheDir = Path.Combine(Path.GetTempPath(), "dn-imagecache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The default options cache the full image after building the thumbnail, which rewinds the
    /// downloaded stream. Decoding must therefore leave that stream open.
    /// </summary>
    [Fact]
    public async Task DownloadAndCacheAsync_WithDefaultOptions_BuildsTheThumbnailAndCachesTheFullImage()
    {
        var png = CreatePng(600, 400);
        using var http = new HttpClient(new StubHandler(png));
        var service = new ImageCacheService(_cacheDir, http);

        var result = await service.DownloadAndCacheAsync("https://example.invalid/image.png", ImageCacheOptions.Default);

        result.Success.Should().BeTrue(result.ErrorMessage);
        result.OriginalWidth.Should().Be(600);
        result.OriginalHeight.Should().Be(400);
        result.ThumbnailData.Should().NotBeNullOrEmpty();
        result.LocalCachePath.Should().NotBeNullOrEmpty();
        File.ReadAllBytes(service.GetFullCachePath(result.LocalCachePath!)).Should().Equal(png);
    }

    private static byte[] CreatePng(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = 200; rgba[i + 1] = 120; rgba[i + 2] = 40; rgba[i + 3] = 255;
        }
        using var bmp = SkiaPixels.FromRgbaBytes(rgba, width, height);
        return SkiaPixels.EncodePng(bmp);
    }

    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}
