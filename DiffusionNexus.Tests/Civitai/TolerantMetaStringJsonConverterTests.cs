using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Civitai image generation parameters arrive in whatever shape the posting tool wrote them:
/// the same <c>"Hires upscale"</c> is a string on one image and the number <c>1.6</c> on another.
/// The text-typed fields of <see cref="CivitaiImageMeta"/> used the default string converter,
/// which throws on a number, and one such image killed the whole model version: "The JSON value
/// could not be converted to System.String. Path: $.images[2].meta['Hires upscale']" (#627 —
/// version 3129180 downloaded with no metadata, and Download Metadata could not repair it,
/// because the by-hash lookup returns the same images). No meta shape may fail the surrounding
/// payload: numbers read as their JSON text; booleans, objects and arrays as null.
/// </summary>
public class TolerantMetaStringJsonConverterTests
{
    // Minimal form of the #627 payload: the meta values are verbatim from the live
    // /model-versions/3129180 response, image 2 of 4.
    private const string Issue627VersionJson = """
        {"id":3129180,"modelId":2513759,"name":"Krea2","baseModel":"Krea 2","images":[
         {"url":"https://example/a.jpeg","meta":{"prompt":"a","seed":42,"steps":8,"cfgScale":1}},
         {"url":"https://example/b.jpeg","meta":{"prompt":"b","seed":420455612,"steps":8,"cfgScale":1,
          "Hires steps":4,"Hires upscale":1.6,"Hires CFG Scale":1,"Denoising strength":0.15}}]}
        """;

    [Fact]
    public void CivitaiModelVersion_ReadsNumericHiresParametersAsText()
    {
        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(Issue627VersionJson);

        version!.Images.Should().HaveCount(2);
        var meta = version.Images![1].Meta!;
        meta.HiresUpscale.Should().Be("1.6");
        meta.DenoisingStrength.Should().Be("0.15");
        meta.Prompt.Should().Be("b", "the rest of the meta must survive the numeric values");
        meta.Seed.Should().Be(420455612);
    }

    [Fact]
    public async Task GetModelVersionAsync_ReturnsVersionWhoseImageMetaCarriesNumbers()
    {
        // Through the client's own serializer options, which is what failed for the user.
        using var client = new CivitaiClient(
            new HttpClient(new StaticJsonHandler(Issue627VersionJson)), disposeHttpClient: true);

        var version = await client.GetModelVersionAsync(3129180);

        version.Should().NotBeNull();
        version!.ModelId.Should().Be(2513759);
        version.Images![1].Meta!.HiresUpscale.Should().Be("1.6");
    }

    [Fact]
    public async Task GetModelVersionAsync_ReadsNumbersSentAsStrings()
    {
        // A number the posting tool wrote as a string costs nothing: the prompt — the field
        // every consumer copies — and the number itself both survive.
        var version = await ReadThroughClientAsync(
            """{"prompt":"a cat","negativePrompt":"blurry","steps":"20","cfgScale":"7","resources":[{"name":"x","weight":"0.8","hash":12345}]}""");

        var meta = version.Images![1].Meta!;
        meta.Prompt.Should().Be("a cat");
        meta.NegativePrompt.Should().Be("blurry");
        meta.Steps.Should().Be(20);
        meta.CfgScale.Should().Be(7);
        meta.Resources![0].Weight.Should().Be(0.8);
        meta.Resources[0].Hash.Should().Be("12345");
    }

    [Theory]
    [InlineData("""{"prompt":"a cat","steps":20.0,"cfgScale":"7 "}""", 20, 7.0)] // Python's json.dumps(20.0)
    [InlineData("""{"prompt":"a cat","steps":"20.0","cfgScale":7.5}""", 20, 7.5)]
    [InlineData("""{"prompt":"a cat","steps":20.5,"cfgScale":"seven"}""", null, null)] // unusable: that field only
    [InlineData("""{"prompt":"a cat","steps":"","cfgScale":{"v":7}}""", null, null)]
    [InlineData("""{"prompt":"a cat","steps":1e10,"cfgScale":"NaN"}""", null, null)]
    public async Task GetModelVersionAsync_AnUnusableNumberCostsOnlyThatField(
        string metaJson, int? expectedSteps, double? expectedCfgScale)
    {
        var version = await ReadThroughClientAsync(metaJson);

        var meta = version.Images![1].Meta!;
        meta.Prompt.Should().Be("a cat");
        meta.Steps.Should().Be(expectedSteps);
        meta.CfgScale.Should().Be(expectedCfgScale);
    }

    [Fact]
    public async Task GetModelVersionAsync_AnUnusableResourceWeightCostsOnlyThatField()
    {
        var version = await ReadThroughClientAsync(
            """{"prompt":"a cat","resources":[{"name":"x","weight":"0.8:1"},{"name":"y","weight":0.6}]}""");

        var meta = version.Images![1].Meta!;
        meta.Prompt.Should().Be("a cat");
        meta.Resources![0].Weight.Should().BeNull();
        meta.Resources[0].Name.Should().Be("x");
        meta.Resources[1].Weight.Should().Be(0.6);
    }

