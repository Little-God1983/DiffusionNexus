using System.Text.Json;
using FluentAssertions;
using ModelMover.Core.Metadata;
using DiffusionNexus.Service.Enum;
using DiffusionNexus.Service.Helper;
using Xunit;

namespace DiffusionNexus.Tests.Service.Services;

public class ModelMetadataUtilsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseTags_String_Empty_ReturnsEmpty(string input)
    {
        var result = ModelMetadataUtils.ParseTags(input);
        result.Should().BeEmpty();
    }

    [Fact]
    public void ParseTags_String_MixedDelimiters_Trimmed()
    {
        var result = ModelMetadataUtils.ParseTags("tag1, Tag2;tag3\nTAG4");
        result.Should().Equal(new[] { "tag1", "Tag2", "tag3", "TAG4" });
    }

    [Fact]
    public void ParseTags_JsonElement_ReturnsList()
    {
        var json = "[ \"one\", \"two\", \"\", null ]";
        using var doc = JsonDocument.Parse(json);
        var result = ModelMetadataUtils.ParseTags(doc.RootElement);
        result.Should().Equal(new[] { "one", "two" });
    }

    [Theory]
    [InlineData("LoRA", DiffusionTypes.LORA)]
    [InlineData("vae", DiffusionTypes.VAE)]
    [InlineData("UnknownType", DiffusionTypes.UNASSIGNED)]
    [InlineData("", DiffusionTypes.UNASSIGNED)]
    public void ParseModelType_Various_Correct(string input, DiffusionTypes expected)
    {
        ModelMetadataUtils.ParseModelType(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("model.safetensors", "model")]
    [InlineData("file.preview.png", "file")]
    [InlineData("strange.ext", "strange")] 
    [InlineData("", "")]
    public void ExtractBaseName_KnownExtensions_Trimmed(string fileName, string expected)
    {
        ModelMetadataUtils.ExtractBaseName(fileName).Should().Be(expected);
    }
}
