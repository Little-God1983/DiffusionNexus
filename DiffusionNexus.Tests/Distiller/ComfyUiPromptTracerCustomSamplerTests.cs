using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

/// <summary>
/// #673 / #675: the modular samplers keep their settings on nodes wired into them. SamplerCustomAdvanced
/// takes a guider (prompts, cfg, model), noise (seed), sampler (sampler name) and sigmas (steps, scheduler,
/// denoise). Ported from the ComfyUI-IntoTheLatent-Utils pack's tests/test_tracer_custom_sampler.py.
/// </summary>
public class ComfyUiPromptTracerCustomSamplerTests
{
    private const string CfgGuider =
        """{"class_type":"CFGGuider","inputs":{"model":["14",0],"positive":["2",0],"negative":["3",0],"cfg":3.5}}""";

    private const string KSamplerSelect = """{"class_type":"KSamplerSelect","inputs":{"sampler_name":"euler"}}""";

    private const string BasicScheduler =
        """{"class_type":"BasicScheduler","inputs":{"model":["14",0],"scheduler":"simple","steps":8,"denoise":1.0}}""";

    private const string Advanced =
        """{"class_type":"SamplerCustomAdvanced","inputs":{"noise":["7",0],"guider":["4",0],"sampler":["5",0],"sigmas":["6",0],"latent_image":["10",0]}}""";

    // The Flux / Krea template: everything but the latent sits on a node wired into SamplerCustomAdvanced.
    private static ImageGenerationData Flux(string guider = CfgGuider, string select = KSamplerSelect,
        string scheduler = BasicScheduler, string sampler = Advanced, string extra = "")
    {
        var json = $$$"""
            {
              "1": {"class_type":"UNETLoader","inputs":{"unet_name":"flux1-krea-dev.safetensors","weight_dtype":"default"}},
              "13": {"class_type":"DualCLIPLoader","inputs":{"clip_name1":"clip_l.safetensors","clip_name2":"t5xxl.safetensors","type":"flux"}},
              "14": {"class_type":"LoraLoaderModelOnly","inputs":{"model":["1",0],"lora_name":"style.safetensors","strength_model":0.8}},
              "2": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat","clip":["13",0]}},
              "3": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry","clip":["13",0]}},
              "4": {{{guider}}},
              "5": {{{select}}},
              "6": {{{scheduler}}},
              "7": {"class_type":"RandomNoise","inputs":{"noise_seed":42}},
              "10": {"class_type":"EmptySD3LatentImage","inputs":{"width":1024,"height":1024,"batch_size":1}},
              "8": {{{sampler}}},
              "11": {"class_type":"VAEDecode","inputs":{"samples":["8",0],"vae":["1",2]}},
              "12": {"class_type":"SaveImage","inputs":{"images":["11",0]}}{{{extra}}}
            }
            """;
        return ComfyUiPromptTracer.Trace(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!, null, 0, 0);
    }

    [Fact]
    public void Cfg_guider_reads_every_field()
    {
        var r = Flux();

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.Cfg.Should().Be(3.5);
        r.Seed.Should().Be(42);
        r.SamplerName.Should().Be("euler");
        r.Scheduler.Should().Be("simple");
        r.Steps.Should().Be(8);
        r.Denoise.Should().Be(1.0);
        r.Checkpoint.Should().Be("flux1-krea-dev");
        r.Loras.Select(l => (l.Name, l.StrengthModel)).Should().Equal(("style", 0.8));
    }

    [Fact]
    public void Cfg_guider_zeroed_negative()
    {
        var r = Flux(
            guider: CfgGuider.Replace("""["3",0]""", """["9",0]"""),
            extra: """
              ,              "9": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["2",0]}}
            """);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeTrue();
    }

    /// <summary>BasicGuider runs without CFG: there is no negative and no CFG value.</summary>
    [Fact]
    public void Basic_guider_has_no_negative_and_no_cfg()
    {
        var r = Flux(guider: """{"class_type":"BasicGuider","inputs":{"model":["14",0],"conditioning":["2",0]}}""");

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeFalse();
        r.Cfg.Should().BeNull();
        r.Checkpoint.Should().Be("flux1-krea-dev");
        r.Loras.Should().ContainSingle();
    }

