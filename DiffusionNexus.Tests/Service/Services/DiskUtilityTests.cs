using DiffusionNexus.Service.Services.IO;
using DiffusionNexus.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class DiskUtilityTests
{
    [Fact]
    public void EnoughFreeSpaceOnDisk_ReturnsTrue_ForSmallFolder()
    {
        var util = new DiskUtility();
        var temp = Path.GetTempPath();
        var dir = Directory.CreateDirectory(Path.Combine(temp, "diskcheck2"));
        try
        {
            util.EnoughFreeSpace(dir.FullName, temp).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(dir.FullName, true);
        }
    }

    [WindowsFact]
    public void EnoughFreeSpace_ReturnsFalse_ForAnUnreachableTarget()
    {
        // It used to let DriveNotFoundException out of a method whose whole job is to answer
        // yes/no, so every caller had to catch it to find out (issue #581).
        var util = new DiskUtility();
        var dir = Directory.CreateTempSubdirectory("dn-diskutil-").FullName;
        try
        {
            util.EnoughFreeSpace(dir, Path.Combine(DeadDriveRoot(), "target")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>Root of a drive letter no volume is mounted on.</summary>
    private static string DeadDriveRoot()
    {
        var taken = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (var letter = 'Z'; letter >= 'D'; letter--)
        {
            if (!taken.Contains(letter)) return $"{letter}:\\";
        }

        throw new InvalidOperationException("every drive letter is in use");
    }

    [Fact]
    public async Task DeleteEmptyDirectoriesAsync_RemovesEmptyDirs()
    {
        var util = new DiskUtility();
        var basePath = Path.Combine(Path.GetTempPath(), "DeleteEmptyTest2");
        var emptyDir = Path.Combine(basePath, "empty");
        var nonEmptyDir = Path.Combine(basePath, "nonempty");
        Directory.CreateDirectory(emptyDir);
        Directory.CreateDirectory(nonEmptyDir);
        File.WriteAllText(Path.Combine(nonEmptyDir, "file.txt"), "content");

        await util.DeleteEmptyDirectoriesAsync(basePath);

        Directory.Exists(emptyDir).Should().BeFalse();
        Directory.Exists(nonEmptyDir).Should().BeTrue();

        Directory.Delete(basePath, true);
    }
}
