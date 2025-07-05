using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class FileControllerServiceTests
{
    [Fact]
    public void ComputeFileHash_ReturnsExpectedHash()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "hash me");
        var svc = new FileControllerService();

        try
        {
            // Act
            var hash = svc.ComputeFileHash(tempFile);

            // Assert
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var stream = File.OpenRead(tempFile);
            var expected = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            hash.Should().Be(expected);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DeleteEmptyDirectoriesAsync_RemovesEmptyDirs()
    {
        var svc = new FileControllerService();
        var basePath = Path.Combine(Path.GetTempPath(), "DeleteEmptyTest");
        var emptyDir = Path.Combine(basePath, "empty");
        var nonEmptyDir = Path.Combine(basePath, "nonempty");
        Directory.CreateDirectory(emptyDir);
        Directory.CreateDirectory(nonEmptyDir);
        File.WriteAllText(Path.Combine(nonEmptyDir, "file.txt"), "content");

        await svc.DeleteEmptyDirectoriesAsync(basePath);

        Directory.Exists(emptyDir).Should().BeFalse();
        Directory.Exists(nonEmptyDir).Should().BeTrue();

        Directory.Delete(basePath, true);
    }
}
