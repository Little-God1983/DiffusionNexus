using DiffusionNexus.UI.Classes;
using FluentAssertions;

namespace DiffusionNexus.Tests.UI.Classes;

public class CivitaiUrlParserTests
{
    private readonly CivitaiUrlParser _parser = new();

    [Theory]
    [InlineData("https://civitai.com/models/372465?modelVersionId=914390", 372465, 914390)]
    [InlineData("https://civitai.com/models/1153088/retro-ghibli-style-porco-rosso?modelVersionId=2396443", 1153088, 2396443)]
    public void Parses_Model_And_Version(string url, int expectedModel, int expectedVersion)
    {
        var result = _parser.TryParse(url, out var info, out var error);

        result.Should().BeTrue();
        info.Should().NotBeNull();
        info!.ModelId.Should().Be(expectedModel);
        info.ModelVersionId.Should().Be(expectedVersion);
        error.Should().BeNull();
    }

    [Fact]
    public void Parses_Model_With_Slug_Without_Version()
    {
        var url = "https://civitai.com/models/1153088/retro-ghibli-style-porco-rosso";

        var result = _parser.TryParse(url, out var info, out var error);

        result.Should().BeTrue();
        info.Should().NotBeNull();
        info!.ModelId.Should().Be(1153088);
        info.ModelVersionId.Should().BeNull();
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("https://notcivitai.com/models/123")]
    [InlineData("https://civitai.com/foo/123")]
    public void Rejects_Invalid_Urls(string url)
    {
        var result = _parser.TryParse(url, out var info, out var error);

        result.Should().BeFalse();
        info.Should().BeNull();
        error.Should().NotBeNull();
    }

    [Fact]
    public void Handles_Extra_Query_Parameters()
    {
        var url = "https://civitai.com/models/372465?modelVersionId=914390&foo=bar";

        var result = _parser.TryParse(url, out var info, out var error);

        result.Should().BeTrue();
        info.Should().NotBeNull();
        info!.ModelId.Should().Be(372465);
        info.ModelVersionId.Should().Be(914390);
        error.Should().BeNull();
    }
}
