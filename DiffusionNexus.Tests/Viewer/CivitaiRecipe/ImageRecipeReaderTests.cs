using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #628: prompt and settings come from the graph trace when it succeeds, otherwise from the
/// parameters text; LoRA weights from the graph's loader nodes, else from the text's tags. A value
/// that is not in the file is unknown — never a default.
/// </summary>
public sealed class ImageRecipeReaderTests
{
    private static ImageRecipeData Read(params (string Key, string Value)[] texts) =>
        ImageRecipeReader.FromTexts(new RecipeFileText(RecipeFileFormat.Png, texts.ToDictionary(t => t.Key, t => t.Value)));

    [Fact]
    public void ExampleFile_SettingsFromText_WeightsFromGraph_HashesFromText()
    {
        var data = Read(
            ("prompt", ComfyLoraScanTests.Fixture("143992601-prompt.json")),
            ("parameters", ComfyLoraScanTests.Fixture("143992601-parameters.txt")));

        data.Source.Should().Be(RecipeDataSource.SettingsText, "ClownsharKSampler: the shared tracer finds no core sampler");
        data.SourceNote.Should().Be("Read from the original image file (settings text; LoRA weights from the ComfyUI workflow)");
        data.Prompt.Should().StartWith("Photograph of a solitary cybernetic sniper");
        data.Steps.Should().Be(8);
        data.Cfg.Should().Be(1.0);
        data.Seed.Should().Be(832350830334358);
        data.Sampler.Should().Be("exponential/ddim_beta57");
        data.Checkpoint.Should().Be(@"Krea-2\krea2TurboOfficialComfy_krea2TurboFp8.safetensors");
        data.Vae.Should().Be("qwen_image_vae.safetensors");
        data.Loras.Should().HaveCount(5);
        data.Loras.Single(l => l.Name == "Purple_Graphics_KR2").Hash.Should().Be("990d0df641");
        data.Loras.Single(l => l.Name == "ass_krea2_loraholic").Weight.Should().Be(3);
        data.Loras.Single(l => l.Name == "AIM-KREA-V1").Hash.Should().Be("acbc6d5295");
    }

    [Fact]
    public void TextOnly_TakesWeightsFromLoraTags()
    {
        var data = Read(("parameters", "a cat <lora:foo:0.7>, studio\nSteps: 20, Sampler: Euler a, Seed: 5, Lora hashes: \"foo: abc1234567\""));

        data.Source.Should().Be(RecipeDataSource.SettingsText);
        data.SourceNote.Should().Be("Read from the original image file (settings text)");
        data.Prompt.Should().Be("a cat, studio");
        data.Loras.Should().ContainSingle().Which.Should().Be(new RecipeLoraUse("foo", null, 0.7, "abc1234567"));
    }

    [Fact]
    public void CivitaiResources_GiveWeightsByVersionId_AndTheCheckpoint()
    {
        // Civitai's own generator (image 147042061, 2026-10-27): no <lora:> tags, weights in a JSON list.
        var data = Read(("parameters",
            "a cat\nSteps: 16, Sampler: Euler a, CFG scale: 5, Seed: 105873465, Civitai resources: " +
            "[{\"type\":\"checkpoint\",\"modelVersionId\":1714314,\"modelName\":\"Plant Milk - Model Suite\",\"modelVersionName\":\"Hemp II\"}," +
            "{\"type\":\"lora\",\"weight\":0.9,\"modelVersionId\":1529516,\"modelName\":\"not HL\\u0027s styles\",\"modelVersionName\":\"Style 001\"}," +
            "{\"type\":\"lora\",\"weight\":-1.9,\"modelVersionId\":1523317,\"modelName\":\"Body Weight Slider\",\"modelVersionName\":\"ILXL\"}," +
            "{\"type\":\"lora\",\"modelVersionId\":42,\"modelName\":\"No weight\"}], Civitai metadata: {\"workflow\":\"txt2img\"}"));

        data.Loras.Should().BeEquivalentTo(new[]
        {
            new RecipeLoraUse("not HL's styles", null, 0.9, null, 1529516),
            new RecipeLoraUse("Body Weight Slider", null, -1.9, null, 1523317),
            new RecipeLoraUse("No weight", null, null, null, 42),
        });
        data.Checkpoint.Should().Be("Plant Milk - Model Suite (Hemp II)");
        data.Steps.Should().Be(16);
    }

