using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Sync.Service;

/// <summary>
/// #631: Civitai no longer sends an image id, so images are matched by URL — an image already
/// stored gets its rating instead of a second copy, and copies left by the old id-only match go.
/// </summary>
public sealed class CivitaiImageMergerTests
{
    private const string UrlA = "https://image.civitai.com/x/a.jpeg";
    private const string UrlB = "https://image.civitai.com/x/b.jpeg";

    private static CivitaiModelImage Civ(string url, int? level) => new() { Url = url, NsfwLevel = level };

    private static ModelVersion Version(params ModelImage[] images)
    {
        var version = new ModelVersion { Id = 7 };
        foreach (var image in images) version.Images.Add(image);
        return version;
    }

    [Fact]
    public void AnImageAlreadyStored_GetsItsRating_MatchedByUrl()
    {
        var stored = new ModelImage { Id = 1, Url = UrlA };
        var version = Version(stored);

        var result = CivitaiImageMerger.Merge(version, [Civ(UrlA, 16)]);

        version.Images.Should().ContainSingle().Which.Should().BeSameAs(stored);
        stored.NsfwLevel.Should().Be(NsfwLevel.XXX);
        result.Should().Be(new ImageMergeResult(Added: 0, Rated: 1, Removed: 0));
    }

    [Fact]
    public void UrlMatch_IgnoresCase()
    {
        var stored = new ModelImage { Id = 1, Url = UrlA.ToUpperInvariant() };
        var version = Version(stored);

        CivitaiImageMerger.Merge(version, [Civ(UrlA, 2)]);

        version.Images.Should().ContainSingle();
        stored.NsfwLevel.Should().Be(NsfwLevel.PG13);
    }

    [Fact]
    public void ANewImage_IsAddedWithItsRating_AfterTheExistingOnes()
    {
        var version = Version(new ModelImage { Id = 1, Url = UrlA, NsfwLevel = NsfwLevel.PG, SortOrder = 0 });

        var result = CivitaiImageMerger.Merge(version, [Civ(UrlA, 1), Civ(UrlB, 4)]);

        var added = version.Images.Should().HaveCount(2).And.Subject.Single(i => i.Url == UrlB);
        added.NsfwLevel.Should().Be(NsfwLevel.R);
        added.ModelVersionId.Should().Be(7);
        added.SortOrder.Should().Be(1);
        result.Should().Be(new ImageMergeResult(Added: 1, Rated: 0, Removed: 0));
    }

    [Fact]
    public void DuplicateCopiesOfAUrl_CollapseToTheCopyWithAThumbnail()
    {
        var bare = new ModelImage { Id = 1, Url = UrlA };
        var withThumbnail = new ModelImage { Id = 2, Url = UrlA, ThumbnailData = [1, 2, 3] };
        var another = new ModelImage { Id = 3, Url = UrlA };
        var version = Version(bare, withThumbnail, another);

        var result = CivitaiImageMerger.Merge(version, [Civ(UrlA, 1)]);

        version.Images.Should().ContainSingle().Which.Should().BeSameAs(withThumbnail);
        withThumbnail.NsfwLevel.Should().Be(NsfwLevel.PG);
        result.Removed.Should().Be(2);
    }

    [Fact]
    public void ADeferredThumbnail_CountsAsAThumbnail_WhenChoosingTheCopyToKeep()
    {
        var bare = new ModelImage { Id = 1, Url = UrlA };
        var deferred = new ModelImage { Id = 2, Url = UrlA, ThumbnailData = ModelImage.ThumbnailNotLoadedSentinel };
        var version = Version(bare, deferred);

        CivitaiImageMerger.Merge(version, [Civ(UrlA, 1)]);

        version.Images.Should().ContainSingle().Which.Should().BeSameAs(deferred);
    }

    [Fact]
    public void WithoutAThumbnailAnywhere_TheFirstCopyIsKept()
    {
        var first = new ModelImage { Id = 4, Url = UrlA, SortOrder = 0 };
        var later = new ModelImage { Id = 9, Url = UrlA, SortOrder = 5 };
        var version = Version(later, first);

        CivitaiImageMerger.Merge(version, [Civ(UrlA, 1)]);

        version.Images.Should().ContainSingle().Which.Should().BeSameAs(first);
    }

    [Fact]
    public void AnUnratedUpstreamImage_DoesNotEraseAKnownRating()
    {
        var stored = new ModelImage { Id = 1, Url = UrlA, NsfwLevel = NsfwLevel.X };
        var version = Version(stored);

        var result = CivitaiImageMerger.Merge(version, [Civ(UrlA, null), Civ(UrlA, 0)]);

        stored.NsfwLevel.Should().Be(NsfwLevel.X);
        result.Changed.Should().BeFalse();
    }

    [Fact]
    public void NothingNewAndNothingToRate_ReportsNoChange()
    {
        var version = Version(new ModelImage { Id = 1, Url = UrlA, NsfwLevel = NsfwLevel.PG });

        var result = CivitaiImageMerger.Merge(version, [Civ(UrlA, 1), new CivitaiModelImage { Url = "" }]);

        result.Changed.Should().BeFalse();
        version.Images.Should().ContainSingle();
    }

    [Fact]
    public void ARepeatedUrlInTheResponse_IsAddedOnce()
    {
        var version = Version();

        var result = CivitaiImageMerger.Merge(version, [Civ(UrlA, 1), Civ(UrlA, 1)]);

        version.Images.Should().ContainSingle();
        result.Added.Should().Be(1);
    }
}
