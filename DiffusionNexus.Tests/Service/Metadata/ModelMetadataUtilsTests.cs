using ModelMover.Core.Metadata;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Service.Metadata;

public class ModelMetadataUtilsTests
{
    [Fact]
    public void ParseTags_String_ReturnsTokens()
    {
        var result = ModelMetadataUtils.ParseTags("Tag1, tag2,tag3");
        result.Should().BeEquivalentTo(new[] { "Tag1", "tag2", "tag3" });
    }

    [Fact]
    public void ParseTags_String_EmptyReturnsEmpty()
    {
        ModelMetadataUtils.ParseTags("").Should().BeEmpty();
    }

    [Fact]
    public void ParseTags_JsonElement_ReturnsList()
    {
        using var doc = JsonDocument.Parse("[\"a\",\"b\",\"\",\"c\"]");
        var result = ModelMetadataUtils.ParseTags(doc.RootElement);
        result.Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Fact]
    public void ParseModelType_ValidToken_IsParsed()
    {
        ModelMetadataUtils.ParseModelType("lora").Should().Be(DiffusionTypes.LORA);
    }

    [Fact]
    public void ParseModelType_InvalidToken_ReturnsUnassigned()
    {
        ModelMetadataUtils.ParseModelType("unknown").Should().Be(DiffusionTypes.UNASSIGNED);
    }

    [Fact]
    public void ExtractBaseName_KnownExtension_IsRemoved()
    {
        ModelMetadataUtils.ExtractBaseName("model.preview.JPG").Should().Be("model");
    }

    [Fact]
    public void ExtractBaseName_UnknownExtension_ReturnsInput()
    {
        ModelMetadataUtils.ExtractBaseName("file.unknown").Should().Be("file.unknown");
    }
}
