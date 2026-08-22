using DiffusionNexus.Domain.Entities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Entities;

/// <summary>
/// Covers <see cref="ModelImage.IsVideoLike"/> — extracted from the <see cref="ModelImage.IsVideo"/>
/// property so the candidate-selection SQL projection (Plan B) can reuse the exact same rule.
/// The extraction must be behaviour-preserving: today <c>IsVideo</c> only inspects
/// <see cref="ModelImage.MediaType"/> and ignores the URL entirely, so these tests pin that
/// as current behaviour rather than asserting what a URL-extension fallback "should" do.
/// </summary>
public class ModelImageIsVideoLikeTests
{
    [Fact]
    public void IsVideoLike_TrueForVideoMediaType()
        => ModelImage.IsVideoLike("video", null).Should().BeTrue();

    [Fact]
    public void IsVideoLike_IsCaseInsensitive()
        => ModelImage.IsVideoLike("VIDEO", null).Should().BeTrue();

    [Fact]
    public void IsVideoLike_IgnoresUrlWhenMediaTypeSaysImage()
        // Pins today's ModelImage.IsVideo behaviour: the URL is not consulted at all.
        => ModelImage.IsVideoLike("image", "x.mp4").Should().BeFalse();

    [Fact]
    public void IsVideoLike_FalseForNullMediaType()
        => ModelImage.IsVideoLike(null, null).Should().BeFalse();

    [Fact]
    public void IsVideo_PropertyDelegatesToIsVideoLike()
    {
        var image = new ModelImage { MediaType = "video" };
        image.IsVideo.Should().BeTrue();

        image.MediaType = "image";
        image.IsVideo.Should().BeFalse();
    }
}
