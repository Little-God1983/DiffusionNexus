using System.Runtime.InteropServices;
using Xunit;

namespace DiffusionNexus.Tests.Helpers;

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
public static class MountPointProbe
{
    /// <summary>Free space wanders while the test runs; 2 GB is far below the gap we require.</summary>
    public const long Tolerance = 2L << 30;

    /// <summary>Name of the junction, so the fixture can unlink it before tearing the temp dir down.</summary>
    public const string LinkName = "mounted-volume";

    private const long MinimumGap = 20L << 30;

    private static readonly DriveInfo? Target = FindTarget();

    public static bool IsAvailable => Target is not null;

    public static string TargetRoot => Target!.Name;

    public static long TargetFreeBytes => Target!.AvailableFreeSpace;

    /// <summary>
    /// Returns the host drive's free bytes and the path of a junction that lands on another drive.
    /// </summary>
    public static (long HostFreeBytes, string MountedPath) Create(string hostDirectory)
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

/// <summary>Drive letters, for tests that need one with nothing behind it.</summary>
public static class DriveLetters
{
    /// <summary>Root of a drive letter no volume is mounted on.</summary>
    public static string DeadRoot()
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
