using System.Runtime.InteropServices;
using DiffusionNexus.Domain.Utilities;
using DiffusionNexus.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Domain.Utilities;

/// <summary>
/// Issue #581: the repo grew five hand-rolled free-space probes with four different
/// conventions for "cannot determine", and none of them was volume-mount-point aware.
/// These pin the one probe they were consolidated into.
/// </summary>
public sealed class DiskSpaceTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("dn-diskspace").FullName;

    public void Dispose()
    {
        // The junction is unlinked first, with a NON-recursive delete that can only ever remove
        // the reparse point. .NET's recursive delete does not descend into junctions, but the
        // link here points at another drive's root and that is not a promise worth leaning on.
        try { Directory.Delete(Path.Combine(_temp, MountPointProbe.LinkName)); } catch { /* not every test makes one */ }
        try { Directory.Delete(_temp, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void TryGetAvailableSpace_ReportsKnownSpace_ForAnExistingDirectory()
    {
        var result = DiskSpace.TryGetAvailableSpace(_temp);

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BePositive();
    }

    [Fact]
    public void TryGetAvailableSpace_ReportsKnownSpace_ForAFileThatDoesNotExistYet()
    {
        // Every download preflight in the app hands this a destination FILE path that is about
        // to be created. GetDiskFreeSpaceEx refuses a file path outright (ERROR_DIRECTORY, 267),
        // so answering for the volume it will land on is the whole job.
        var result = DiskSpace.TryGetAvailableSpace(Path.Combine(_temp, "model.gguf"));

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BePositive();
    }

    [Fact]
    public void TryGetAvailableSpace_ReportsKnownSpace_ForADirectoryThatDoesNotExistYet()
    {
        // The captioning destination picker offers "<models>/captioning — subfolder will be
        // created". A nonexistent directory is a normal destination, not a broken one.
        var result = DiskSpace.TryGetAvailableSpace(Path.Combine(_temp, "not", "created", "yet"));

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BePositive();
    }

    [Fact]
    public void TryGetAvailableSpace_ResolvesRelativePaths()
    {
        // The probe this replaced called Path.GetPathRoot straight on the caller's string, which
        // is "" for a relative path — and every caller then had to decide what "" meant.
        var relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), _temp);

        var result = DiskSpace.TryGetAvailableSpace(relative);

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BePositive();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetAvailableSpace_ReportsUnreachable_ForAnEmptyPath(string? path)
    {
        var result = DiskSpace.TryGetAvailableSpace(path);

        result.Kind.Should().Be(FreeSpaceKind.Unreachable);
        result.FreeBytes.Should().Be(0);
    }

    [WindowsFact]
    public void TryGetAvailableSpace_ReportsUnreachable_ForADeadDriveLetter()
    {
        // The distinction that cost a run: failing open on an unplugged drive armed Start and
        // the sorter then reported "Done: 0 sorted, 0 duplicates skipped, 412 failed".
        var result = DiskSpace.TryGetAvailableSpace(Path.Combine(DriveLetters.DeadRoot(), "loras"));

        result.Kind.Should().Be(FreeSpaceKind.Unreachable);
    }

    [WindowsFact]
    public void TryGetAvailableSpace_WithoutNetworkIo_ReportsUnreachable_ForADeadDriveLetter()
    {
        // An unmapped letter reports DriveType.NoRootDirectory, which settles it with no I/O at
        // all — so the UI-thread mode still gives the verdict a caller blocks on.
        var result = DiskSpace.TryGetAvailableSpace(DriveLetters.DeadRoot(), DiskProbe.LocalVolumesOnly);

        result.Kind.Should().Be(FreeSpaceKind.Unreachable);
    }

    [WindowsFact]
    public void TryGetAvailableSpace_WithoutNetworkIo_ReportsUnknown_ForAUncPath()
    {
        // Measured: reading a dead UNC path costs the full SMB timeout (~5 s), so the mode that
        // runs on the UI thread must not attempt it. Unknown, not unreachable — callers fail
        // open and say so, and the off-thread paths get the real answer.
        var result = DiskSpace.TryGetAvailableSpace(@"\\nas\share\loras", DiskProbe.LocalVolumesOnly);

        result.Kind.Should().Be(FreeSpaceKind.Unknown);
        result.FreeBytes.Should().Be(0);
    }

    [MountPointFact]
    public void TryGetAvailableSpace_MeasuresTheMountedVolume_NotTheHostDriveLetter()
    {
        var (host, mounted) = MountPointProbe.Create(_temp);

        var result = DiskSpace.TryGetAvailableSpace(Path.Combine(mounted, "model.safetensors"));

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BeCloseTo(MountPointProbe.TargetFreeBytes, MountPointProbe.Tolerance,
            "a destination on a mounted volume must be measured against that volume");
        result.FreeBytes.Should().NotBeCloseTo(host, MountPointProbe.Tolerance,
            "reporting the host letter's free space is the bug — a full C: blocked downloads bound for an empty 8 TB disk");
    }

    [MountPointFact]
    public void TryGetAvailableSpace_WithoutNetworkIo_StillMeasuresAMountedLocalVolume()
    {
        // The UI-thread mode gives up network volumes, NOT mount points. Measured on a local
        // path the full reading costs well under a millisecond, and a local disk mounted into a
        // folder is exactly the case issue #581 is about — so the caller that gates the Start
        // button gets the accurate number without leaving the dispatcher.
        var (host, mounted) = MountPointProbe.Create(_temp);

        var result = DiskSpace.TryGetAvailableSpace(
            Path.Combine(mounted, "model.safetensors"), DiskProbe.LocalVolumesOnly);

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BeCloseTo(MountPointProbe.TargetFreeBytes, MountPointProbe.Tolerance);
        result.FreeBytes.Should().NotBeCloseTo(host, MountPointProbe.Tolerance);
    }

    [MountPointFact]
    public void GetVolumeRoot_WithoutNetworkIo_StillResolvesAMountedLocalVolume()
    {
        var (_, mounted) = MountPointProbe.Create(_temp);

        DiskSpace.GetVolumeRoot(Path.Combine(mounted, "deeper"), DiskProbe.LocalVolumesOnly)
            .Should().Be(MountPointProbe.TargetRoot);
    }

    [WindowsFact]
    public void GetVolumeRoot_WithoutNetworkIo_FallsBackToTheShareRoot_ForAUncPath()
    {
        DiskSpace.GetVolumeRoot(@"\\nas\share\loras", DiskProbe.LocalVolumesOnly)
            .Should().Be(@"\\nas\share\", "the user still has to be told which destination was skipped");
    }

    [WindowsFact]
    public void GetVolumeRoot_AppendsTheTrailingSeparator_ForAUncPath()
    {
        // Path.GetPathRoot(@"\\nas\share\loras") is [\\nas\share] — no separator. The doc
        // promises one, the string is shown to the user, and GetDiskFreeSpaceEx documents a
        // trailing backslash as required for a UNC name.
        DiskSpace.GetVolumeRoot(@"\\nas\share\loras").Should().EndWith(@"\");
    }

    [Fact]
    public void TryGetVolumeSpace_MeasuresAnAlreadyResolvedRoot()
    {
        // The queue resolves a destination's volume to group by it, then reads that volume. This
        // overload exists so the second step does not resolve the same path all over again.
        var volume = DiskSpace.GetVolumeRoot(_temp)!;

        var result = DiskSpace.TryGetVolumeSpace(volume);

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BeCloseTo(DiskSpace.TryGetAvailableSpace(_temp).FreeBytes, MountPointProbe.Tolerance);
    }

    [MountPointFact]
    public void TryGetVolumeSpace_KeepsTheMountedVolumesReading()
    {
        var (host, mounted) = MountPointProbe.Create(_temp);
        var volume = DiskSpace.GetVolumeRoot(mounted)!;

        var result = DiskSpace.TryGetVolumeSpace(volume);

        result.FreeBytes.Should().BeCloseTo(MountPointProbe.TargetFreeBytes, MountPointProbe.Tolerance);
        result.FreeBytes.Should().NotBeCloseTo(host, MountPointProbe.Tolerance);
    }

    [Theory]
    // A destination sitting on a mount deeper than "/" must match that mount, not the root
    // filesystem — the Unix equivalent of the Windows bug this class exists to fix.
    [InlineData("/mnt/models/loras/x.safetensors", "/mnt/models")]
    [InlineData("/mnt/models", "/mnt/models")]
    [InlineData("/home/chris/loras", "/home")]
    [InlineData("/var/tmp/x", "/")]
    [InlineData("/mnt/models-backup/x", "/")]
    public void LongestMountRootContaining_PicksTheDeepestMountThePathLiesUnder(string path, string expected)
    {
        string[] mounts = ["/", "/home", "/mnt/models", "/mnt/other"];

        DiskSpace.LongestMountRootContaining(path, mounts).Should().Be(expected);
    }

    [Fact]
    public void LongestMountRootContaining_ReturnsNull_WhenNothingContainsThePath()
    {
        DiskSpace.LongestMountRootContaining("/mnt/models/x", ["/home", "/boot"]).Should().BeNull();
    }

    [Theory]
    [InlineData(FreeSpaceKind.Unknown)]
    [InlineData(FreeSpaceKind.Unreachable)]
    public void FreeSpaceResult_ReportsNoBytes_ForAnythingButKnown(FreeSpaceKind kind)
    {
        // The invariant the doc states. Without this the positional constructor lets anyone build
        // a reading of 12345 bytes that no caller is allowed to compare against.
        new FreeSpaceResult(kind, 12_345).FreeBytes.Should().Be(0);
    }

    [Fact]
    public void FreeSpaceResult_FactoriesAreReachableFromOtherAssemblies()
    {
        // They were internal, so four test files hand-rolled the same three helpers.
        FreeSpaceResult.Known(42).Should().Be(new FreeSpaceResult(FreeSpaceKind.Known, 42));
        FreeSpaceResult.Unknown.Kind.Should().Be(FreeSpaceKind.Unknown);
        FreeSpaceResult.Unreachable.Kind.Should().Be(FreeSpaceKind.Unreachable);
    }

    [MountPointFact]
    public void GetVolumeRoot_ReturnsTheMountedVolume_NotTheHostDriveLetter()
    {
        var (_, mounted) = MountPointProbe.Create(_temp);

        DiskSpace.GetVolumeRoot(Path.Combine(mounted, "deeper", "still.safetensors"))
            .Should().Be(MountPointProbe.TargetRoot,
                "grouping queued bytes by drive letter puts two different volumes in one bucket");
    }

    [Fact]
    public void GetVolumeRoot_ReturnsTheDriveRoot_ForAnOrdinaryPath()
    {
        DiskSpace.GetVolumeRoot(_temp).Should().Be(Path.GetPathRoot(_temp));
    }

    [WindowsFact]
    public void GetVolumeRoot_ReturnsTheDriveRoot_ForADeadDriveLetter()
    {
        // No volume to name, but the user still has to be told which destination is dead.
        DiskSpace.GetVolumeRoot(Path.Combine(DriveLetters.DeadRoot(), "loras")).Should().Be(DriveLetters.DeadRoot());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetVolumeRoot_ReturnsNull_ForAnEmptyPath(string? path)
    {
        DiskSpace.GetVolumeRoot(path).Should().BeNull();
    }

}

