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

    [Fact]
    public void ModelImage_IsNsfw_IsDerivedFromItsRating()
    {
        var image = new ModelImage { Url = "https://image.civitai.com/x/a.jpeg" };
        image.IsNsfw.Should().BeTrue("an unrated Civitai image is not known to be safe");

        image.NsfwLevel = NsfwLevel.PG13;
        image.IsNsfw.Should().BeFalse();

        image.NsfwLevel = NsfwLevel.XXX;
        image.IsNsfw.Should().BeTrue();
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
