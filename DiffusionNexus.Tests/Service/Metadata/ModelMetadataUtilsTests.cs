using DiffusionNexus.Service.Helper;
using ModelMover.Core.Metadata;
using System.Text.Json;
using Xunit;
using FluentAssertions;

namespace DiffusionNexus.Tests.Service.Metadata;

public class ModelMetadataUtilsTests
{
    [Fact]
    public void ParseTags_String_ReturnsDistinctNormalized()
    {
        var result = ModelMetadataUtils.ParseTags("tag1, Tag2,tag1 ,  ");
        result.Should().BeEquivalentTo(new[] { "tag1", "Tag2" });
    }

    [Fact]
    public void ParseTags_String_Empty_ReturnsEmpty()
    {
        ModelMetadataUtils.ParseTags("   ").Should().BeEmpty();
    }

    [Fact]
    public void ParseTags_Json_IgnoresNonStrings()
    {
        var json = "[\"one\", 2, \"two\"]";
        using var doc = JsonDocument.Parse(json);
        var tags = ModelMetadataUtils.ParseTags(doc.RootElement);
        tags.Should().BeEquivalentTo(new[] { "one", "two" });
    }

    [Theory]
    [InlineData("LORA", DiffusionTypes.LORA)]
    [InlineData("  locoN ", DiffusionTypes.LOCON)]
    [InlineData("", DiffusionTypes.UNASSIGNED)]
    [InlineData("unknown", DiffusionTypes.UNASSIGNED)]
    public void ParseModelType_HandlesInputs(string input, DiffusionTypes expected)
    {
        ModelMetadataUtils.ParseModelType(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("model.ckpt", "model.ckpt")]
    [InlineData("model.preview.png", "model")]
    [InlineData("weird.ext", "weird.ext")]
    [InlineData("", "")]
    public void ExtractBaseName_RemovesKnownExtensions(string fileName, string expected)
    {
        ModelMetadataUtils.ExtractBaseName(fileName).Should().Be(expected);
    }
}
