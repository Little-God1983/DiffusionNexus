using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.Sync.Service.Thumbnails;

/// <summary>
/// Covers <see cref="ThumbnailCodec"/> — the single decode/resize/encode path (450px JPEG)
/// shared by the thumbnail provider and writer, plus the video-magic-byte guard moved from
/// <c>ModelTileViewModel.IsVideoData</c>.
/// </summary>
public class ThumbnailCodecTests
{
    private static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Teal);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void Encode_ShrinksWideImagesToTargetWidthAsJpeg()
    {
        var payload = ThumbnailCodec.Encode(Png(900, 600));
        payload.Should().NotBeNull();
        payload!.MimeType.Should().Be("image/jpeg");
        payload.Width.Should().Be(ThumbnailCodec.TargetWidth);
        payload.Height.Should().Be(300);
        SKBitmap.Decode(payload.Data).Should().NotBeNull("the stored bytes must round-trip");
    }

    [Fact]
    public void Encode_KeepsNarrowImagesAtTheirSize()
    {
        var payload = ThumbnailCodec.Encode(Png(200, 300));
        payload!.Width.Should().Be(200);
        payload.Height.Should().Be(300);
    }

    [Fact]
    public void Encode_ReturnsNullForUndecodableBytes()
        => ThumbnailCodec.Encode([0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01]).Should().BeNull();

    [Fact]
    public void Encode_ExtremeAspectRatioStillProducesAThumbnail()
    {
        // 9000x10 scales to a nominal height of round(10 * 450/9000) == 0; SKBitmap.Resize
        // returns null for a zero-height target, which must not be mistaken for NotDecodable.
        var payload = ThumbnailCodec.Encode(Png(9000, 10));
        payload.Should().NotBeNull();
        payload!.Width.Should().Be(450);
        payload.Height.Should().Be(1);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, true)]  // ....ftypisom
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 }, true)]                             // EBML (webm)
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }, false)]                            // PNG
    public void LooksLikeVideo_RecognisesContainerMagic(byte[] head, bool expected)
        => ThumbnailCodec.LooksLikeVideo(head).Should().Be(expected);
}
