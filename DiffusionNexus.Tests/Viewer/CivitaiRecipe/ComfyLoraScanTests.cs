using System.Text.Json;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #628: the recipe collects LoRA weights from every loader node in the executed graph — the shared
/// tracer only follows the core samplers' model chain, and the example image uses RES4LYF's
/// ClownsharKSampler with an rgthree Power Lora Loader (47 entries, 5 switched on).
/// </summary>
public sealed class ComfyLoraScanTests
{
    internal static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Viewer", "TestData", "CivitaiRecipe", name));

    private static Dictionary<string, JsonElement> Graph(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void ExampleGraph_FindsTheFiveSwitchedOnLoras()
    {
        var facts = ComfyLoraScan.Scan(Graph(Fixture("143992601-prompt.json")));

        facts.Loras.Select(l => (l.Name, l.Weight)).Should().BeEquivalentTo(new (string, double?)[]
        {
            ("Purple_Graphics_KR2", 1), ("Purple_Grainy_Kr2_AM", 0.5), ("ass_krea2_loraholic", 3),
            ("AIM-KREA-V1", 0.65), ("godago_4ho", 1),
        });
        facts.Loras.Single(l => l.Name == "AIM-KREA-V1").FileName.Should().Be(@"Krea2\Style\AIM-KREA-V1.safetensors");
        facts.Checkpoint.Should().Be(@"Krea-2\krea2TurboOfficialComfy_krea2TurboFp8.safetensors");
        facts.Vae.Should().Be("qwen_image_vae.safetensors");
    }

    [Fact]
    public void CoreLoraLoader_UsesStrengthModel()
    {
        var facts = ComfyLoraScan.Scan(Graph("""
            {"1":{"class_type":"LoraLoader","inputs":{"lora_name":"styles/foo.safetensors","strength_model":0.8,"strength_clip":0.3,"model":["2",0],"clip":["2",1]}},
             "3":{"class_type":"LoraLoaderModelOnly","inputs":{"lora_name":"bar.safetensors","strength_model":1.25,"model":["1",0]}}}
            """));

        facts.Loras.Select(l => (l.Name, l.Weight)).Should().BeEquivalentTo(new (string, double?)[] { ("foo", 0.8), ("bar", 1.25) });
    }

    [Fact]
    public void GraphStrengthAsLink_IsUnknown()
    {
        var facts = ComfyLoraScan.Scan(Graph("""
            {"1":{"class_type":"LoraLoader","inputs":{"lora_name":"foo.safetensors","strength_model":["12",0],"strength_clip":1}}}
            """));

        facts.Loras.Should().ContainSingle().Which.Weight.Should().BeNull();
    }

    [Fact]
    public void TwoCheckpointLoaders_GiveNoCheckpoint()
    {
        var facts = ComfyLoraScan.Scan(Graph("""
            {"1":{"class_type":"CheckpointLoaderSimple","inputs":{"ckpt_name":"a.safetensors"}},
             "2":{"class_type":"UNETLoader","inputs":{"unet_name":"b.safetensors"}},
             "3":{"class_type":"VAELoader","inputs":{"vae_name":"v.safetensors"}}}
            """));

        facts.Checkpoint.Should().BeNull("two model loaders: which one made the image is not known");
        facts.Vae.Should().Be("v.safetensors");
    }
}
