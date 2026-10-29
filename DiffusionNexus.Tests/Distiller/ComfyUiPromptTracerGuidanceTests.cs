using System.Collections.Generic;
using System.Text.Json;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

/// <summary>
/// #674: Flux's built-in guidance sits on the positive conditioning (FluxGuidance, or CLIPTextEncodeFlux's own
/// guidance), not on the sampler. Ported from the ComfyUI-IntoTheLatent-Utils pack's tests/test_tracer_guidance.py.
/// </summary>
public class ComfyUiPromptTracerGuidanceTests
{
    // POSITIVE / NEGATIVE are the KSampler's links, EXTRA more nodes; SAMPLER replaces the KSampler.
    private const string KSamplerFlux = """
        {
          "1": {"class_type":"UNETLoader","inputs":{"unet_name":"flux1-dev.safetensors","weight_dtype":"default"}},
          "13": {"class_type":"DualCLIPLoader","inputs":{"clip_name1":"clip_l.safetensors","clip_name2":"t5xxl.safetensors","type":"flux"}},
          "2": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat","clip":["13",0]}},
          "3": {"class_type":"CLIPTextEncode","inputs":{"text":"","clip":["13",0]}},
          "4": SAMPLER,
          "6": {"class_type":"VAEDecode","inputs":{"samples":["4",0]}},
          "7": {"class_type":"SaveImage","inputs":{"images":["6",0]}}EXTRA
        }
        """;

    private const string KSampler = """
        {"class_type":"KSampler","inputs":{"model":["1",0],"positive":POSITIVE,"negative":NEGATIVE,
         "seed":1,"steps":20,"cfg":1.0,"sampler_name":"euler","scheduler":"simple"}}
        """;

    private static ImageGenerationData Trace(string positive = """["2",0]""", string negative = """["3",0]""",
        string extra = "", string? sampler = null)
    {
        var json = KSamplerFlux
            .Replace("SAMPLER", sampler ?? KSampler)
            .Replace("POSITIVE", positive).Replace("NEGATIVE", negative)
            .Replace("EXTRA", extra);
        return ComfyUiPromptTracer.Trace(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!, null, 0, 0);
    }

    [Fact]
    public void Flux_guidance_on_the_positive()
    {
        var r = Trace(positive: """["8",0]""", extra: """
              ,"8": {"class_type":"FluxGuidance","inputs":{"conditioning":["2",0],"guidance":3.5}}
            """);

        r.Guidance.Should().Be(3.5);
        r.Cfg.Should().Be(1.0);
        r.PositivePrompt.Should().Be("a cat");
    }

    /// <summary>Flux Kontext: FluxGuidance(ReferenceLatent(enc, latent)).</summary>
    [Fact]
    public void Flux_guidance_behind_reference_latent()
    {
        var r = Trace(positive: """["8",0]""", extra: """
              ,"9": {"class_type":"ReferenceLatent","inputs":{"conditioning":["2",0],"latent":["10",0]}},
              "10": {"class_type":"VAEEncode","inputs":{"pixels":["11",0],"vae":["1",2]}},
              "11": {"class_type":"LoadImage","inputs":{"image":"ref.png"}},
              "8": {"class_type":"FluxGuidance","inputs":{"conditioning":["9",0],"guidance":2.5}}
            """);

        r.Guidance.Should().Be(2.5);
    }

    [Fact]
    public void Clip_text_encode_flux_carries_its_own_guidance()
    {
        var r = Trace(positive: """["8",0]""", extra: """
              ,"8": {"class_type":"CLIPTextEncodeFlux","inputs":{"clip":["13",0],"clip_l":"a cat","t5xxl":"a cat","guidance":4.0}}
            """);

        r.Guidance.Should().Be(4.0);
    }

    [Fact]
    public void Flux_disable_guidance_means_none()
    {
        var r = Trace(positive: """["8",0]""", extra: """
              ,"9": {"class_type":"FluxGuidance","inputs":{"conditioning":["2",0],"guidance":3.5}},
              "8": {"class_type":"FluxDisableGuidance","inputs":{"conditioning":["9",0]}}
            """);

        r.Guidance.Should().BeNull();
    }

    [Fact]
    public void Guidance_is_read_from_the_positive_only()
    {
        var r = Trace(negative: """["8",0]""", extra: """
              ,"8": {"class_type":"FluxGuidance","inputs":{"conditioning":["3",0],"guidance":3.5}}
            """);

        r.Guidance.Should().BeNull();
    }

    [Fact]
    public void Wired_guidance_is_unknown()
    {
        var r = Trace(positive: """["8",0]""", extra: """
              ,"8": {"class_type":"FluxGuidance","inputs":{"conditioning":["2",0],"guidance":["20",0]}},
              "20": {"class_type":"PrimitiveFloat","inputs":{"value":3.5}}
            """);

        r.Guidance.Should().BeNull();
    }

    [Fact]
    public void Basic_guider_flux_template()
    {
        var r = Trace(
            sampler: """{"class_type":"SamplerCustomAdvanced","inputs":{"guider":["5",0]}}""",
            extra: """
              ,"8": {"class_type":"FluxGuidance","inputs":{"conditioning":["2",0],"guidance":3.5}},
              "5": {"class_type":"BasicGuider","inputs":{"model":["1",0],"conditioning":["8",0]}}
            """);

        r.Guidance.Should().Be(3.5);
        r.Cfg.Should().BeNull();
        r.PositivePrompt.Should().Be("a cat");
    }

    [Fact]
    public void No_flux_nodes_no_guidance()
    {
        Trace().Guidance.Should().BeNull();
    }

    /// <summary>#674 review: like the pack, only a plain CLIPTextEncode ends the walk; CLIPTextEncodeControlnet passes conditioning on.</summary>
    [Fact]
    public void Flux_guidance_behind_clip_text_encode_controlnet()
    {
        var r = Trace(positive: """["9",0]""", extra: """
              ,"8": {"class_type":"FluxGuidance","inputs":{"conditioning":["2",0],"guidance":3.5}},
              "9": {"class_type":"CLIPTextEncodeControlnet","inputs":{"clip":["13",0],"conditioning":["8",0],"text":"cn"}}
            """);

        r.Guidance.Should().Be(3.5);
    }
}
