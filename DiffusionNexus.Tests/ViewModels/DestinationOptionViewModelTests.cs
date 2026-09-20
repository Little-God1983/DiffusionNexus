using DiffusionNexus.Domain.Utilities;
using DiffusionNexus.Inference.Captioning;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The captioning download dialog's per-destination row. It gates the OK button, so its answer
/// has to be the same one <see cref="CaptioningModelManager"/> gives a moment later (issue #581).
/// </summary>
public class DestinationOptionViewModelTests
{
    private const long Gb = 1L << 30;

    private static DestinationOptionViewModel Row(FreeSpaceResult space, long requiredBytes = 0) =>
        new(new CaptioningModelManager.DownloadDestination(@"C:\models", "label", space, IsDefault: true))
        {
            RequiredBytes = requiredBytes,
        };

    [Fact]
    public void ADestinationThatCannotBeReached_IsNotConfirmable()
    {
        // It used to be indistinguishable from an unprobeable share — both reported 0 free bytes
        // — so the dialog happily started a multi-gigabyte download into a drive that was gone.
        var row = Row(FreeSpaceResult.Unreachable, requiredBytes: 1 * Gb);

        row.IsUnreachable.Should().BeTrue();
        row.HasEnoughSpace.Should().BeFalse();
        row.FreeBytesLabel.Should().Contain("not reachable");
        row.SpaceCheckLabel.Should().Contain("NOT REACHABLE");
    }

    [Fact]
    public void ADestinationThatWillNotReportItsSize_StaysConfirmable()
    {
        // Refusing a perfectly good network share because it will not answer is the worse error.
        var row = Row(FreeSpaceResult.Unknown, requiredBytes: 1 * Gb);

        row.HasEnoughSpace.Should().BeTrue();
        row.FreeBytesLabel.Should().Contain("unknown");
    }

    [Fact]
    public void ADestinationWithRoomOnlyInsideTheSafetyMargin_IsNotConfirmable()
    {
        // The manager refuses unless free >= size + 256 MB. This row compared the bare size, so
        // 8.1 GB free with an 8.0 GB model showed a green "OK — need 8.0 GB" and the download
        // then reported "Not enough free disk space" — after the user had committed.
        var required = 8 * Gb;
        var free = required + (CaptioningModelManager.FreeSpaceMarginBytes / 2);

        var row = Row(FreeSpaceResult.Known(free), required);

        row.HasEnoughSpace.Should().BeFalse("the dialog must demand the headroom the download demands");
        row.SpaceCheckLabel.Should().Contain("NOT ENOUGH SPACE");
    }

    [Fact]
    public void ADestinationWithRoomForTheFileAndTheMargin_IsConfirmable()
    {
        var required = 8 * Gb;
        var free = required + CaptioningModelManager.FreeSpaceMarginBytes;

        var row = Row(FreeSpaceResult.Known(free), required);

        row.HasEnoughSpace.Should().BeTrue();
        row.SpaceCheckLabel.Should().StartWith("OK");
        row.FreeBytesLabel.Should().Contain("free");
    }

    [Fact]
    public void BeforeATierIsPicked_NothingIsDemanded()
    {
        // RequiredBytes is 0 until the VRAM tier resolves; the row must not read as a refusal.
        var row = Row(FreeSpaceResult.Known(1 * Gb));

        row.HasEnoughSpace.Should().BeTrue();
        row.SpaceCheckLabel.Should().BeEmpty();
    }
}
