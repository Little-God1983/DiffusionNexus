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
    public async Task SetRating_StoresTheClickedVerdict_AndNotifiesTheGallery()
    {
        var path = ImagePath("misrated");
        var mockIndex = new Mock<ITagIndexService>();
        mockIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new ImageTagLookup(IsNsfw: true, Tags: ["1girl"]),
            });

        var notified = new List<(string Path, bool IsNsfw)>();
        var panel = new ImageMetadataPanelViewModel(mockIndex.Object, (p, n) => notified.Add((p, n)));
        panel.LoadMetadata(path);
        await panel.TagLookup;

        await panel.SetRatingCommand.ExecuteAsync("SFW");

        mockIndex.Verify(t => t.SetRatingOverrideAsync(path, false, It.IsAny<CancellationToken>()), Times.Once);
        panel.IsNsfw.Should().BeFalse("the active side switches to the user's verdict");
        panel.IsRatingOverridden.Should().BeTrue("the 'manual' marker appears");
        notified.Should().ContainSingle().Which.Should().Be((path, false));

        // Clicking the side that is already active changes nothing and must
        // not quietly pin another override.
        await panel.SetRatingCommand.ExecuteAsync("SFW");
        mockIndex.Verify(t => t.SetRatingOverrideAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        notified.Should().HaveCount(1);
    }

    [Fact]
    public async Task SetRating_WhenTheWriteFails_KeepsShowingTheStoredRating()
    {
        var path = ImagePath("locked-db");
        var mockIndex = new Mock<ITagIndexService>();
        mockIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = new ImageTagLookup(IsNsfw: true, Tags: ["1girl"]),
            });
        mockIndex.Setup(t => t.SetRatingOverrideAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var notified = 0;
        var panel = new ImageMetadataPanelViewModel(mockIndex.Object, (_, _) => notified++);
        panel.LoadMetadata(path);
        await panel.TagLookup;

        await panel.SetRatingCommand.ExecuteAsync("SFW");

        panel.IsNsfw.Should().BeTrue("the badge must not lie about what is actually stored");
        panel.IsRatingOverridden.Should().BeFalse();
        notified.Should().Be(0, "the gallery must not be told about a change that never landed");
    }

    [Fact]
    public async Task ResetRating_ClearsTheOverride_AndReloadsTheAutomaticRating()
    {
        var path = ImagePath("reset-me");
        var mockIndex = new Mock<ITagIndexService>();
        // First load: overridden to NSFW. After the clear: automatic SFW.
        var cleared = false;
        mockIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new Dictionary<string, ImageTagLookup>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = cleared
                    ? new ImageTagLookup(IsNsfw: false, Tags: ["1girl"])
                    : new ImageTagLookup(IsNsfw: true, Tags: ["1girl"], IsRatingOverridden: true),
            });
        mockIndex.Setup(t => t.ClearRatingOverrideAsync(path, It.IsAny<CancellationToken>()))
            .Callback(() => cleared = true)
            .ReturnsAsync(true);

        var notified = new List<(string Path, bool IsNsfw)>();
        var panel = new ImageMetadataPanelViewModel(mockIndex.Object, (p, n) => notified.Add((p, n)));
        panel.LoadMetadata(path);
        await panel.TagLookup;
        panel.IsRatingOverridden.Should().BeTrue("precondition: the stored rating is a manual verdict");

        await panel.ResetRatingCommand.ExecuteAsync(null);

        panel.IsRatingOverridden.Should().BeFalse();
        panel.IsNsfw.Should().BeFalse("back to the automatic rating, re-read from the index");
        notified.Should().ContainSingle().Which.Should().Be((path, false));
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
