using System.Runtime.InteropServices;
using DiffusionNexus.Domain.Utilities;
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
        var result = DiskSpace.TryGetAvailableSpace(Path.Combine(DeadDriveRoot(), "loras"));

        result.Kind.Should().Be(FreeSpaceKind.Unreachable);
    }

    [WindowsFact]
    public void TryGetAvailableSpace_WithoutMountPointResolution_ReportsUnreachable_ForADeadDriveLetter()
    {
        // The cheap mode gives up mount-point resolution, never the unreachable verdict — that is
        // the one a caller blocks on.
        var result = DiskSpace.TryGetAvailableSpace(DeadDriveRoot(), resolveMountPoints: false);

        result.Kind.Should().Be(FreeSpaceKind.Unreachable);
    }

    [WindowsFact]
    public void TryGetAvailableSpace_WithoutMountPointResolution_ReportsUnknown_ForAUncRoot()
    {
        // A UNC share has no DriveInfo — new DriveInfo(@"\\nas\share\") throws ArgumentException
        // on the name alone, without touching the network — so the cheap mode has no number to
        // give. Unknown, not unreachable: callers fail open and say so.
        var result = DiskSpace.TryGetAvailableSpace(@"\\nas\share\loras", resolveMountPoints: false);

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
    public void TryGetAvailableSpace_WithoutMountPointResolution_MeasuresTheHostDriveLetter()
    {
        // Pins the documented cost of the cheap mode, so nobody adopts it by accident: it answers
        // from the drive letter alone and therefore reports the host volume for a mount point.
        var (host, mounted) = MountPointProbe.Create(_temp);

        var result = DiskSpace.TryGetAvailableSpace(mounted, resolveMountPoints: false);

        result.Kind.Should().Be(FreeSpaceKind.Known);
        result.FreeBytes.Should().BeCloseTo(host, MountPointProbe.Tolerance);
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
        DiskSpace.GetVolumeRoot(Path.Combine(DeadDriveRoot(), "loras")).Should().Be(DeadDriveRoot());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetVolumeRoot_ReturnsNull_ForAnEmptyPath(string? path)
    {
        DiskSpace.GetVolumeRoot(path).Should().BeNull();
    }

    /// <summary>Root of a drive letter no volume is mounted on.</summary>
    private static string DeadDriveRoot()
    {
        var taken = DriveInfo.GetDrives()
            .Select(d => char.ToUpperInvariant(d.Name[0]))
            .ToHashSet();

        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!taken.Contains(letter)) return $"{letter}:\\";
        }

        throw new InvalidOperationException("every drive letter is in use; cannot test the dead-letter verdict");
    }
}

/// <summary>A fact that runs on Windows only.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Drive letters and UNC roots are Windows concepts.";
        }
    }
}

/// <summary>
/// A fact that runs only where a mount point can be faked: Windows, plus a second ready fixed
/// drive whose free space differs from the temp drive's by enough to tell the two apart.
/// </summary>
public sealed class MountPointFactAttribute : FactAttribute
{
    public MountPointFactAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Skip = "Directory junctions are a Windows feature.";
            return;
        }

        if (!MountPointProbe.IsAvailable)
        {
            Skip = "Needs a second fixed drive whose free space is clearly different from the temp drive's.";
        }
    }
}

/// <summary>
/// Fakes a volume mount point with a directory junction to another drive. A junction is the same
/// reparse point a mounted-folder volume uses, and <c>GetDiskFreeSpaceEx</c> resolves both the
/// same way, so it reproduces the "C:/Models is really an 8 TB disk" case without a spare volume.
/// </summary>
internal static class MountPointProbe
{
    /// <summary>Free space wanders while the test runs; 2 GB is far below the gap we require.</summary>
    internal const long Tolerance = 2L << 30;

    /// <summary>Name of the junction, so the fixture can unlink it before tearing the temp dir down.</summary>
    internal const string LinkName = "mounted-volume";

    private const long MinimumGap = 20L << 30;

    private static readonly DriveInfo? Target = FindTarget();

    internal static bool IsAvailable => Target is not null;

    internal static string TargetRoot => Target!.Name;

    internal static long TargetFreeBytes => Target!.AvailableFreeSpace;

    /// <summary>
    /// Returns the host drive's free bytes and the path of a junction that lands on another drive.
    /// </summary>
    internal static (long HostFreeBytes, string MountedPath) Create(string hostDirectory)
    {
        var link = Path.Combine(hostDirectory, LinkName);
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{TargetRoot}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();

        if (!Directory.Exists(link))
        {
            throw new InvalidOperationException("mklink /J did not create the junction");
        }

        return (new DriveInfo(Path.GetPathRoot(hostDirectory)!).AvailableFreeSpace, link);
    }

    private static DriveInfo? FindTarget()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

        var hostRoot = Path.GetPathRoot(Path.GetTempPath());
        long hostFree;
        try { hostFree = new DriveInfo(hostRoot!).AvailableFreeSpace; }
        catch { return null; }

        return DriveInfo.GetDrives().FirstOrDefault(d =>
        {
            try
            {
                return d.IsReady
                    && d.DriveType == DriveType.Fixed
                    && !string.Equals(d.Name, hostRoot, StringComparison.OrdinalIgnoreCase)
                    && Math.Abs(d.AvailableFreeSpace - hostFree) > MinimumGap;
            }
            catch { return false; }
        });
    }
}
