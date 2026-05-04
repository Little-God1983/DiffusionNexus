using DiffusionNexus.Domain.Enums;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Enums;

/// <summary>
/// Unit tests for <see cref="BaseModelTypeExtensions.ParseCivitai"/>.
/// </summary>
public class BaseModelTypeExtensionsTests
{
    [Theory]
    [InlineData("SD 1.5", BaseModelType.SD15)]
    [InlineData("SD 2.0", BaseModelType.SD20)]
    [InlineData("SD 2.1", BaseModelType.SD21)]
    [InlineData("SDXL 1.0", BaseModelType.SDXL10)]
    [InlineData("SDXL 0.9", BaseModelType.SDXL09)]
    [InlineData("SDXL Turbo", BaseModelType.SDXLTurbo)]
    [InlineData("SDXL Lightning", BaseModelType.SDXLLightning)]
    [InlineData("Flux.1 D", BaseModelType.Flux1D)]
    [InlineData("Flux.1 S", BaseModelType.Flux1S)]
    [InlineData("Pony", BaseModelType.Pony)]
    [InlineData("Illustrious", BaseModelType.Illustrious)]
    [InlineData("NoobAI", BaseModelType.NoobAI)]
    [InlineData("Hunyuan", BaseModelType.Hunyuan)]
    public void ParseCivitai_MapsKnownStrings_ByConvention(string civitai, BaseModelType expected)
    {
        BaseModelTypeExtensions.ParseCivitai(civitai).Should().Be(expected);
    }

    [Theory]
    [InlineData("sdxl 1.0")]
    [InlineData("SDXL 1.0")]
    [InlineData("sDxL 1.0")]
    public void ParseCivitai_IsCaseInsensitive(string input)
    {
        BaseModelTypeExtensions.ParseCivitai(input).Should().Be(BaseModelType.SDXL10);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseCivitai_ReturnsUnknown_ForNullOrWhitespace(string? input)
    {
        BaseModelTypeExtensions.ParseCivitai(input).Should().Be(BaseModelType.Unknown);
    }

    [Fact]
    public void ParseCivitai_ReturnsOther_ForUnrecognizedString()
    {
        BaseModelTypeExtensions.ParseCivitai("NotAKnownModel").Should().Be(BaseModelType.Other);
    }

    [Fact]
    public void ParseCivitai_StripsSpacesAndDots_BeforeMatching()
    {
        // "S D 1.5" should normalize to "SD15" and parse successfully.
        BaseModelTypeExtensions.ParseCivitai("S D 1.5").Should().Be(BaseModelType.SD15);
    }
}
