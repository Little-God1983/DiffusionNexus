using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class CivitaiLinkParserTests
{
    [Theory]
    [InlineData("https://civitai.com/models/372465?modelVersionId=914390", "372465", "914390")]
    [InlineData("https://civitai.com/models/372465/model-versions/914390", "372465", "914390")]
    [InlineData("https://civitai.com/models/372465", "372465", null)]
    [InlineData("https://civitai.com/models/372465/wild-hair", "372465", null)]
    public void TryParse_ValidLinks_ReturnsExpected(string link, string modelId, string? versionId)
    {
        var result = CivitaiLinkParser.TryParse(link, out CivitaiLinkInfo? info);

        result.Should().BeTrue();
        info.Should().NotBeNull();
        info!.ModelId.Should().Be(modelId);
        info.ModelVersionId.Should().Be(versionId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://example.com/models/123")] 
    [InlineData("https://civitai.com/users/1")] 
    public void TryParse_InvalidLinks_ReturnsFalse(string? link)
    {
        var result = CivitaiLinkParser.TryParse(link, out var info);

        result.Should().BeFalse();
        info.Should().BeNull();
    }
}
