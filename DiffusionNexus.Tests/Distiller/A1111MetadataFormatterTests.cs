using System.Collections.Generic;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class A1111MetadataFormatterTests
{
    private static ImageGenerationData Data() => new()
    {
        Width = 1024, Height = 1536, Checkpoint = "sd_xl_base",
        SamplerName = "dpmpp_2m", Scheduler = "karras", Steps = 28, Cfg = 4.5, Seed = 88213105, HasData = true
    };

    [Fact]
    public void Build_appends_lora_tokens_and_maps_sampler_name()
    {
        var loras = new List<LoraInfo> { new() { Name = "styleB", StrengthModel = 0.8 } };

        var s = A1111MetadataFormatter.Build(Data(), "cinematic portrait", "blurry", loras, hashes: null);

        s.Should().StartWith("cinematic portrait <lora:styleB:0.8>");
        s.Should().Contain("Negative prompt: blurry");
        s.Should().Contain("Sampler: DPM++ 2M Karras");
        s.Should().NotContain("Schedule type:"); // scheduler is folded into the combined Sampler name
        s.Should().Contain("Steps: 28");
        s.Should().Contain("CFG scale: 4.5");
        s.Should().Contain("Size: 1024x1536");
        s.Should().Contain("Model: sd_xl_base");
        s.Should().Contain("Version: ComfyUI");
        s.Should().NotContain("Hashes:");
        s.Should().NotContain("Model hash:");
    }

    [Theory]
    [InlineData("euler", "normal", "Euler")]              // plain scheduler -> no suffix
    [InlineData("euler", "simple", "Euler")]              // simple is also "plain"
    [InlineData("euler_ancestral", "karras", "Euler a Karras")]
    [InlineData("dpmpp_2m_sde", "karras", "DPM++ 2M SDE Karras")]
    [InlineData("dpmpp_sde", "exponential", "DPM++ SDE Exponential")]
    [InlineData("my_custom_sampler", "karras", "My Custom Sampler Karras")] // unmapped -> title-case
    [InlineData("uni_pc", "sgm_uniform", "UniPC")]        // uni_pc -> UniPC, sgm_uniform is plain
    [InlineData("dpmpp_2m_sde", "beta", "DPM++ 2M SDE")]  // #675: BetaSamplingScheduler is plain
    [InlineData("dpmpp_2m", "align_your_steps", "DPM++ 2M align_your_steps")] // #675: unknown scheduler appended as is
    public void Build_maps_sampler_and_scheduler_to_combined_a1111_name(string sampler, string scheduler, string expected)
    {
        var data = Data() with { SamplerName = sampler, Scheduler = scheduler };

        var s = A1111MetadataFormatter.Build(data, "p", null, [], hashes: null);

        s.Should().Contain($"Sampler: {expected}");
        s.Should().NotContain("Schedule type:");
    }

    [Fact]
    public void Build_emits_hashes_block_when_supplied()
    {
        var loras = new List<LoraInfo> { new() { Name = "styleB", StrengthModel = 0.8 } };
        var hashes = new ResourceHashes("abc1234567",
            new Dictionary<string, string> { ["styleB"] = "def8901234" });

        var s = A1111MetadataFormatter.Build(Data(), "p", null, loras, hashes);

        s.Should().Contain("Model hash: abc1234567");
        s.Should().Contain("Hashes: {");
        s.Should().Contain("\"model\":\"abc1234567\"");
        s.Should().Contain("\"lora:styleB\":\"def8901234\"");
    }

    [Fact]
    public void Build_omits_negative_line_when_negative_is_empty()
    {
        var s = A1111MetadataFormatter.Build(Data(), "p", null, [], hashes: null);

        s.Should().NotContain("Negative prompt:");
    }

    [Fact]
    public void Build_omits_sampler_line_when_sampler_name_missing()
    {
        var data = Data() with { SamplerName = null };
        var s = A1111MetadataFormatter.Build(data, "p", null, [], hashes: null);
        s.Should().NotContain("Sampler:");
    }

    /// <summary>#674: Flux's built-in guidance goes after the real CFG under Forge's name, as the pack's a1111.format_parameters writes it.</summary>
    [Fact]
    public void Build_writes_flux_guidance_as_distilled_cfg_scale_after_the_cfg()
    {
        var data = Data() with { Cfg = 1, Guidance = 3.5, Seed = 1 };

        A1111MetadataFormatter.Build(data, "cat", null, [], hashes: null)
            .Should().Contain("CFG scale: 1, Distilled CFG Scale: 3.5, Seed: 1");
    }

    [Fact]
    public void Build_without_guidance_writes_no_distilled_cfg_scale()
    {
        A1111MetadataFormatter.Build(Data(), "cat", null, [], hashes: null).Should().NotContain("Distilled");
    }

    [Fact]
    public void Parse_reads_distilled_cfg_scale_as_the_guidance()
    {
        var data = ImageMetadataParser.ParseA1111Parameters(
            "cat\nNegative prompt: \nSteps: 8, Sampler: Euler, CFG scale: 1, Distilled CFG Scale: 3.5, Seed: 1", null, 0, 0);

        data.Guidance.Should().Be(3.5);
        data.Cfg.Should().Be(1);
    }

    [Fact]
    public void Parse_without_distilled_cfg_scale_has_no_guidance()
    {
        ImageMetadataParser.ParseA1111Parameters("cat\nSteps: 8, CFG scale: 7, Seed: 1", null, 0, 0).Guidance.Should().BeNull();
    }

    [Fact]
    public void Guidance_round_trips_through_the_text()
    {
        var text = A1111MetadataFormatter.Build(Data() with { Cfg = 1, Guidance = 3.5 }, "cat", "blurry", [], hashes: null);

        var back = ImageMetadataParser.ParseA1111Parameters(text, null, 0, 0);

        back.Cfg.Should().Be(1);
        back.Guidance.Should().Be(3.5);
    }
}
