using System.Collections.Generic;
using System.Text.Json;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class ComfyUiPromptTracerTests
{
    private static Dictionary<string, JsonElement> Graph(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    /// <summary>
    /// #670: at CFG 1 the negative is often ConditioningZeroOut of the positive encode. Walking
    /// through it reached the positive CLIPTextEncode, so the Distiller wrote the positive prompt as
    /// the negative. A zeroed negative has no text.
    /// </summary>
    [Fact]
    public void Zeroed_out_negative_has_no_prompt()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{
                 "model":["4",0],"positive":["6",0],"negative":["8",0],
                 "steps":8,"cfg":1.0,"seed":5,"sampler_name":"euler","scheduler":"simple","denoise":1.0}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"m.safetensors"}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat"}},
          "8": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["6",0]}},
          "9": {"class_type":"SaveImage","inputs":{"images":["3",0]}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeTrue();
    }

    private const string WanI2V = """
        {
          "1": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat"}},
          "2": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry"}},
          "3": {"class_type":"WanImageToVideo","inputs":{"positive":["1",0],"negative":NEGATIVE}},
          "4": {"class_type":"KSamplerAdvanced","inputs":{
                 "model":["5",0],"positive":["3",0],"negative":["3",1],
                 "steps":4,"cfg":1.0,"noise_seed":5,"sampler_name":"euler","scheduler":"simple"}},
          "5": {"class_type":"UNETLoader","inputs":{"unet_name":"wan.safetensors"}},
          "6": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["1",0]}},
          "9": {"class_type":"SaveImage","inputs":{"images":["4",0]}}
        }
        """;

    /// <summary>
    /// #670 review: WanImageToVideo / ControlNetApplyAdvanced carry both conditionings. The walk from the
    /// sampler's negative followed their "positive" input too and returned the positive prompt.
    /// </summary>
    [Fact]
    public void Negative_through_a_node_carrying_both_conditionings_is_the_negative()
    {
        var r = ComfyUiPromptTracer.Trace(Graph(WanI2V.Replace("NEGATIVE", """["2",0]""")), null, 0, 0);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
    }

    [Fact]
    public void Zeroed_negative_through_a_node_carrying_both_conditionings_has_no_prompt()
    {
        var r = ComfyUiPromptTracer.Trace(Graph(WanI2V.Replace("NEGATIVE", """["6",0]""")), null, 0, 0);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().BeNull();
    }

    [Fact]
    public void Plain_checkpoint_no_lora()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{
                 "model":["4",0],"positive":["6",0],"negative":["7",0],
                 "steps":20,"cfg":7.0,"seed":123,"sampler_name":"euler","scheduler":"normal","denoise":1.0}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"sub/sd_xl.safetensors"}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat"}},
          "7": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry"}},
          "9": {"class_type":"SaveImage","inputs":{"images":["3",0]}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, "x.png", 512, 768);

        r.HasData.Should().BeTrue();
        r.Checkpoint.Should().Be("sd_xl");
        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.NegativeZeroedOut.Should().BeFalse();
        r.Steps.Should().Be(20);
        r.Cfg.Should().Be(7.0);
        r.Seed.Should().Be(123);
        r.SamplerName.Should().Be("euler");
        r.Scheduler.Should().Be("normal");
        r.Loras.Should().BeEmpty();
    }

    [Fact]
    public void LoraLoader_chain_is_in_load_order()
    {
        // sampler <- A <- B <- checkpoint  => load order [B, A] (B nearest checkpoint)
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["10",0],"positive":["6",0],"negative":["7",0]}},
          "10":{"class_type":"LoraLoader","inputs":{"lora_name":"A.safetensors","strength_model":0.8,"model":["11",0]}},
          "11":{"class_type":"LoraLoader","inputs":{"lora_name":"B.safetensors","strength_model":0.6,"model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":"p"}},
          "7": {"class_type":"CLIPTextEncode","inputs":{"text":"n"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("B", "A");
        r.Loras[1].StrengthModel.Should().Be(0.8);
        r.Loras[0].Source.Should().Be("LoraLoader");
        r.Checkpoint.Should().Be("base");
    }

    [Fact]
    public void PowerLoraLoader_skips_disabled_and_none()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["20",0]}},
          "20":{"class_type":"Power Lora Loader (rgthree)","inputs":{
                 "lora_1":{"on":true,"lora":"style.safetensors","strength":0.7},
                 "lora_2":{"on":false,"lora":"off.safetensors","strength":1.0},
                 "lora_3":{"on":true,"lora":"None","strength":1.0},
                 "model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("style");
        r.Loras[0].StrengthModel.Should().Be(0.7);
        r.Loras[0].Source.Should().Be("Power Lora");
    }

    [Fact]
    public void LoraLoaderStack_skips_none_and_zero_strength()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["30",0]}},
          "30":{"class_type":"Lora Loader Stack (rgthree)","inputs":{
                 "lora_01":"a.safetensors","strength_01":0.5,
                 "lora_02":"None","strength_02":1.0,
                 "lora_03":"b.safetensors","strength_03":0,
                 "model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("a");
        r.Loras[0].Source.Should().Be("Lora Stack");
    }

    [Fact]
    public void LoraLoaderStack_preserves_within_node_load_order()
    {
        // Two surviving entries; listed order lora_01=first, lora_02=second.
        // Within-node reversal + global reversal must yield listed order [first, second].
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["30",0]}},
          "30":{"class_type":"Lora Loader Stack (rgthree)","inputs":{
                 "lora_01":"first.safetensors","strength_01":0.4,
                 "lora_02":"second.safetensors","strength_02":0.6,
                 "model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("first", "second");
    }

    [Fact]
    public void Mixed_power_lora_and_stock_loader_load_order()
    {
        // sampler <- PowerLora(A,B) <- LoraLoader(C) <- checkpoint  => [C, A, B]
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["20",0]}},
          "20":{"class_type":"Power Lora Loader (rgthree)","inputs":{
                 "lora_1":{"on":true,"lora":"A.safetensors","strength":0.5},
                 "lora_2":{"on":true,"lora":"B.safetensors","strength":0.6},
                 "model":["21",0]}},
          "21":{"class_type":"LoraLoader","inputs":{"lora_name":"C.safetensors","strength_model":0.7,"model":["4",0]}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Loras.Select(l => l.Name).Should().Equal("C", "A", "B");
    }

    [Fact]
    public void KSamplerAdvanced_uses_noise_seed()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSamplerAdvanced","inputs":{"model":["4",0],"noise_seed":999,"steps":30,"cfg":5.0}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Seed.Should().Be(999);
        r.Steps.Should().Be(30);
    }

    [Fact]
    public void Linked_text_resolves_through_primitive_string_node()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["4",0],"positive":["6",0]}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":["8",0]}},
          "8": {"class_type":"PrimitiveNode","inputs":{"value":"resolved text"}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.PositivePrompt.Should().Be("resolved text");
    }

    [Fact]
    public void UNETLoader_diffusion_model_is_recognized()
    {
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["40",0]}},
          "40":{"class_type":"UNETLoader","inputs":{"unet_name":"flux-klein.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.Checkpoint.Should().Be("flux-klein");
    }

    [Fact]
    public void No_sampler_returns_no_data()
    {
        var g = Graph("""{ "1": {"class_type":"LoadImage","inputs":{"image":"x.png"}} }""");

        var r = ComfyUiPromptTracer.Trace(g, "x.png", 10, 10);

        r.HasData.Should().BeFalse();
    }

    [Fact]
    public void AI2GoPromptBatch_resolves_positive_and_negative_by_output_slot()
    {
        // positive CLIPTextEncode.text -> [batch, slot 0]; negative -> [batch, slot 1].
        // The batch node holds a JSON array of {positive,negative} + an index (1 here).
        var g = Graph("""
        {
          "3": {"class_type":"KSampler","inputs":{"model":["4",0],"positive":["6",0],"negative":["7",0],"sampler_name":"euler","scheduler":"normal","steps":20,"cfg":7.0}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":["9",0]}},
          "7": {"class_type":"CLIPTextEncode","inputs":{"text":["9",1]}},
          "9": {"class_type":"AI2GoPromptBatch","inputs":{"prompts_json":"[{\"positive\":\"a red fox\",\"negative\":\"blurry\"},{\"positive\":\"a blue cat\",\"negative\":\"lowres\"}]","index":1}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.PositivePrompt.Should().Be("a blue cat");   // index 1, slot 0
        r.NegativePrompt.Should().Be("lowres");        // index 1, slot 1 (NOT equal to positive)
        r.PositivePrompt.Should().NotContain("{");     // not the raw prompts_json blob
    }

    [Fact]
    public void Multi_branch_graph_picks_the_sampler_feeding_the_save_node()
    {
        // Two aspect-ratio branches share one checkpoint. A CUSTOM save node (not a core SaveImage)
        // is wired to branch B's VAEDecode. The picked sampler must be B (feeding the saved image),
        // NOT the first sampler in the graph (A) — that was the pre-fix behaviour.
        var g = Graph("""
        {
          "1": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}},
          "2": {"class_type":"KSampler","inputs":{"model":["1",0],"seed":111,"sampler_name":"euler","scheduler":"simple","steps":8,"cfg":1.0}},
          "3": {"class_type":"KSampler","inputs":{"model":["1",0],"seed":222,"sampler_name":"dpmpp_2m","scheduler":"karras","steps":20,"cfg":7.0}},
          "4": {"class_type":"VAEDecode","inputs":{"samples":["3",0]}},
          "5": {"class_type":"AI2GoSaveCivitaiMetadata","inputs":{"images":["4",0]}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, "x.png", 1024, 1024);

        r.Seed.Should().Be(222);
        r.SamplerName.Should().Be("dpmpp_2m");
        r.Steps.Should().Be(20);
    }

    [Fact]
    public void Sampler_name_link_is_followed_to_a_selector_node()
    {
        // A plain KSampler whose sampler_name input is a LINK to a "Sampler Selector" node holding
        // the literal — resolve it rather than leaving the sampler empty.
        var g = Graph("""
        {
          "1": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}},
          "2": {"class_type":"KSampler","inputs":{"model":["1",0],"seed":5,"sampler_name":["9",0],"scheduler":"karras","steps":8,"cfg":1.0}},
          "9": {"class_type":"Sampler Selector","inputs":{"sampler_name":"dpmpp_2m"}},
          "3": {"class_type":"SaveImage","inputs":{"images":["4",0]}},
          "4": {"class_type":"VAEDecode","inputs":{"samples":["2",0]}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.SamplerName.Should().Be("dpmpp_2m");
    }

    [Fact]
    public void SamplerCustom_recovers_sampler_and_scheduler_from_linked_nodes()
    {
        // SamplerCustom has no sampler_name/scheduler widgets — they live on KSamplerSelect / BasicScheduler
        // wired into the sampler/sigmas link inputs.
        var g = Graph("""
        {
          "3": {"class_type":"SamplerCustom","inputs":{"model":["4",0],"positive":["6",0],"negative":["7",0],"sampler":["10",0],"sigmas":["11",0]}},
          "10":{"class_type":"KSamplerSelect","inputs":{"sampler_name":"dpmpp_2m"}},
          "11":{"class_type":"BasicScheduler","inputs":{"scheduler":"karras","steps":28,"denoise":1.0,"model":["4",0]}},
          "6": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat"}},
          "7": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry"}},
          "4": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"base.safetensors"}}
        }
        """);

        var r = ComfyUiPromptTracer.Trace(g, null, 0, 0);

        r.SamplerName.Should().Be("dpmpp_2m");
        r.Scheduler.Should().Be("karras");
        r.Steps.Should().Be(28);
        r.PositivePrompt.Should().Be("a cat");
    }

    // #672: KSampler graph from the pack's test_tracer_prompts.py; NEGATIVE is the sampler's negative link, EXTRA more nodes.
    private const string KSamplerGraph = """
        {
          "1": {"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"m.safetensors"}},
          "2": {"class_type":"CLIPTextEncode","inputs":{"text":"a cat","clip":["1",1]}},
          "3": {"class_type":"CLIPTextEncode","inputs":{"text":"blurry","clip":["1",1]}},
          "4": {"class_type":"KSampler","inputs":{
                 "model":["1",0],"positive":POSITIVE,"negative":NEGATIVE,
                 "seed":1,"steps":20,"cfg":7,"sampler_name":"euler","scheduler":"normal"}},
          "6": {"class_type":"VAEDecode","inputs":{"samples":["4",0],"vae":["1",2]}},
          "7": {"class_type":"SaveImage","inputs":{"images":["6",0]}}EXTRA
        }
        """;

    private static ImageGenerationData TraceKSampler(string negative, string extra, string positive = """["2",0]""") =>
        ComfyUiPromptTracer.Trace(Graph(KSamplerGraph.Replace("POSITIVE", positive).Replace("NEGATIVE", negative)
            .Replace("EXTRA", extra)), null, 0, 0);

    /// <summary>#672: one branch is zeroed, the other ends at an encoder the tracer can't read. The negative is unknown, not empty.</summary>
    [Fact]
    public void Zero_out_beside_a_clip_fed_encoder_is_unknown_not_zeroed()
    {
        var r = TraceKSampler("""["10",0]""", """
          ,          "8": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["2",0]}},
          "9": {"class_type":"ImpactWildcardEncode","inputs":{"wildcard_text":"blurry","model":["1",0],"clip":["1",1]}},
          "10": {"class_type":"ConditioningCombine","inputs":{"conditioning_1":["8",0],"conditioning_2":["9",2]}}
        """);

        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeFalse();
    }

    [Fact]
    public void Zero_out_beside_a_TextEncode_class_is_unknown_not_zeroed()
    {
        var r = TraceKSampler("""["10",0]""", """
          ,          "8": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["2",0]}},
          "9": {"class_type":"TextEncodeQwenImageEdit","inputs":{"prompt":"blurry","vae":["1",2]}},
          "10": {"class_type":"ConditioningCombine","inputs":{"conditioning_1":["8",0],"conditioning_2":["9",0]}}
        """);

        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeFalse();
    }

    [Fact]
    public void Zero_out_beside_a_missing_node_is_unknown_not_zeroed()
    {
        var r = TraceKSampler("""["10",0]""", """
          ,          "8": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["2",0]}},
          "10": {"class_type":"ConditioningCombine","inputs":{"conditioning_1":["8",0],"conditioning_2":["99",0]}}
        """);

        r.NegativeZeroedOut.Should().BeFalse();
    }

    /// <summary>#672: Flux Kontext. The latent branch ends at LoadImage, which says nothing about the text.</summary>
    [Fact]
    public void Zeroed_negative_through_a_latent_carrier_is_zeroed()
    {
        var r = TraceKSampler("""["11",0]""", """
          ,          "8": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["2",0]}},
          "9": {"class_type":"LoadImage","inputs":{"image":"ref.png"}},
          "10": {"class_type":"VAEEncode","inputs":{"pixels":["9",0],"vae":["1",2]}},
          "11": {"class_type":"ReferenceLatent","inputs":{"conditioning":["8",0],"latent":["10",0]}}
        """);

        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeTrue();
    }

    /// <summary>#672: hook nodes carry both conditionings under suffixed names (positive_NEW / negative_NEW, *_A / *_B).</summary>
    [Fact]
    public void Pair_conditioning_follows_the_suffixed_inputs()
    {
        var r = TraceKSampler("""["9",1]""", """
          ,          "8": {"class_type":"PairConditioningSetProperties","inputs":{"positive_NEW":["2",0],"negative_NEW":["3",0],"strength":1.0,"set_cond_area":"default"}},
          "9": {"class_type":"PairConditioningCombine","inputs":{"positive_A":["8",0],"negative_A":["8",1],"positive_B":["2",0],"negative_B":["3",0]}}
        """, positive: """["9",0]""");

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
    }

    [Fact]
    public void Pair_conditioning_set_properties_alone_gives_its_negative()
    {
        var r = TraceKSampler("""["8",1]""", """
          ,          "8": {"class_type":"PairConditioningSetProperties","inputs":{"positive_NEW":["2",0],"negative_NEW":["3",0],"strength":1.0,"set_cond_area":"default"}}
        """, positive: """["8",0]""");

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
    }

    /// <summary>#672: a carrier whose optional negative is unwired (so absent from the API prompt) must not fall back to its positive.</summary>
    [Fact]
    public void Carrier_without_the_matching_input_never_reads_the_other_side()
    {
        var r = TraceKSampler("""["8",1]""", """
          ,          "8": {"class_type":"SomeCarrier","inputs":{"positive":["2",0],"strength":1.0}}
        """);

        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeFalse();
    }

    /// <summary>#672: two stages. The stage-2 negative's latent branch must not reach the stage-1 sampler and report its negative.</summary>
    [Fact]
    public void Walk_stops_at_an_earlier_sampler()
    {
        var r = TraceKSampler("""["11",0]""", """
          ,          "20": {"class_type":"CLIPTextEncode","inputs":{"text":"stage1 blurry","clip":["1",1]}},
          "21": {"class_type":"KSampler","inputs":{"model":["1",0],"positive":["2",0],"negative":["20",0],
                 "seed":1,"steps":30,"cfg":7,"sampler_name":"euler","scheduler":"normal"}},
          "22": {"class_type":"VAEDecode","inputs":{"samples":["21",0],"vae":["1",2]}},
          "23": {"class_type":"VAEEncode","inputs":{"pixels":["22",0],"vae":["1",2]}},
          "8": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["2",0]}},
          "11": {"class_type":"ReferenceLatent","inputs":{"conditioning":["8",0],"latent":["23",0]}}
        """);

        r.NegativePrompt.Should().BeNull();
        r.NegativeZeroedOut.Should().BeTrue();
        r.Steps.Should().Be(20, "the traced sampler is still the one feeding the save node");
    }

    /// <summary>#670 regression in the #672 graph: ComfyUI's SD3 template, Combine(SetTimestepRange(ZeroOut(enc)), SetTimestepRange(enc)).</summary>
    [Fact]
    public void Sd3_template_combine_keeps_the_real_negative()
    {
        var r = TraceKSampler("""["11",0]""", """
          ,          "8": {"class_type":"ConditioningZeroOut","inputs":{"conditioning":["3",0]}},
          "9": {"class_type":"ConditioningSetTimestepRange","inputs":{"conditioning":["8",0],"start":0.1,"end":1.0}},
          "10": {"class_type":"ConditioningSetTimestepRange","inputs":{"conditioning":["3",0],"start":0.0,"end":0.1}},
          "11": {"class_type":"ConditioningCombine","inputs":{"conditioning_1":["9",0],"conditioning_2":["10",0]}}
        """);

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.NegativeZeroedOut.Should().BeFalse();
    }

    [Fact]
    public void Plain_negative_encode_is_read()
    {
        var r = TraceKSampler("""["3",0]""", "");

        r.PositivePrompt.Should().Be("a cat");
        r.NegativePrompt.Should().Be("blurry");
        r.NegativeZeroedOut.Should().BeFalse();
    }
}