    [Fact]
    public void OddJsonShapes_NeverThrow()
    {
        // Third-party writers: a version id that is not a number, a Hashes value that is not an object.
        var act = () => Read(("parameters",
            "a cat <lora:foo:0.7>\nSteps: 20, Hashes: [1, 2], Civitai resources: [{\"type\":\"lora\",\"weight\":0.5,\"modelVersionId\":\"123\",\"modelName\":\"bar\"}]"));

        var data = act.Should().NotThrow().Subject;
        data.Loras.Should().ContainSingle().Which.Should().Be(new RecipeLoraUse("bar", null, 0.5, null, null));
        data.Steps.Should().Be(20);
    }

    [Fact]
    public void TracedGraph_TakesWhatItLacksFromTheText()
    {
        // The tracer knows the KSampler but its positive input leads nowhere.
        var data = Read(
            ("prompt", """
                {"4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"sdxl.safetensors"}},
                 "3":{"class_type":"KSampler","inputs":{"seed":42,"steps":25,"cfg":6.5,"sampler_name":"euler","scheduler":"karras","denoise":1,
                      "model":["4",0],"positive":["99",0],"negative":["98",0],"latent_image":["5",0]}}}
                """),
            ("parameters", "a red fox\nNegative prompt: blurry\nSteps: 25, Seed: 42"));

        data.Source.Should().Be(RecipeDataSource.ComfyUiWorkflow);
        data.Prompt.Should().Be("a red fox");
        data.NegativePrompt.Should().Be("blurry");
        data.Sampler.Should().Be("euler");
    }

    [Fact]
    public void BrokenCivitaiResources_FallBackToTags()
    {
        var data = Read(("parameters", "a cat <lora:foo:0.7>\nSteps: 20, Civitai resources: [not json"));

        data.Loras.Should().ContainSingle().Which.Name.Should().Be("foo");
    }

    [Fact]
    public void HashesJson_IsTheFallbackHashSource()
    {
        var data = Read(("parameters", "a cat <lora:foo:0.7>\nSteps: 20, Seed: 5, Hashes: {\"model\": \"aa\", \"lora:foo\": \"ff00ff00ff\"}"));

        data.Loras.Should().ContainSingle().Which.Hash.Should().Be("ff00ff00ff");
    }

    [Fact]
    public void A1111Tag_UnreadableWeight_IsUnknown()
    {
        var data = Read(("parameters", "a cat <lora:foo:abc>\nSteps: 20, Seed: 5"));

        data.Loras.Should().ContainSingle().Which.Weight.Should().BeNull();
    }

    [Fact]
    public void CoreKSamplerGraph_UsesTheTrace()
    {
        var data = Read(("prompt", """
            {"4":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"sdxl.safetensors"}},
             "6":{"class_type":"CLIPTextEncode","inputs":{"text":"a red fox","clip":["4",1]}},
             "7":{"class_type":"CLIPTextEncode","inputs":{"text":"blurry","clip":["4",1]}},
             "3":{"class_type":"KSampler","inputs":{"seed":42,"steps":25,"cfg":6.5,"sampler_name":"euler","scheduler":"karras","denoise":1,
                  "model":["4",0],"positive":["6",0],"negative":["7",0],"latent_image":["5",0]}}}
            """));

        data.Source.Should().Be(RecipeDataSource.ComfyUiWorkflow);
        data.SourceNote.Should().Be("Read from the original image file (ComfyUI workflow embedded)");
        data.Prompt.Should().Be("a red fox");
        data.NegativePrompt.Should().Be("blurry");
        data.Checkpoint.Should().Be("sdxl.safetensors");
        data.Sampler.Should().Be("euler");
        data.Scheduler.Should().Be("karras");
        data.Steps.Should().Be(25);
        data.Cfg.Should().Be(6.5);
        data.Seed.Should().Be(42);
    }

    [Fact]
    public void BrokenPromptJson_FallsBackToTextAndReportsTheError()
    {
        var data = Read(("prompt", "{not json"), ("parameters", "a cat\nSteps: 20, Seed: 5"));

        data.Source.Should().Be(RecipeDataSource.SettingsText);
        data.Prompt.Should().Be("a cat");
        data.ReadError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void NoTexts_GivesNone()
    {
        var data = Read();

        data.Source.Should().Be(RecipeDataSource.None);
        data.SourceNote.Should().BeNull();
        data.Loras.Should().BeEmpty();
        data.Steps.Should().BeNull();
    }
}
