using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Thumbnails;

/// <summary>
/// Covers <see cref="CivitaiImageUrls"/> — the shared Civitai CDN URL transform rewriter,
/// generalised from <c>CivitaiResultViewModel.RewriteToResizedImageUrl</c> so the sync
/// pipeline and the browser UI never drift on transform-segment logic again.
/// </summary>
public class CivitaiImageUrlsTests
{
    private const string Base = "https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/abc-123";

    [Theory]
    [InlineData($"{Base}/width=450/img.jpeg", $"{Base}/width=450/img.jpeg")]            // already right
    [InlineData($"{Base}/original=true/img.jpeg", $"{Base}/width=450/img.jpeg")]        // replaces existing transform
    [InlineData($"{Base}/img.jpeg", $"{Base}/width=450/img.jpeg")]                      // inserts when absent
    [InlineData($"{Base}/width=300/img.jpeg?token=x", $"{Base}/width=450/img.jpeg?token=x")] // query preserved
    public void ToThumbnailUrl_NormalisesTheTransformSegment(string input, string expected)
        => CivitaiImageUrls.ToThumbnailUrl(input).Should().Be(expected);

    [Fact]
    public void ToThumbnailUrl_LeavesNonCdnUrlsAlone()
        => CivitaiImageUrls.ToThumbnailUrl("https://example.com/a/b.png").Should().Be("https://example.com/a/b.png");

    [Theory]
    [InlineData($"{Base}/width=450/clip.mp4", $"{Base}/width=450,anim=false,transcode=true/clip.jpeg")]
    [InlineData($"{Base}/clip.webm", $"{Base}/width=450,anim=false,transcode=true/clip.jpeg")]
    [InlineData($"{Base}/transcode=true,width=320/clip.mp4", $"{Base}/width=450,anim=false,transcode=true/clip.jpeg")]
    public void ToVideoPosterUrl_RewritesTransformAndExtension(string input, string expected)
        => CivitaiImageUrls.ToVideoPosterUrl(input).Should().Be(expected);

    [Fact]
    public void ToVideoPosterUrl_ReturnsNullForNonCdnUrls()
        => CivitaiImageUrls.ToVideoPosterUrl("https://example.com/v.mp4").Should().BeNull();

    [Fact]
    public void ToThumbnailUrl_NullAndWhitespacePassThrough()
    {
        CivitaiImageUrls.ToThumbnailUrl(null).Should().BeNull();
        CivitaiImageUrls.ToThumbnailUrl("  ").Should().Be("  ");
    }
}
