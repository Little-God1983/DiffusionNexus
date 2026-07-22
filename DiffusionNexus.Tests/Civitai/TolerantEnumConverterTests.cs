using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Pins the malformed-input tolerance of <see cref="TolerantEnumConverter{T}"/>
/// and <see cref="TolerantEnumConverterFactory"/>. The converters exist so that
/// an unknown enum string from Civitai (they add values without warning) reads
/// as <c>default(T)</c> instead of throwing and killing the whole response.
/// </summary>
public class TolerantEnumConverterTests
{
    private enum Color
    {
        Red = 0,
        Green = 1,
        Blue = 2,
    }

    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new TolerantEnumConverterFactory());
        return options;
    }

    private static Color ReadColor(string json) =>
        JsonSerializer.Deserialize<Color>(json, Options());

    [Theory]
    [InlineData("\"Green\"")]
    [InlineData("\"green\"")]   // case-insensitive
    [InlineData("\"GREEN\"")]
    public void Read_ValidStringAnyCase_Parses(string json)
    {
        ReadColor(json).Should().Be(Color.Green);
    }

    [Fact]
    public void Read_UnknownString_FallsBackToDefault()
    {
        ReadColor("\"Chartreuse\"").Should().Be(Color.Red);
    }

    [Fact]
    public void Read_EmptyString_FallsBackToDefault()
    {
        ReadColor("\"\"").Should().Be(Color.Red);
    }

    [Fact]
    public void Read_NullToken_FallsBackToDefault()
    {
        ReadColor("null").Should().Be(Color.Red);
    }

    [Fact]
    public void Read_DefinedNumber_ReturnsEnumValue()
    {
        ReadColor("2").Should().Be(Color.Blue);
    }

    [Fact]
    public void Read_UndefinedNumber_FallsBackToDefault()
    {
        ReadColor("99").Should().Be(Color.Red);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public void Read_NonScalarOrBooleanToken_FallsBackToDefault(string json)
    {
        // The default switch arm skips the token and returns default(T).
        ReadColor(json).Should().Be(Color.Red);
    }

    [Fact]
    public void Write_EmitsEnumName()
    {
        var json = JsonSerializer.Serialize(Color.Blue, Options());
        json.Should().Be("\"Blue\"");
    }

    [Theory]
    [InlineData(typeof(Color), true)]
    [InlineData(typeof(CivitaiModelType), true)]
    [InlineData(typeof(int), false)]
    [InlineData(typeof(string), false)]
    public void Factory_CanConvert_OnlyEnums(Type type, bool expected)
    {
        new TolerantEnumConverterFactory().CanConvert(type).Should().Be(expected);
    }

    [Fact]
    public void RealWorld_UnknownCivitaiModelType_DegradesToUnknown_KeepsRestOfPayload()
    {
        // CivitaiModelType is decorated with the tolerant factory. A brand-new
        // upstream type must not blow up deserialization of the surrounding model.
        const string json = """
            { "id": 7, "name": "Fancy", "type": "SomeBrandNewType", "tags": ["anime"] }
            """;

        var model = JsonSerializer.Deserialize<CivitaiModel>(json);

        model.Should().NotBeNull();
        model!.Type.Should().Be(CivitaiModelType.Unknown);
        model.Id.Should().Be(7);
        model.Name.Should().Be("Fancy");
        model.Tags.Should().ContainSingle().Which.Should().Be("anime");
    }

    [Fact]
    public void RealWorld_KnownCivitaiModelType_Parses()
    {
        var model = JsonSerializer.Deserialize<CivitaiModel>("""{ "id": 1, "type": "LORA" }""");

        model!.Type.Should().Be(CivitaiModelType.LORA);
    }
}
