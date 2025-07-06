using DiffusionNexus.Service.Services.IO;
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
