using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Tests for the tag-index integration of <see cref="ImageMetadataPanelViewModel"/>:
/// the Content Tags section of the image viewer's Generation Data panel.
/// Paths use a non-<c>.png</c> extension so the PNG metadata parse takes its
/// early-out branch and never touches the file system.
/// </summary>
public class ImageMetadataPanelViewModelTests
{
    private static string ImagePath(string name) => Path.GetFullPath(Path.Combine("gallery", $"{name}.jpg"));

    [Fact]
    public async Task LoadMetadata_PopulatesTagsAndRating_ForAnIndexedImage()
    {
        var path = ImagePath("indexed");
        var mockIndex = new Mock<ITagIndexService>();
        mockIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new ImageTagLookup(IsNsfw: true, Tags: ["1girl", "solo"]),
            });

        var panel = new ImageMetadataPanelViewModel(mockIndex.Object);
        panel.LoadMetadata(path);
        await panel.TagLookup;

        panel.HasTagData.Should().BeTrue();
        panel.IsNsfw.Should().BeTrue();
        panel.Tags.Should().Equal("1girl", "solo");
    }

    [Fact]
    public async Task LoadMetadata_HidesTheTagSection_ForAnUnindexedImage()
    {
        var mockIndex = new Mock<ITagIndexService>();
        mockIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());

        var panel = new ImageMetadataPanelViewModel(mockIndex.Object);
        panel.LoadMetadata(ImagePath("unindexed"));
        await panel.TagLookup;

        panel.HasTagData.Should().BeFalse("an unindexed image must not show a misleading empty tag list");
        panel.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadMetadata_WithoutATagIndexService_ShowsNoTagSection()
    {
        var panel = new ImageMetadataPanelViewModel();
        panel.LoadMetadata(ImagePath("any"));
        await panel.TagLookup;

        panel.HasTagData.Should().BeFalse();
    }

    [Fact]
    public async Task NavigatingToAnotherImage_ReplacesTheTags_AndClearsThemWhenUnindexed()
    {
        var first = ImagePath("first");
        var second = ImagePath("second");
        var mockIndex = new Mock<ITagIndexService>();
        mockIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string> paths, CancellationToken _) =>
                paths.Contains(first)
                    ? new Dictionary<string, ImageTagLookup>(StringComparer.OrdinalIgnoreCase)
                    {
                        [first] = new ImageTagLookup(IsNsfw: false, Tags: ["dog"]),
                    }
                    : new Dictionary<string, ImageTagLookup>());

        var panel = new ImageMetadataPanelViewModel(mockIndex.Object);

        panel.LoadMetadata(first);
        await panel.TagLookup;
        panel.HasTagData.Should().BeTrue();
        panel.IsNsfw.Should().BeFalse();

        // Arrow-key navigation to an unindexed image: the previous image's
        // tags must not linger on screen.
        panel.LoadMetadata(second);
        await panel.TagLookup;
        panel.HasTagData.Should().BeFalse();
        panel.Tags.Should().BeEmpty();
    }

    [Fact]
    public async Task ATagIndexError_LeavesThePanelUsable_WithoutTags()
    {
        var mockIndex = new Mock<ITagIndexService>();
        mockIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db is broken"));

        var panel = new ImageMetadataPanelViewModel(mockIndex.Object);
        panel.LoadMetadata(ImagePath("any"));
        await panel.TagLookup;

        panel.HasTagData.Should().BeFalse("a broken index must degrade to 'no tags shown', not crash the viewer");
    }
}
