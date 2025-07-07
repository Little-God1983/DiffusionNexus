using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Classes;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class LoraMetadataDownloadServiceTests
{
    [Fact]
    public void ParseInfoJson_ReturnsExpectedValues()
    {
        var json = "{\"modelId\":123,\"images\":[{\"url\":\"http://example.com/a.jpg\"}]}";
        var (url, id) = LoraMetadataDownloadService.ParseInfoJson(json);
        id.Should().Be("123");
        url.Should().Be("http://example.com/a.jpg");
    }

    [Fact]
    public void ComputeSHA256_ComputesCorrectHash()
    {
        var temp = Path.GetTempFileName();
        File.WriteAllText(temp, "hello");
        try
        {
            var result = LoraMetadataDownloadService.ComputeSHA256(temp);
            using var sha = System.Security.Cryptography.SHA256.Create();
            using var stream = File.OpenRead(temp);
            var expected = string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
            result.Should().Be(expected);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
