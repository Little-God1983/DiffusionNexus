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
/// payload: numbers and booleans read as their JSON text, objects and arrays as null.
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

    [Theory]
    [InlineData("Clip skip", "2", "2")]
    [InlineData("Hires upscale", "1.6", "1.6")]
    [InlineData("Denoising strength", "0.15", "0.15")]
    [InlineData("Denoising strength", "1e-1", "1e-1")] // the JSON text, not a reformatted double
    [InlineData("Size", "512", "512")]
    [InlineData("sampler", "true", "true")]
    [InlineData("Model", "false", "false")]
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
    [InlineData("Model hash", "null")]
    public void CivitaiImageMeta_ReadsStructuredOrNullValuesAsNull(string field, string valueJson)
    {
        var meta = JsonSerializer.Deserialize<CivitaiImageMeta>(
            $$"""{"{{field}}":{{valueJson}},"prompt-check":0,"Clip skip":"2"}""");

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
