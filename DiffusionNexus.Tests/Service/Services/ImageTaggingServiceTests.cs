using DiffusionNexus.Service.Services;
using FluentAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public sealed class ImageTaggingServiceTests
{
    private static readonly Rgba32 Red = new(255, 0, 0, 255);

    private static Image<Rgba32> SolidRed(int width, int height) => new(width, height, Red);

    private static bool IsRed(Rgba32 p) => p.R > 200 && p.G < 64 && p.B < 64;

    private static bool IsWhite(Rgba32 p) => p.R > 200 && p.G > 200 && p.B > 200;

    /// <summary>Counts how many rows of the given column hold image content rather than padding.</summary>
    private static int RedRowsInColumn(Image<Rgba32> image, int x)
    {
        var count = 0;
        for (var y = 0; y < image.Height; y++)
            if (IsRed(image[x, y])) count++;
        return count;
    }
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
        // Strict ordering: confirms tags are actually sorted descending by confidence
        // (1girl .92 > character_a .60 > dog .40), not just present as a set.
        tags.Select(t => t.Name).Should().BeEquivalentTo(
            new[] { "1girl", "character_a", "dog" },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public void SelectTagsAndRating_Throws_WhenScoreCountDoesNotMatchTagListCount()
    {
        var tagList = new List<(string Name, string Category)> { ("general", "9") };
        var scores = new List<float> { 0.1f, 0.2f };

        var act = () => ImageTaggingService.SelectTagsAndRating(tagList, scores, 0.35f);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void SelectTagsAndRating_ReturnsNullRating_WhenTagListHasNoRatingCategoryRows()
    {
        var tagList = new List<(string Name, string Category)>
        {
            ("1girl", "0"),
            ("outdoor", "0"),
            ("character_a", "4"),
        };
        var scores = new List<float> { 0.92f, 0.10f, 0.60f };

        var (tags, rating, _) = ImageTaggingService.SelectTagsAndRating(tagList, scores, tagConfidenceThreshold: 0.35f);

        rating.Should().BeNull();
        tags.Select(t => t.Name).Should().BeEquivalentTo(new[] { "1girl", "character_a" });
    }

    // ---- ResizeAndPadToSquare (resize-then-pad preprocessing) ------------

    [Fact]
    public void ResizeAndPadToSquare_ScalesLongEdgeToInputSize_AndCentersOnWhitePadding()
    {
        // 100x50 at size 32 => long edge 100 scales to 32, short edge to 16,
        // leaving 8 rows of white padding above and below.
        using var source = SolidRed(100, 50);

        using var result = ImageTaggingService.ResizeAndPadToSquare(source, 32);

        result.Width.Should().Be(32);
        result.Height.Should().Be(32);
        IsRed(result[16, 16]).Should().BeTrue("the image content is centered");
        IsWhite(result[16, 0]).Should().BeTrue("the top band is white padding");
        IsWhite(result[16, 31]).Should().BeTrue("the bottom band is white padding");
        IsWhite(result[0, 0]).Should().BeTrue();
        RedRowsInColumn(result, 16).Should().Be(16, "aspect ratio is preserved: 50/100 * 32 = 16 rows of content");
    }

    [Fact]
    public void ResizeAndPadToSquare_FillsTheWholeSquare_ForASquareSource()
    {
        using var source = SolidRed(64, 64);

        using var result = ImageTaggingService.ResizeAndPadToSquare(source, 32);

        result.Width.Should().Be(32);
        result.Height.Should().Be(32);
        RedRowsInColumn(result, 16).Should().Be(32, "a square source needs no padding at all");
        IsRed(result[0, 0]).Should().BeTrue();
        IsRed(result[31, 31]).Should().BeTrue();
    }

    [Fact]
    public void ResizeAndPadToSquare_KeepsAtLeastOnePixel_ForExtremeAspectRatios()
    {
        // 4000x50 at size 32 scales the short edge to 0.4px. Without a floor
        // of 1 this would throw on a zero-height resize.
        using var source = SolidRed(4000, 50);

        var act = () => ImageTaggingService.ResizeAndPadToSquare(source, 32);

        using var result = act.Should().NotThrow().Which;
        result.Width.Should().Be(32);
        result.Height.Should().Be(32);
        RedRowsInColumn(result, 16).Should().Be(1);
    }

    [Fact]
    public void ResizeAndPadToSquare_StaysWithinASmallMemoryBudget_ForLargeSources()
    {
        // This is the actual point of resize-then-pad. The old ordering built
        // a max(width, height)² intermediate first: for this 4000x64 source
        // that is a 4000x4000 RGBA buffer (~61 MB), per image, in a loop over
        // a whole gallery — and this app produces multi-thousand-pixel images
        // from its own batch upscaler. Capping the allocator at 16 MB passes
        // only if that square is never built.
        var budgeted = Configuration.Default.Clone();
        budgeted.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 16 });
        using var source = new Image<Rgba32>(budgeted, 4000, 64, Red); // ~1 MB

        var act = () => ImageTaggingService.ResizeAndPadToSquare(source, 32);

        using var result = act.Should().NotThrow(
            "padding to a 4000x4000 square before resizing would blow the 16 MB budget").Which;
        result.Width.Should().Be(32);
        result.Height.Should().Be(32);
    }
}