    /// <summary>DualCFGGuider's scale against the negative (A1111's "CFG scale") is cfg_cond2_negative; cfg_conds is cond1 vs cond2.</summary>
    [Fact]
    public void Dual_cfg_guider()
    {
        var r = Flux(guider: """
            {"class_type":"DualCFGGuider","inputs":{"model":["14",0],"cond1":["2",0],"cond2":["2",0],"negative":["3",0],
             "cfg_conds":5.0,"cfg_cond2_negative":2.0,"style":"regular"}}
            """);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.Cfg.Should().Be(2.0);
    }

    [Fact]
    public void Other_guider_is_read_by_the_cfg_guider_input_names()
    {
        var r = Flux(guider: """
            {"class_type":"PerpNegGuider","inputs":{"model":["14",0],"positive":["2",0],"negative":["3",0],
             "empty_conditioning":["3",0],"cfg":4.0,"neg_scale":1.0}}
            """);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.Cfg.Should().Be(4.0);
        r.Checkpoint.Should().Be("flux1-krea-dev");
    }

    [Fact]
    public void Scheduler_denoise_is_read()
    {
        Flux(scheduler: BasicScheduler.Replace("\"denoise\":1.0", "\"denoise\":0.6")).Denoise.Should().Be(0.6);
    }

    [Fact]
    public void Sigmas_through_a_split_node()
    {
        var r = Flux(
            sampler: Advanced.Replace("""["6",0]""", """["15",1]"""),
            extra: """
              ,              "15": {"class_type":"SplitSigmas","inputs":{"sigmas":["6",0],"step":4}}
            """);

        r.Steps.Should().Be(8);
        r.Scheduler.Should().Be("simple");
    }

    /// <summary>SplitSigmasDenoise's denoise is the split point; steps, scheduler and denoise all come from the scheduler node.</summary>
    [Fact]
    public void Split_sigmas_denoise_is_not_the_img2img_strength()
    {
        var r = Flux(
            sampler: Advanced.Replace("""["6",0]""", """["15",1]"""),
            extra: """
              ,              "15": {"class_type":"SplitSigmasDenoise","inputs":{"sigmas":["6",0],"denoise":0.3}}
            """);

        r.Denoise.Should().Be(1.0);
        r.Steps.Should().Be(8);
        r.Scheduler.Should().Be("simple");
    }

    /// <summary>SamplerCustom has the prompts, cfg and seed itself, but the sampler name and steps on wired nodes.</summary>
    [Fact]
    public void Sampler_custom_follows_sampler_and_sigmas()
    {
        var r = Flux(sampler: """
            {"class_type":"SamplerCustom","inputs":{"model":["14",0],"add_noise":true,"noise_seed":7,"cfg":2.0,
             "positive":["2",0],"negative":["3",0],"sampler":["5",0],"sigmas":["6",0],"latent_image":["10",0]}}
            """);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.Cfg.Should().Be(2.0);
        r.Seed.Should().Be(7);
        r.SamplerName.Should().Be("euler");
        r.Steps.Should().Be(8);
        r.Scheduler.Should().Be("simple");
        r.Checkpoint.Should().Be("flux1-krea-dev");
    }

    [Fact]
    public void Unwired_guider_gives_no_prompts_or_cfg()
    {
        var r = Flux(sampler: Advanced.Replace("""
            "guider":["4",0],
            """, ""));

        r.PositivePrompt.Should().BeNull();
        r.NegativePrompt.Should().BeNull();
        r.Cfg.Should().BeNull();
        r.SamplerName.Should().Be("euler", "the other settings nodes are still wired");
    }

