using DiffusionNexus.Service.Services.IO;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class HashingServiceTests
{
    [Fact]
    public void ComputeFileHash_ReturnsExpected()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "hash me");
        var svc = new HashingService();
        try
        {
            var result = svc.ComputeFileHash(tempFile);
            using var md5 = System.Security.Cryptography.MD5.Create();
            using var stream = File.OpenRead(tempFile);
            var expected = BitConverter.ToString(md5.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            result.Should().Be(expected);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ComputeAsync_Stream_ReturnsExpected()
    {
        var svc = new HashingService();
        await using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("hello"));
        var hash = await svc.ComputeAsync(ms, HashingService.HashAlgorithmType.MD5);
        ms.Position = 0;
        using var md5 = System.Security.Cryptography.MD5.Create();
        var expected = BitConverter.ToString(md5.ComputeHash(ms)).Replace("-", "").ToLowerInvariant();
        hash.Should().Be(expected);
    }
}
