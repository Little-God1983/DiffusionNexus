using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Thumbnails;

/// <summary>
/// Covers <see cref="ThumbnailWriter"/> — the one place the six thumbnail columns are written.
/// </summary>
/// <remarks>
/// Two rules carry the whole class. A success writes <i>every</i> column, the failure reason
/// included, because a row that keeps yesterday's reason next to today's bytes is a row the retry
/// policy will read wrongly. A failure writes only the attempt and the reason: whatever bytes the
/// row already holds are the ones the tile is currently showing, and blanking them would turn a
/// transient CDN hiccup into a visibly emptier library.
/// </remarks>
public class ThumbnailWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApplySuccess_WritesEveryColumnAndClearsTheFailure()
    {
        var image = new ModelImage
        {
            Url = "https://image.civitai.com/abc/width=450/still.jpeg",
            // A row that failed yesterday and succeeded today.
            ThumbnailAttemptedAt = Now.AddDays(-1),
            ThumbnailFailure = ThumbnailFailureReason.HttpError,
        };

        ThumbnailWriter.ApplySuccess(image, new ThumbnailPayload([1, 2, 3, 4], "image/jpeg", 450, 675), Now);

        image.ThumbnailData.Should().Equal(1, 2, 3, 4);
        image.ThumbnailMimeType.Should().Be("image/jpeg");
        image.ThumbnailWidth.Should().Be(450);
        image.ThumbnailHeight.Should().Be(675);
        image.ThumbnailAttemptedAt.Should().Be(Now);
        image.ThumbnailFailure.Should().BeNull("a row with bytes has no failure left to explain");
        image.HasThumbnail.Should().BeTrue();
    }

    [Fact]
    public void ApplyFailure_StampsTheAttemptAndLeavesExistingBytesAlone()
    {
        var image = new ModelImage
        {
            Url = "https://image.civitai.com/abc/width=450/still.jpeg",
            ThumbnailData = [9, 9, 9],
            ThumbnailMimeType = "image/jpeg",
            ThumbnailWidth = 450,
            ThumbnailHeight = 675,
        };

        ThumbnailWriter.ApplyFailure(image, ThumbnailFailureReason.Http404, Now);

        image.ThumbnailAttemptedAt.Should().Be(Now);
        image.ThumbnailFailure.Should().Be(ThumbnailFailureReason.Http404);

        // The bytes on the row are what the tile is showing right now; a failed re-fetch is not a
        // reason to take them away.
        image.ThumbnailData.Should().Equal(9, 9, 9);
        image.ThumbnailMimeType.Should().Be("image/jpeg");
        image.ThumbnailWidth.Should().Be(450);
        image.ThumbnailHeight.Should().Be(675);
    }

    [Fact]
    public void ApplyFailure_OnABareRowRecordsTheAnswerWithoutInventingBytes()
    {
        var image = new ModelImage { Url = "file:///C:/m/gone.png" };

        ThumbnailWriter.ApplyFailure(image, ThumbnailFailureReason.LocalFileMissing, Now);

        image.ThumbnailAttemptedAt.Should().Be(Now);
        image.ThumbnailFailure.Should().Be(ThumbnailFailureReason.LocalFileMissing);
        image.ThumbnailData.Should().BeNull();
        image.HasThumbnail.Should().BeFalse();
    }

    /// <summary>
    /// The reason is what the retry policy reads, so writing a fresh one has to replace the old one
    /// outright — a hard verdict left standing under a soft one would freeze the row forever.
    /// </summary>
    [Fact]
    public void ApplyFailure_ReplacesAPreviousReason()
    {
        var image = new ModelImage
        {
            Url = "https://image.civitai.com/abc/width=450/clip.mp4",
            ThumbnailAttemptedAt = Now.AddDays(-3),
            ThumbnailFailure = ThumbnailFailureReason.Http404,
        };

        ThumbnailWriter.ApplyFailure(image, ThumbnailFailureReason.VideoNoPoster, Now);

        image.ThumbnailFailure.Should().Be(ThumbnailFailureReason.VideoNoPoster);
        image.ThumbnailAttemptedAt.Should().Be(Now);
    }

    /// <summary>
    /// A null/empty reason would stamp "attempted, no failure", which the retry policy reads as
    /// success — a byte-less row frozen out of every future run. The writer must refuse it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ApplyFailure_RefusesAMissingReason(string? reason)
    {
        var image = new ModelImage { Url = "https://image.civitai.com/abc/width=450/a.jpeg" };

        var act = () => ThumbnailWriter.ApplyFailure(image, reason!, Now);

        act.Should().Throw<ArgumentException>();
        image.ThumbnailAttemptedAt.Should().BeNull("a refused stamp must not half-apply");
    }
}
