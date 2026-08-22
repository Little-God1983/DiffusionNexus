using System.Net;
using System.Net.Http.Headers;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.Sync.Service.Thumbnails;

/// <summary>
/// Covers <see cref="ThumbnailProvider"/> — the §4.3 resolution ladder that turns one image
/// record into thumbnail bytes. The assertions that matter most are about what is <i>not</i>
/// fetched: a video must never be downloaded without permission, and a <c>file://</c> preview
/// must never touch the network.
/// </summary>
public class ThumbnailProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            Urls.Add(req.RequestUri!.ToString());
            return Task.FromResult(respond(req));
        }
    }

    private static ThumbnailProvider Provider(FakeHandler handler) => new(new HttpClient(handler));

    private static byte[] Png()
    {
        using var bmp = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Teal);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    /// <summary>MP4 magic: a "ftyp" box at offset 4 — what the CDN returns when it ignores a poster request.</summary>
    private static byte[] Mp4() => [0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m'];

    private static HttpResponseMessage Bytes(byte[] data, string mime = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue(mime) } } };

    [Fact]
    public async Task Image_FetchesTheWidth450UrlAndEncodes()
    {
        var handler = new FakeHandler(_ => Bytes(Png()));

        var result = await Provider(handler).ProduceAsync(new(
            "https://image.civitai.com/x/abc/width=1024/pic.jpeg", "image", null));

        result.Succeeded.Should().BeTrue();
        result.Payload!.MimeType.Should().Be("image/jpeg");
        handler.Urls.Should().ContainSingle().Which.Should().Contain("width=450");
    }

    [Fact]
    public async Task Http404_IsAHardFailure()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Provider(handler).ProduceAsync(new(
            "https://image.civitai.com/x/abc/width=1024/pic.jpeg", "image", null));

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().Be(ThumbnailFailureReason.Http404);
        ThumbnailFailureReason.IsHardFailure(result.Failure).Should().BeTrue();
    }

    [Fact]
    public async Task ServerError_IsSoftHttpError()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await Provider(handler).ProduceAsync(new(
            "https://image.civitai.com/x/abc/width=1024/pic.jpeg", "image", null));

        result.Failure.Should().Be(ThumbnailFailureReason.HttpError);
        ThumbnailFailureReason.IsHardFailure(result.Failure).Should().BeFalse("a 503 is worth retrying");
    }

    [Fact]
    public async Task Video_FetchesOnlyThePosterUrl_NeverTheVideo()
    {
        var handler = new FakeHandler(_ => Bytes(Png()));

        var result = await Provider(handler).ProduceAsync(new(
            "https://image.civitai.com/x/abc/width=450/clip.mp4", "video", null));

        result.Succeeded.Should().BeTrue();
        handler.Urls.Should().ContainSingle().Which.Should().Contain("anim=false,transcode=true").And.EndWith("clip.jpeg");
    }

    [Fact]
    public async Task Video_PosterFailureWithoutPermissionIsVideoNoPoster()
    {
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await Provider(handler).ProduceAsync(new(
            "https://image.civitai.com/x/abc/width=450/clip.mp4", "video", null));

        result.Failure.Should().Be(ThumbnailFailureReason.VideoNoPoster);
        handler.Urls.Should().ContainSingle().Which.Should().EndWith("clip.jpeg",
            "without AllowVideoDownload the multi-MB original is never requested");
    }

    [Fact]
    public async Task NonCdnVideo_WithoutPermissionIsVideoNoPoster_NoHttpAtAll()
    {
        var handler = new FakeHandler(_ => Bytes(Png()));

        var result = await Provider(handler).ProduceAsync(new(
            "https://example.com/v.mp4", "video", null));

        result.Failure.Should().Be(ThumbnailFailureReason.VideoNoPoster);
        handler.Urls.Should().BeEmpty("there is no poster transform to derive off the CDN");
    }

    [Fact]
    public async Task ImageBytesThatAreVideo_RetryViaPosterUrl()
    {
        var calls = 0;
        var handler = new FakeHandler(_ => calls++ == 0 ? Bytes(Mp4(), "video/mp4") : Bytes(Png()));

        var result = await Provider(handler).ProduceAsync(new(
            "https://image.civitai.com/x/abc/width=450/clip.jpeg", "image", null));

        result.Succeeded.Should().BeTrue();
        handler.Urls.Should().HaveCount(2);
        handler.Urls[1].Should().Contain("anim=false,transcode=true");
    }

    [Fact]
    public async Task FileUrl_ReadsDiskAndNeverHttp()
    {
        var directory = Directory.CreateTempSubdirectory("dn_thumb_provider_").FullName;
        var preview = Path.Combine(directory, "model.preview.png");
        await File.WriteAllBytesAsync(preview, Png());
        var handler = new FakeHandler(_ => Bytes(Png()));

        try
        {
            var result = await Provider(handler).ProduceAsync(new(
                LocalPreviewFiles.FileUrlPrefix + preview, "image", null));

            result.Succeeded.Should().BeTrue();
            handler.Urls.Should().BeEmpty("a local preview is never fetched over the network");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task FileUrl_MissingFallsBackToSiblingThenLocalFileMissing()
    {
        var directory = Directory.CreateTempSubdirectory("dn_thumb_provider_").FullName;
        var model = Path.Combine(directory, "model.safetensors");
        var sibling = Path.Combine(directory, "model.preview.png");
        await File.WriteAllBytesAsync(model, [0x00]);
        await File.WriteAllBytesAsync(sibling, Png());
        var handler = new FakeHandler(_ => Bytes(Png()));
        var missingUrl = LocalPreviewFiles.FileUrlPrefix + Path.Combine(directory, "gone.png");

        try
        {
            var viaSibling = await Provider(handler).ProduceAsync(new(missingUrl, "image", model));
            viaSibling.Succeeded.Should().BeTrue("the sibling preview stands in for the recorded path");

            File.Delete(sibling);
            var nothingLeft = await Provider(handler).ProduceAsync(new(missingUrl, "image", model));
            nothingLeft.Failure.Should().Be(ThumbnailFailureReason.LocalFileMissing);
            handler.Urls.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task UserThumbnailScheme_IsUnsupported()
    {
        var handler = new FakeHandler(_ => Bytes(Png()));

        var result = await Provider(handler).ProduceAsync(new(
            LocalPreviewFiles.UserThumbnailScheme + "42", "image", null));

        result.Failure.Should().Be(ThumbnailFailureReason.UnsupportedScheme);
        handler.Urls.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        var handler = new FakeHandler(_ => throw new OperationCanceledException());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions
            .Awaiting(() => Provider(handler).ProduceAsync(
                new("https://image.civitai.com/x/abc/width=1024/pic.jpeg", "image", null), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>("a cancelled run is not a thumbnail failure");
    }
}
