using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public class SorterCategoryResolverTests
{
    [Fact]
    public void UserCategoryOverrideWinsOverTags()
        => SorterCategoryResolver.Resolve(CivitaiCategory.Style, new[] { "character" })
            .Should().Be(CivitaiCategory.Style);

    [Fact]
    public void InfersFromFirstMatchingSingleWordTagCaseInsensitive()
        => SorterCategoryResolver.Resolve(null, new[] { "anime", "STYLE" })
            .Should().Be(CivitaiCategory.Style);

    [Fact]
    public void MultiWordTagDoesNotParseMatchingDownloaderBehavior()
        // Downloader parity (CivitaiResultViewModel.InferCategoryFromTags): "base model"
        // normalizes to "base_model", which does not parse to CivitaiCategory.BaseModel.
        // Sorted files must land exactly where downloads land, so we mirror this bug-for-bug.
        => SorterCategoryResolver.Resolve(null, new[] { "base model" })
            .Should().Be(CivitaiCategory.Unknown);

    [Fact]
    public void NullAndWhitespaceTagsAreSkipped()
        => SorterCategoryResolver.Resolve(null, new string?[] { null, "  ", "vehicle" })
            .Should().Be(CivitaiCategory.Vehicle);

    [Fact]
    public void NoMatchYieldsUnknown()
        => SorterCategoryResolver.Resolve(null, new[] { "anime", "photorealistic" })
            .Should().Be(CivitaiCategory.Unknown);

    [Fact]
    public void UnknownUserCategoryFallsThroughToTags()
        => SorterCategoryResolver.Resolve(CivitaiCategory.Unknown, new[] { "poses" })
            .Should().Be(CivitaiCategory.Poses);

    [Theory]
    [InlineData(CivitaiCategory.BaseModel, "Base Model")]
    [InlineData(CivitaiCategory.Character, "Character")]
    [InlineData(CivitaiCategory.Unknown, "Unknown")]
    public void ToFolderNameMatchesDownloaderDisplayConvention(CivitaiCategory category, string expected)
        => SorterCategoryResolver.ToFolderName(category).Should().Be(expected);
}
