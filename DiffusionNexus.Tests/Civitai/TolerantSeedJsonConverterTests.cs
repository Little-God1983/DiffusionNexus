using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Civitai gallery images carry generation seeds that went through JavaScript
/// float64 on their way in, so a seed can exceed <c>Int64.MaxValue</c> (a real
/// by-hash response served <c>"seed":12859270413054550000</c> — 1.28e19 against
/// a max of 9.22e18). The default converter throws on such a value, and one
/// oversized seed killed identification of the whole model: "The JSON value
/// could not be converted to System.Nullable`1[System.Int64].
/// Path: $.images[1].meta.seed" (user-reported — two Krea LoRAs failing every
/// sync run). The seed is display-only and already precision-mangled, so no
/// seed shape may ever fail the surrounding payload: in-range integers pass
/// through, everything else reads as null.
/// </summary>
public class TolerantSeedJsonConverterTests
{
    [Fact]
    public void CivitaiModelVersion_ReadsSeedBeyondInt64AsNull()
    {
        // Minimal form of the crashing shape at $.images[1].meta.seed —
        // the oversized seed value is verbatim from the live response.
        var json = """
            {"id":3134500,"name":"v1.0 - 5,500 Steps","images":[
             {"url":"https://example/a.jpeg","meta":{"prompt":"a","seed":993423092662132400}},
             {"url":"https://example/b.jpeg","meta":{"prompt":"b","seed":12859270413054550000}}]}
            """;

        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json);

        version!.Images.Should().HaveCount(2);
        version.Images![0].Meta!.Seed.Should().Be(993423092662132400);
        version.Images[1].Meta!.Seed.Should().BeNull();
        version.Images[1].Meta!.Prompt.Should().Be("b", "the rest of the meta must survive the bad seed");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("-1")]
    [InlineData("42")]
    public void CivitaiImageMeta_ReadsOrdinarySeedShapesAsBefore(string seedJson)
    {
        var meta = JsonSerializer.Deserialize<CivitaiImageMeta>($$"""{"seed":{{seedJson}}}""");

        long? expected = seedJson == "null" ? null : long.Parse(seedJson);
        meta!.Seed.Should().Be(expected);
    }

    [Theory]
    [InlineData(""" "1644221548111640800" """, 1644221548111640800L)] // numeric string → parsed
    [InlineData(""" "not-a-number" """, null)]
    [InlineData("3.7", null)] // fractional — a rounded seed is a lie
    [InlineData("1.2859270413054550e19", null)] // scientific notation beyond Int64
    [InlineData("""{"nested":"object"}""", null)]
    public void CivitaiImageMeta_ReadsUnusableSeedShapesAsNull(string seedJson, long? expected)
    {
        var meta = JsonSerializer.Deserialize<CivitaiImageMeta>($$"""{"seed":{{seedJson.Trim()}}}""");

        meta!.Seed.Should().Be(expected);
    }
}
