using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Domain;

public class ModelSyncStateTests
{
    [Fact]
    public void NewStateDefaultsToNoneWithZeroAttempts()
    {
        var state = new ModelSyncState { ModelId = 7 };

        state.MetadataOutcome.Should().Be(SyncOutcome.None);
        state.MetadataAttempts.Should().Be(0);
        state.MetadataCheckedAt.Should().BeNull();
        state.TagsCheckedAt.Should().BeNull();
        state.ImagesCheckedAt.Should().BeNull();
        state.HeaderCheckedAt.Should().BeNull();
        state.SidecarSignature.Should().BeNull();
        state.LastError.Should().BeNull();
    }

    [Theory]
    [InlineData(ThumbnailFailureReason.Http404, true)]
    [InlineData(ThumbnailFailureReason.NotDecodable, true)]
    [InlineData(ThumbnailFailureReason.LocalFileMissing, true)]
    [InlineData(ThumbnailFailureReason.UnsupportedScheme, true)]
    [InlineData(ThumbnailFailureReason.HttpError, false)]
    [InlineData(ThumbnailFailureReason.Corrupt, false)]
    [InlineData(ThumbnailFailureReason.VideoNoPoster, false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void HardFailuresAreNeverAutoRetried(string? reason, bool expectedHard)
    {
        ThumbnailFailureReason.IsHardFailure(reason).Should().Be(expectedHard);
    }

    [Fact]
    public void ModelImageCarriesAttemptColumns()
    {
        var image = new ModelImage { Url = "https://x/y.jpeg" };
        image.ThumbnailAttemptedAt.Should().BeNull();
        image.ThumbnailFailure.Should().BeNull();
    }
}