    [Theory]
    [InlineData("""{"prompt":"b","resources":{"name":"x"}}""")]
    [InlineData("""{"prompt":"b","resources":["lora:foo"]}""")]
    [InlineData("\"Steps: 20, Sampler: Euler\"")] // meta as a raw parameter string
    [InlineData("""["a","b"]""")]
    public async Task GetModelVersionAsync_SurvivesAnyMetaShapeOnOneImage(string badMetaJson)
    {
        // Same rule one level up: a meta this DTO cannot read costs that image its meta,
        // never the version — the other images, and the files the download needs, stay.
        var version = await ReadThroughClientAsync(badMetaJson);

        version.Files.Should().ContainSingle();
        version.Images.Should().HaveCount(3);
        version.Images![0].Meta!.Steps.Should().Be(8);
        version.Images[1].Meta.Should().BeNull();
        version.Images[1].Url.Should().Be("https://example/b.jpeg");
        version.Images[2].Meta!.Prompt.Should().Be("c", "the reader must leave the stream on the next image");
    }

    /// <summary>
    /// A three-image version whose middle image carries <paramref name="metaJson"/>, read through
    /// <see cref="CivitaiClient"/> so its own serializer options are the ones under test.
    /// </summary>
    private static async Task<CivitaiModelVersion> ReadThroughClientAsync(string metaJson)
    {
        var json = $$$"""
            {"id":3129180,"modelId":2513759,"files":[{"name":"f.safetensors"}],"images":[
             {"url":"https://example/a.jpeg","meta":{"prompt":"a","steps":8}},
             {"url":"https://example/b.jpeg","meta":{{{metaJson}}}},
             {"url":"https://example/c.jpeg","meta":{"prompt":"c"}}]}
            """;
        using var client = new CivitaiClient(new HttpClient(new StaticJsonHandler(json)), disposeHttpClient: true);

        var version = await client.GetModelVersionAsync(3129180);

        version.Should().NotBeNull();
        return version!;
    }

    [Theory]
    [InlineData("Clip skip", "2", "2")]
    [InlineData("Hires upscale", "1.6", "1.6")]
    [InlineData("Denoising strength", "0.15", "0.15")]
    [InlineData("Denoising strength", "1e-1", "1e-1")] // the JSON text, not a reformatted double
    [InlineData("Size", "512", "512")]
    [InlineData("prompt", "7", "7")]
    public void CivitaiImageMeta_ReadsScalarsAsTheirJsonText(string field, string valueJson, string expected)
    {
        var meta = JsonSerializer.Deserialize<CivitaiImageMeta>($$"""{"{{field}}":{{valueJson}}}""");

        ReadField(meta!, field).Should().Be(expected);
    }

    [Theory]
    [InlineData("Hires upscale", """{"nested":"object"}""")]
    [InlineData("Hires upscaler", """["a","b"]""")]
    [InlineData("negativePrompt", """{"text":"x"}""")]
    [InlineData("sampler", "true")] // a flag is no sampler: "true" would read as a real one
    [InlineData("Model", "false")]
    // End-to-end only: System.Text.Json answers a JSON null itself and never calls the
    // converter (HandleNull is false for reference types). The rows above reach its Skip.
    [InlineData("Model hash", "null")]
    public void CivitaiImageMeta_ReadsStructuredOrNullValuesAsNull(string field, string valueJson)
    {
        var meta = JsonSerializer.Deserialize<CivitaiImageMeta>(
            $$"""{"{{field}}":{{valueJson}},"Clip skip":"2"}""");

        ReadField(meta!, field).Should().BeNull();
        meta!.ClipSkip.Should().Be("2", "the reader must leave the stream on the next property");
    }

    [Fact]
    public void CivitaiImageMeta_ReadsStringsAsBefore()
    {
        var meta = JsonSerializer.Deserialize<CivitaiImageMeta>(
            """{"prompt":"a cat","Hires upscale":"2","Hires upscaler":"4x-UltraSharp","Size":"832x1216"}""");

        meta!.Prompt.Should().Be("a cat");
        meta.HiresUpscale.Should().Be("2");
        meta.HiresUpscaler.Should().Be("4x-UltraSharp");
        meta.Size.Should().Be("832x1216");
    }

    private static string? ReadField(CivitaiImageMeta meta, string field) => field switch
    {
        "prompt" => meta.Prompt,
        "negativePrompt" => meta.NegativePrompt,
        "sampler" => meta.Sampler,
        "Model" => meta.Model,
        "Model hash" => meta.ModelHash,
        "Size" => meta.Size,
        "Clip skip" => meta.ClipSkip,
        "Hires upscale" => meta.HiresUpscale,
        "Hires upscaler" => meta.HiresUpscaler,
        "Denoising strength" => meta.DenoisingStrength,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
    };

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
