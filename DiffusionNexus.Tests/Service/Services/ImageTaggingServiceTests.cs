using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public sealed class ImageTaggingServiceTests
{
    [Fact]
    public void LoadTagList_ParsesNameAndCategory_SkippingHeaderAndBlankLines()
    {
        var csv = "tag_id,name,category,count\n1,general,9,0\n2,sensitive,9,0\n3,1girl,0,412\n\n4,character_a,4,50\n";
        using var reader = new StringReader(csv);

        var result = ImageTaggingService.LoadTagList(reader);

        result.Should().BeEquivalentTo(new[]
        {
            ("general", "9"),
            ("sensitive", "9"),
            ("1girl", "0"),
            ("character_a", "4"),
        }, options => options.WithStrictOrdering());
    }

    [Fact]
    public void LoadTagList_Throws_WhenNoDataRows()
    {
        using var reader = new StringReader("tag_id,name,category,count\n");

        var act = () => ImageTaggingService.LoadTagList(reader);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SelectTagsAndRating_PicksArgmaxRating_AndThresholdsGeneralCharacterTags()
    {
        var tagList = new List<(string Name, string Category)>
        {
            ("general", "9"),
            ("sensitive", "9"),
            ("questionable", "9"),
            ("explicit", "9"),
            ("1girl", "0"),
            ("outdoor", "0"),
            ("dog", "0"),
            ("character_a", "4"),
        };
        var scores = new List<float> { 0.05f, 0.85f, 0.08f, 0.02f, 0.92f, 0.10f, 0.40f, 0.60f };

        var (tags, rating, ratingScore) = ImageTaggingService.SelectTagsAndRating(tagList, scores, tagConfidenceThreshold: 0.35f);

        rating.Should().Be("sensitive");
        ratingScore.Should().Be(0.85f);
        tags.Select(t => t.Name).Should().BeEquivalentTo(new[] { "1girl", "dog", "character_a" });
    }

    [Fact]
    public void SelectTagsAndRating_Throws_WhenScoreCountDoesNotMatchTagListCount()
    {
        var tagList = new List<(string Name, string Category)> { ("general", "9") };
        var scores = new List<float> { 0.1f, 0.2f };

        var act = () => ImageTaggingService.SelectTagsAndRating(tagList, scores, 0.35f);

        act.Should().Throw<InvalidOperationException>();
    }
}
