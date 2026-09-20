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
            util.EnoughFreeSpace(dir, Path.Combine(DriveLetters.DeadRoot(), "target")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [WindowsFact]
    public void EnoughFreeSpace_AnswersAnUnreachableTarget_WithoutWalkingTheSource()
    {
        // The source walk recurses an entire LoRA library; the disk verdict takes microseconds
        // and already settles it. Proven by a source path that does not exist: GetDirectorySize
        // would throw DirectoryNotFoundException if it ran.
        var util = new DiskUtility();

        util.EnoughFreeSpace(
            Path.Combine(Path.GetTempPath(), "dn-no-such-source"),
            Path.Combine(DriveLetters.DeadRoot(), "target")).Should().BeFalse();
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
