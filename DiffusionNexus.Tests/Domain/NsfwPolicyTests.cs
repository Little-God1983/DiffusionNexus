using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Domain;

/// <summary>
/// The one definition of "safe" for Civitai-rated content (#631): PG and PG-13 are safe,
/// anything carrying R, X, XXX or Blocked is not, and an unrated Civitai image fails closed.
/// </summary>
public sealed class NsfwPolicyTests
{
    [Theory]
    [InlineData(NsfwLevel.PG)]
    [InlineData(NsfwLevel.PG13)]
    [InlineData(NsfwLevel.PG | NsfwLevel.PG13)]
    public void SafeLevels_AreSafe(NsfwLevel level)
        => NsfwPolicy.IsSafe(level).Should().BeTrue();

    [Theory]
    [InlineData(NsfwLevel.None)] // unrated fails closed
    [InlineData(NsfwLevel.R)]
    [InlineData(NsfwLevel.X)]
    [InlineData(NsfwLevel.XXX)]
    [InlineData(NsfwLevel.Blocked)]
    [InlineData(NsfwLevel.PG | NsfwLevel.R)] // any unsafe bit makes the whole rating unsafe
    public void EverythingElse_IsNotSafe(NsfwLevel level)
        => NsfwPolicy.IsSafe(level).Should().BeFalse();

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, false)]
    [InlineData(16, false)]
    [InlineData(0, false)]
    [InlineData(null, false)]
    public void RawCivitaiLevels_UseTheSameRule(int? raw, bool safe)
        => NsfwPolicy.IsSafe(raw).Should().Be(safe);

    [Theory]
    [InlineData("https://image.civitai.com/x/a.jpeg")]
    [InlineData("http://example.com/a.png")]
    public void UnratedRemoteImage_IsNsfw(string url)
        => NsfwPolicy.IsNsfwImage(NsfwLevel.None, url).Should().BeTrue();

    [Theory]
    [InlineData("file:///E:/Loras/a.preview.png")]
    [InlineData(ModelImage.UserThumbnailScheme + "abc")]
    public void UnratedLocalOrUserImage_IsNotNsfw(string url)
        => NsfwPolicy.IsNsfwImage(NsfwLevel.None, url).Should().BeFalse();

    [Theory]
    [InlineData(NsfwLevel.PG13, false)]
    [InlineData(NsfwLevel.R, true)]
    public void RatedImage_FollowsItsRating_WhateverItsSource(NsfwLevel level, bool nsfw)
    {
        NsfwPolicy.IsNsfwImage(level, "https://image.civitai.com/x/a.jpeg").Should().Be(nsfw);
        NsfwPolicy.IsNsfwImage(level, "file:///E:/Loras/a.png").Should().Be(nsfw);
    }

    /// <summary>
    /// A local preview file beside a LoRA is, for a Civitai version, a copy one of the downloaders
    /// saved from its gallery — often the X-rated cover. Next to Civitai pictures it is judged like
    /// them (unknown = not safe); a local-only version has nothing else to show and keeps it. A
    /// thumbnail the user uploaded is their choice either way.
    /// </summary>
    [Fact]
    public void UnratedLocalPreview_IsNsfw_OnlyBesideCivitaiImages()
    {
        const string local = "file:///E:/Loras/a.preview.png";
        NsfwPolicy.IsNsfwImage(NsfwLevel.None, local, versionHasCivitaiImages: true).Should().BeTrue();
        NsfwPolicy.IsNsfwImage(NsfwLevel.None, local, versionHasCivitaiImages: false).Should().BeFalse();
        NsfwPolicy.IsNsfwImage(NsfwLevel.None, ModelImage.UserThumbnailScheme + "x", versionHasCivitaiImages: true)
            .Should().BeFalse();
    }

    private static ModelImage Img(int id, string url, NsfwLevel level = NsfwLevel.None, string mediaType = "image")
        => new() { Id = id, Url = url, NsfwLevel = level, MediaType = mediaType };

    private static ModelVersion Version(params ModelImage[] images)
    {
        var version = new ModelVersion();
        foreach (var image in images) version.Images.Add(image);
        return version;
    }

    [Fact]
    public void PrimaryImage_PrefersARatedSafeCivitaiImage_OverALocalPreview()
    {
        var pg = Img(2, "https://image.civitai.com/x/pg.jpeg", NsfwLevel.PG);
        var version = Version(Img(1, "file:///E:/Loras/a.preview.png"), pg, Img(3, "https://image.civitai.com/x/x.jpeg", NsfwLevel.X));

        version.PrimaryImage.Should().BeSameAs(pg);
        version.IsImageNsfw(pg).Should().BeFalse();
    }

    [Fact]
    public void PrimaryImage_ALocalPreviewBesideOnlyNsfwCivitaiImages_IsShownButJudgedNsfw()
    {
        var local = Img(1, "file:///E:/Loras/a.preview.png");
        var version = Version(local, Img(2, "https://image.civitai.com/x/x.jpeg", NsfwLevel.XXX));

        version.PrimaryImage.Should().BeSameAs(local, "with Show NSFW on, the first still is still the preview");
        version.IsImageNsfw(local).Should().BeTrue();
    }

    [Fact]
    public void PrimaryImage_ALocalOnlyVersion_KeepsItsPreviewAsSafe()
    {
        var local = Img(1, "file:///E:/Loras/a.preview.png");
        var version = Version(local);

        version.PrimaryImage.Should().BeSameAs(local);
        version.IsImageNsfw(local).Should().BeFalse();
    }

    [Fact]
    public void IsImageNsfw_FollowsTheRating_AndUnratedCivitaiImagesAreNsfw()
    {
        var unrated = Img(1, "https://image.civitai.com/x/a.jpeg");
        var version = Version(unrated);
        version.IsImageNsfw(unrated).Should().BeTrue("an unrated Civitai image is not known to be safe");

        unrated.NsfwLevel = NsfwLevel.PG13;
        version.IsImageNsfw(unrated).Should().BeFalse();

        unrated.NsfwLevel = NsfwLevel.XXX;
        version.IsImageNsfw(unrated).Should().BeTrue();
    }

    [Theory]
    [InlineData("None", NsfwLevel.PG)]
    [InlineData("Soft", NsfwLevel.PG13)]
    [InlineData("Mature", NsfwLevel.R)]
    [InlineData("X", NsfwLevel.X)]
    [InlineData("mature", NsfwLevel.R)]
    [InlineData("garbage", NsfwLevel.None)]
    [InlineData(null, NsfwLevel.None)]
    public void LegacyRatingStrings_MapToTheBitmask(string? legacy, NsfwLevel expected)
        => NsfwPolicy.FromLegacyRating(legacy).Should().Be(expected);
}
