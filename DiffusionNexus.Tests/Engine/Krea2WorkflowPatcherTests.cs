using System.Text.Json;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.UI.Services.Diffusion;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class Krea2WorkflowPatcherTests
{
    private const string Template = """
    {
      "9":  { "class_type": "VAEDecode", "inputs": { "samples": ["37", 0], "vae": ["57", 0] } },
      "17": { "class_type": "CLIPTextEncode", "inputs": { "text": "OLD POSITIVE", "clip": ["55", 1] } },
      "21": { "class_type": "SaveImage", "inputs": { "filename_prefix": "2026-08-16/Krea-Turbo", "images": ["9", 0] } },
      "35": { "class_type": "CLIPTextEncode", "inputs": { "text": "OLD NEGATIVE", "clip": ["55", 1] } },
      "36": { "class_type": "EmptySD3LatentImage", "inputs": { "width": ["65", 0], "height": ["65", 1], "batch_size": 1 } },
      "37": { "class_type": "KSampler", "inputs": { "seed": 637067905137781, "steps": 8, "cfg": 1, "sampler_name": "euler", "scheduler": "simple", "denoise": 1, "model": ["55", 0], "positive": ["17", 0], "negative": ["35", 0], "latent_image": ["36", 0] } },
      "62": { "class_type": "LoaderGGUF", "inputs": { "gguf_name": "krea2_turbo-Q8_0.gguf" } },
      "65": { "class_type": "AI2GoResolutionSelector", "inputs": { "width": 1000, "height": 1000 } }
    }
    """;

    private static DiffusionRequest Request(
        int width = 1216, int height = 832, int? steps = null, string? negative = null)
        => new()
        {
            ModelKey = "krea2",
            Prompt = "a lighthouse at dusk",
            Width = width,
            Height = height,
            Steps = steps,
            NegativePrompt = negative
        };

    private static JsonElement Inputs(string json, string nodeId)
        => JsonDocument.Parse(json).RootElement.GetProperty(nodeId).GetProperty("inputs").Clone();

    [Fact]
    public void Patch_WritesThePositivePromptIntoNode17()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(), seed: 4242, ggufFileName: null);

        Inputs(patched, "17").GetProperty("text").GetString().Should().Be("a lighthouse at dusk");
    }

    [Fact]
    public void Patch_WritesTheNegativePromptIntoNode35_EmptyWhenUnset()
    {
        Inputs(Krea2WorkflowPatcher.Patch(Template, Request(), 1, null), "35")
            .GetProperty("text").GetString().Should().BeEmpty();

        Inputs(Krea2WorkflowPatcher.Patch(Template, Request(negative: "blurry"), 1, null), "35")
            .GetProperty("text").GetString().Should().Be("blurry");
    }

    [Fact]
    public void Patch_ReplacesTheLinkedResolutionWithLiteralCanvasDimensions()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(1216, 832), seed: 1, ggufFileName: null);
        var latent = Inputs(patched, "36");

        latent.GetProperty("width").ValueKind.Should().Be(JsonValueKind.Number,
            "the AI2GoResolutionSelector link must be replaced, or the canvas size is ignored");
        latent.GetProperty("width").GetInt32().Should().Be(1216);
        latent.GetProperty("height").GetInt32().Should().Be(832);
    }

    [Fact]
    public void Patch_SetsTheSeedAndKeepsTheWorkflowsTunedSamplerSettings()
    {
        var sampler = Inputs(Krea2WorkflowPatcher.Patch(Template, Request(), seed: 4242, ggufFileName: null), "37");

        sampler.GetProperty("seed").GetInt64().Should().Be(4242);
        sampler.GetProperty("steps").GetInt32().Should().Be(8, "8 steps is the turbo model's tuned default");
        sampler.GetProperty("cfg").GetDouble().Should().Be(1);
        sampler.GetProperty("sampler_name").GetString().Should().Be("euler");
    }

    [Fact]
    public void Patch_OverridesStepsAndCfgOnlyWhenTheRequestSuppliesThem()
    {
        var sampler = Inputs(Krea2WorkflowPatcher.Patch(Template, Request(steps: 20), seed: 1, ggufFileName: null), "37");

        sampler.GetProperty("steps").GetInt32().Should().Be(20);
    }

    [Fact]
    public void Patch_RepointsTheGgufLoaderAtTheInstalledQuant()
    {
        var patched = Krea2WorkflowPatcher.Patch(
            Template, Request(), seed: 1, ggufFileName: "krea2_turbo-Q5_K_S.gguf");

        Inputs(patched, "62").GetProperty("gguf_name").GetString()
            .Should().Be("krea2_turbo-Q5_K_S.gguf",
                "a machine that installed a smaller quant has no Q8_0 file");
    }

    [Fact]
    public void Patch_LeavesTheGgufNameAloneWhenNoInstalledQuantWasResolved()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(), seed: 1, ggufFileName: null);

        Inputs(patched, "62").GetProperty("gguf_name").GetString().Should().Be("krea2_turbo-Q8_0.gguf");
    }

    [Fact]
    public void Patch_ReplacesTheHardcodedDatedSavePrefix()
    {
        var patched = Krea2WorkflowPatcher.Patch(Template, Request(), seed: 1, ggufFileName: null);

        Inputs(patched, "21").GetProperty("filename_prefix").GetString()
            .Should().Be("DiffusionNexus/Canvas");
    }

    [Fact]
    public void Patch_FailsLoudlyWhenTheTemplateLosesAnExpectedNode()
    {
        var act = () => Krea2WorkflowPatcher.Patch(
            """{"99":{"class_type":"KSampler","inputs":{}}}""", Request(), seed: 1, ggufFileName: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*17*", "a silently unpatched workflow would generate somebody else's prompt");
    }
}
