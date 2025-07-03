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
}
