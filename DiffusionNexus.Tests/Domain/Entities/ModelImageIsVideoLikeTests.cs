using DiffusionNexus.Domain.Entities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Entities;

/// <summary>
/// Covers <see cref="ModelImage.IsVideoLike"/> — the one place the app decides whether a preview
/// record describes a video. <see cref="ModelImage.IsVideo"/>, the tile's own video test, and the
/// SQL-side candidate ranking all route through it.
/// </summary>
/// <remarks>
/// Task 1 extracted this from <c>IsVideo</c> behaviour-preservingly, which at the time meant
/// ignoring the URL entirely. The Plan B review overturned that: the extracted predicate became the
/// gate on <c>ThumbnailProvider</c>'s rung 3, and a legacy row with a null <c>MediaType</c> and an
/// <c>.mp4</c> URL that misses rung 3 falls to rung 4, which GETs the URL and buffers the whole clip
/// before <c>LooksLikeVideo</c> discards it. So the URL extension is now consulted — but only when
/// <c>MediaType</c> says nothing at all, exactly as <c>ModelTileViewModel.IsVideoPreview</c> already
/// did before it was folded in here.
/// <para>
/// Ranking parity with <see cref="ModelVersion.PrimaryImage"/> survives the change for free: the
/// entity property and the candidate rank are the same call, so whatever this returns, both sides
/// see it.
/// </para>
/// </remarks>
public class ModelImageIsVideoLikeTests
{
    private const string CdnVideo = "https://image.civitai.com/abc/width=450/clip.mp4";

    [Fact]
    public void IsVideoLike_TrueForVideoMediaType()
        => ModelImage.IsVideoLike("video", null).Should().BeTrue();

    [Fact]
    public void IsVideoLike_IsCaseInsensitive()
        => ModelImage.IsVideoLike("VIDEO", null).Should().BeTrue();

    /// <summary>
    /// The extension fallback folds case too � a legacy row storing "clip.MP4" is the same video
    /// as one storing "clip.mp4".
    /// </summary>
    [Fact]
    public void IsVideoLike_UrlExtensionIsCaseInsensitive()
        => ModelImage.IsVideoLike(null, "https://image.civitai.com/x/abc/width=450/clip.MP4").Should().BeTrue();

    /// <summary>
    /// A recorded media type is an answer, and the URL does not get to argue with it. The extension
    /// fallback exists for rows that carry no answer, not to second-guess the ones that do.
    /// </summary>
    [Fact]
    public void IsVideoLike_IgnoresUrlWhenMediaTypeSaysImage()
        => ModelImage.IsVideoLike("image", CdnVideo).Should().BeFalse();

    [Fact]
    public void IsVideoLike_FalseForNullMediaType()
        => ModelImage.IsVideoLike(null, null).Should().BeFalse();

    /// <summary>
    /// The legacy sidecar row the review was about: no <c>type</c> field, so no <c>MediaType</c>,
    /// and only the URL left to say what it is.
    /// </summary>
    [Theory]
    [InlineData("https://image.civitai.com/abc/width=450/clip.mp4")]
    [InlineData("https://image.civitai.com/abc/width=450/clip.webm")]
    [InlineData("https://example.com/a/clip.mov")]
    [InlineData("https://example.com/a/clip.avi")]
    [InlineData("https://example.com/a/clip.mkv")]
    public void IsVideoLike_TrueForAVideoExtensionWhenMediaTypeIsSilent(string url)
        => ModelImage.IsVideoLike(null, url).Should().BeTrue();

    [Fact]
    public void IsVideoLike_FalseForAStillUrlWhenMediaTypeIsSilent()
        => ModelImage.IsVideoLike(null, "https://image.civitai.com/abc/width=450/still.jpeg").Should().BeFalse();

    /// <summary>
    /// The database holds URLs nothing guarantees are parseable — a truncated download, a legacy
    /// relative path, a malformed bracket. One is simply not known to be a video.
    /// </summary>
    [Theory]
    [InlineData("https://")]
    [InlineData("images/preview.mp4")]
    [InlineData("https://[unclosed/a.mp4")]
    [InlineData("")]
    public void IsVideoLike_FalseForAUrlThatCannotBeParsed(string url)
    {
        var act = () => ModelImage.IsVideoLike(null, url);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void IsVideo_PropertyDelegatesToIsVideoLike()
    {
        var image = new ModelImage { MediaType = "video" };
        image.IsVideo.Should().BeTrue();

        image.MediaType = "image";
        image.IsVideo.Should().BeFalse();

        image.MediaType = null;
        image.Url = CdnVideo;
        image.IsVideo.Should().BeTrue("the property carries the URL fallback too — it is the same call");
    }
}