    /// <summary>sigmas from an unknown node fed by an earlier KSampler: that sampler's steps are not this one's.</summary>
    [Fact]
    public void Settings_walk_never_reads_past_a_sampler()
    {
        var r = Flux(
            sampler: Advanced.Replace("""["6",0]""", """["21",0]"""),
            extra: """
              ,              "20": {"class_type":"KSampler","inputs":{"model":["1",0],"positive":["2",0],"negative":["3",0],
                     "latent_image":["10",0],"seed":1,"steps":30,"cfg":7,"sampler_name":"euler","scheduler":"normal"}},
              "21": {"class_type":"SomeSigmasFromLatent","inputs":{"latent":["20",0]}}
            """);

        r.Steps.Should().BeNull();
        r.Scheduler.Should().BeNull();
    }

    /// <summary>#675: SamplerDPMPP_2M_SDE / KarrasScheduler have no sampler_name / scheduler widget; the class says what runs.</summary>
    [Fact]
    public void Dedicated_sampler_and_scheduler_nodes_are_named_by_class()
    {
        var r = Flux(
            select: """{"class_type":"SamplerDPMPP_2M_SDE","inputs":{"solver_type":"midpoint","eta":1.0,"s_noise":1.0,"noise_device":"gpu"}}""",
            scheduler: """{"class_type":"KarrasScheduler","inputs":{"steps":30,"sigma_max":14.6,"sigma_min":0.03,"rho":7.0}}""");

        r.SamplerName.Should().Be("dpmpp_2m_sde");
        r.Scheduler.Should().Be("karras");
        r.Steps.Should().Be(30);
        A1111MetadataFormatter.Build(r, "a cat", null, r.Loras, hashes: null).Should().Contain("Sampler: DPM++ 2M SDE Karras");
    }

    [Fact]
    public void Sampler_node_behind_a_pass_through()
    {
        var r = Flux(
            select: """{"class_type":"SamplerEulerAncestral","inputs":{"eta":1.0,"s_noise":1.0}}""",
            sampler: Advanced.Replace("""["5",0]""", """["16",0]"""),
            extra: """
              ,              "16": {"class_type":"SomeSamplerWrapper","inputs":{"sampler":["5",0]}}
            """);

        r.SamplerName.Should().Be("euler_ancestral");
    }

    [Fact]
    public void Unknown_sampler_node_gives_no_sampler()
    {
        Flux(select: """{"class_type":"SomeCustomSampler","inputs":{"eta":1.0}}""").SamplerName.Should().BeNull();
    }

    [Fact]
    public void Model_specific_scheduler_has_steps_but_no_scheduler_name()
    {
        var r = Flux(scheduler: """{"class_type":"Flux2Scheduler","inputs":{"steps":20,"width":1024,"height":1024}}""");

        r.Steps.Should().Be(20);
        r.Scheduler.Should().BeNull();
    }

    /// <summary>
    /// #673 review: Image Saver's selector nodes feed KSamplerSelect's sampler_name and BasicScheduler's scheduler.
    /// The settings node holds a link there, which is followed to the selector (differs from tracer.py).
    /// </summary>
    [Theory]
    [InlineData("SamplerCustom")]
    [InlineData("SamplerCustomAdvanced")]
    public void Settings_node_values_linked_to_selector_nodes_are_followed(string samplerClass)
    {
        var r = Flux(
            select: """{"class_type":"KSamplerSelect","inputs":{"sampler_name":["20",0]}}""",
            scheduler: """{"class_type":"BasicScheduler","inputs":{"model":["14",0],"scheduler":["21",0],"steps":20,"denoise":1.0}}""",
            sampler: samplerClass == "SamplerCustom"
                ? """{"class_type":"SamplerCustom","inputs":{"model":["14",0],"noise_seed":7,"cfg":6.0,"positive":["2",0],"negative":["3",0],"sampler":["5",0],"sigmas":["6",0]}}"""
                : Advanced,
            extra: """
              ,"20": {"class_type":"Sampler Selector (Image Saver)","inputs":{"sampler_name":"dpmpp_2m"}},
              "21": {"class_type":"Scheduler Selector (Image Saver)","inputs":{"scheduler":"karras"}}
            """);

        r.SamplerName.Should().Be("dpmpp_2m");
        r.Scheduler.Should().Be("karras");
        r.Steps.Should().Be(20);
    }
}
