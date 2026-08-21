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

    [Fact]
    public void UndefinedUserCategoryOverrideFallsThroughToTags()
        // Same hole 4.5 closed for tags: an undefined stored integer (e.g. corrupt/legacy
        // AppSettings data) must not name a directory by its raw number.
        => SorterCategoryResolver.Resolve((CivitaiCategory)2000, new[] { "style" })
            .Should().Be(CivitaiCategory.Style);

    [Theory]
    [InlineData(CivitaiCategory.BaseModel, "Base Model")]
    [InlineData(CivitaiCategory.Character, "Character")]
    [InlineData(CivitaiCategory.Unknown, "Unknown")]
    public void ToFolderNameMatchesDownloaderDisplayConvention(CivitaiCategory category, string expected)
        => SorterCategoryResolver.ToFolderName(category).Should().Be(expected);

    [Theory]
    // Review 4.5, verified against the real enum (Unknown=0, Character=1, Style=2,
    // Celebrity=3, Concept=4, Clothing=5): Enum.TryParse accepts numeric forms and
    // comma-separated lists. "2000" is a real Civitai style tag and parsed to the
    // UNDEFINED value 2000 — ToFolderName then returned "2000" and the sorter created
    // and populated a directory literally named 2000.
    [InlineData("2000")]
    [InlineData("5")]
    [InlineData("1")]
    [InlineData("-3")]
    [InlineData("character,style")]
    [InlineData("2,4")]
    public void NumericAndCommaSeparatedTagsAreNotCategories(string tag)
        => SorterCategoryResolver.Resolve(null, new[] { tag }).Should().Be(CivitaiCategory.Unknown);

    [Fact]
    public void ARealCategoryTagAfterANumericOneStillWins()
        => SorterCategoryResolver.Resolve(null, new[] { "2000", "5", "concept" })
            .Should().Be(CivitaiCategory.Concept);

    [Fact]
    public void InferFolderNameIsTheDownloaderContractNullWhenUnresolved()
    {
        // This is what CivitaiResultViewModel/DownloadLoraDialogViewModel now call, and
        // DownloadDestinationViewModel.BuildTargetDirectory omits the segment for null.
        SorterCategoryResolver.InferFolderName(new[] { "anime", "photorealistic" }).Should().BeNull();
        SorterCategoryResolver.InferFolderName(new[] { "style" }).Should().Be("Style");
        SorterCategoryResolver.InferFolderName(new[] { "basemodel" }).Should().Be("Base Model");
        SorterCategoryResolver.InferFolderName(new[] { "2000" }).Should().BeNull();
    }

    [Theory]
    [InlineData(CivitaiCategory.Unknown, null)]
    [InlineData(CivitaiCategory.Style, "Style")]
    public void ToFolderNameOrNullMapsUnknownToNoSegment(CivitaiCategory category, string? expected)
        => SorterCategoryResolver.ToFolderNameOrNull(category).Should().Be(expected);
}
