using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Domain;

public sealed class ContentRatingPolicyTests
{
    [Theory]
    [InlineData("general")]
    [InlineData("sensitive")] // WD14 assigns this to much completely ordinary art — it must not badge/hide SFW images
    [InlineData("General")]   // labels come from a CSV; casing must not matter
    [InlineData("SENSITIVE")]
    public void SafeRatings_AreNotNsfw(string label)
        => ContentRatingPolicy.IsNsfw(label).Should().BeFalse();

    [Theory]
    [InlineData("questionable")]
    [InlineData("explicit")]
    [InlineData(null)]        // unrated/corrupt rows fail closed
    [InlineData("")]
    [InlineData("garbage")]
    public void EverythingElse_IsNsfw(string? label)
        => ContentRatingPolicy.IsNsfw(label).Should().BeTrue();
}
