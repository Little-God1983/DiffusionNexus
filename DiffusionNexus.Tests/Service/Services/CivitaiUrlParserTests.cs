using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Service.Services;

public class CivitaiUrlParserTests
{
    [Theory]
    [InlineData("https://civitai.com/models/372465?modelVersionId=914390", 372465, 914390)]
    [InlineData("https://civitai.com/models/1153088/retro-ghibli-style-porco-rosso", 1153088, null)]
    public void ParsesValidUrls(string url, int expectedModel, int? expectedVersion)
    {
        var parser = new CivitaiUrlParser();

        var success = parser.TryParse(url, out var info, out _, out var error);

        success.Should().BeTrue();
        error.Should().BeNull();
        info.ModelId.Should().Be(expectedModel);
        info.ModelVersionId.Should().Be(expectedVersion);
    }

    [Fact]
    public void RejectsNonCivitaiHost()
    {
        var parser = new CivitaiUrlParser();

        var success = parser.TryParse("https://example.com/models/123", out _, out _, out var error);

        success.Should().BeFalse();
        error.Should().Contain("civitai.com");
    }

    [Fact]
    public void RejectsInvalidPath()
    {
        var parser = new CivitaiUrlParser();

        var success = parser.TryParse("https://civitai.com/foo/123", out _, out _, out var error);

        success.Should().BeFalse();
        error.Should().Contain("models");
    }
}
