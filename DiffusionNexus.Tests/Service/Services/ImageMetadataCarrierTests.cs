using DiffusionNexus.Service.Services.Imaging;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class ImageMetadataCarrierTests
{
    [Fact]
    public void IsLosslessWebp_ChunkSizeBeyondTheFile_ReturnsFalseInsteadOfThrowing()
    {
        // RIFF/WEBP header followed by a junk chunk that claims 0xFFFFFFFF bytes: a size this
        // large must end the scan, not overflow the read position.
        byte[] webp =
        [
            .. "RIFF"u8, 0x20, 0x00, 0x00, 0x00, .. "WEBP"u8,
            .. "JUNK"u8, 0xFF, 0xFF, 0xFF, 0xFF,
            .. "VP8L"u8, 0x00, 0x00, 0x00, 0x00,
        ];

        var act = () => ImageMetadataCarrier.IsLosslessWebp(webp);

        act.Should().NotThrow();
        ImageMetadataCarrier.IsLosslessWebp(webp).Should().BeFalse();
    }

    [Theory]
    [InlineData("VP8L", true)]
    [InlineData("VP8 ", false)]
    public void IsLosslessWebp_ReadsTheImageChunkBehindAnExtendedHeader(string imageChunk, bool expected)
    {
        byte[] webp =
        [
            .. "RIFF"u8, 0x30, 0x00, 0x00, 0x00, .. "WEBP"u8,
            .. "VP8X"u8, 0x0A, 0x00, 0x00, 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            .. System.Text.Encoding.ASCII.GetBytes(imageChunk), 0x00, 0x00, 0x00, 0x00,
        ];

        ImageMetadataCarrier.IsLosslessWebp(webp).Should().Be(expected);
    }
}
