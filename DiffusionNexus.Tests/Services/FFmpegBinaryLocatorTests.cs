using System;
using System.IO;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// FFMpegCore shells out to ffprobe as well as ffmpeg. A directory holding only one of
/// them must not be accepted as a valid binary folder, or discovery succeeds and the
/// first analysis call fails instead.
/// </summary>
public class FFmpegBinaryLocatorTests : IDisposable
{
    private readonly string _dir;

    public FFmpegBinaryLocatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dn-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private void CreateBinary(string fileName) => File.WriteAllText(Path.Combine(_dir, fileName), "stub");

    [Fact]
    public void WhenBothBinariesPresentThenDirectoryIsAccepted()
    {
        CreateBinary(FFmpegBinaryLocator.FFmpegFileName);
        CreateBinary(FFmpegBinaryLocator.FFprobeFileName);

        FFmpegBinaryLocator.HasBothBinaries(_dir).Should().BeTrue();
    }

    [Fact]
    public void WhenOnlyFFmpegPresentThenDirectoryIsRejected()
    {
        CreateBinary(FFmpegBinaryLocator.FFmpegFileName);

        FFmpegBinaryLocator.HasBothBinaries(_dir).Should().BeFalse(
            "FFMpegCore needs ffprobe for analysis, so a folder with only ffmpeg is not usable");
    }

    [Fact]
    public void WhenOnlyFFprobePresentThenDirectoryIsRejected()
    {
        CreateBinary(FFmpegBinaryLocator.FFprobeFileName);

        FFmpegBinaryLocator.HasBothBinaries(_dir).Should().BeFalse();
    }

    [Fact]
    public void WhenDirectoryIsEmptyThenDirectoryIsRejected()
    {
        FFmpegBinaryLocator.HasBothBinaries(_dir).Should().BeFalse();
    }

    [Fact]
    public void WhenDirectoryDoesNotExistThenDirectoryIsRejected()
    {
        FFmpegBinaryLocator.HasBothBinaries(Path.Combine(_dir, "nope")).Should().BeFalse();
    }

    [Fact]
    public void WhenDirectoryPathIsBlankThenDirectoryIsRejected()
    {
        FFmpegBinaryLocator.HasBothBinaries("   ").Should().BeFalse();
    }

    [Fact]
    public void WhenBinaryNamesResolvedThenTheyMatchThePlatform()
    {
        var expectedSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        FFmpegBinaryLocator.FFmpegFileName.Should().Be("ffmpeg" + expectedSuffix);
        FFmpegBinaryLocator.FFprobeFileName.Should().Be("ffprobe" + expectedSuffix);
    }
}
