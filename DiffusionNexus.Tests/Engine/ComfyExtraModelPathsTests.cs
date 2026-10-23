using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

/// <summary>
/// Covers <see cref="ComfyExtraModelPaths"/> — the section-aware reader for
/// <c>extra_model_paths.yaml</c>. The category <em>mapping</em> is the point: a shared library does
/// not have to use ComfyUI's folder names, and that mapping is the only thing that makes it
/// readable by another installation.
/// </summary>
public sealed class ComfyExtraModelPathsTests
{
    /// <summary>
    /// The shape found in the wild (a real user's file): a commented-out a111 section using block
    /// scalars, and a comfyui section pointing at a shared library with renamed category folders.
    /// </summary>
    private static readonly string[] RealWorldYaml =
    [
        "#Rename this to extra_model_paths.yaml and ComfyUI will load it",
        "",
        "a111:",
        "    base_path: D:\\AI-Privat\\stable-diffusion-webui",
        "",
        "    checkpoints: models/Stable-diffusion",
        "    loras: |",
        "         models/Lora",
        "         models/_Lora",
        "    embeddings: embeddings",
        "",
        "comfyui:",
        "    base_path: D:/Models/",
        "    checkpoints: StableDiffusion/",
        "    text_encoders: TextEncoders/",
        "    #configs: models/configs/",
        "    loras: Lora/",
        "    upscale_models: ESRGAN/",
        "    unet:  DiffusionModels/",
        "    llm/GGUF: LLM/GGUF/",
    ];

    [Fact]
    public void Parse_KeepsEachSectionsBasePathWithItsOwnCategories()
    {
        var sections = ComfyExtraModelPaths.Parse(RealWorldYaml);

        sections.Select(s => s.Name).Should().Equal("a111", "comfyui");

        var comfy = sections.Single(s => s.Name == "comfyui");
        comfy.BasePath.Should().Be("D:/Models/");
        comfy.Categories.Should().Contain(new ComfyCategoryPath("text_encoders", "TextEncoders/"));
        comfy.Categories.Should().Contain(new ComfyCategoryPath("unet", "DiffusionModels/"));
        comfy.Categories.Should().Contain(new ComfyCategoryPath("upscale_models", "ESRGAN/"));
    }

    [Fact]
    public void Parse_CommentedOutCategory_IsIgnored()
    {
        var comfy = ComfyExtraModelPaths.Parse(RealWorldYaml).Single(s => s.Name == "comfyui");

        comfy.Categories.Should().NotContain(c => c.Category == "configs");
    }

    [Fact]
    public void Parse_KeyWithASlash_IsStillACategory()
    {
        // "llm/GGUF" is a legitimate ComfyUI folder key; nothing about it should trip the parser.
        var comfy = ComfyExtraModelPaths.Parse(RealWorldYaml).Single(s => s.Name == "comfyui");

        comfy.Categories.Should().Contain(new ComfyCategoryPath("llm/GGUF", "LLM/GGUF/"));
    }

    [Fact]
    public void Parse_BlockScalar_YieldsEveryPathUnderTheKey()
    {
        var a111 = ComfyExtraModelPaths.Parse(RealWorldYaml).Single(s => s.Name == "a111");

        a111.Categories.Where(c => c.Category == "loras").Select(c => c.Value)
            .Should().Equal("models/Lora", "models/_Lora");

        // The key after the block must not be swallowed by it.
        a111.Categories.Should().Contain(new ComfyCategoryPath("embeddings", "embeddings"));
    }

    [Fact]
    public void Parse_IsDefault_IsNeverTreatedAsACategory()
    {
        // ComfyUI reads is_default as "make this the default save location". Copying it into
        // another installation's file would silently redirect where that one writes.
        var sections = ComfyExtraModelPaths.Parse(
        [
            "comfyui:",
            "    base_path: D:/Models/",
            "    is_default: true",
            "    loras: Lora/",
        ]);

        sections.Single().Categories.Should().Equal(new ComfyCategoryPath("loras", "Lora/"));
    }

    [Fact]
    public void Parse_CustomNodes_IsNeverTreatedAsACategory()
    {
        // A real path key, but pointing one install at another's custom nodes would load
        // third-party code into it.
        var sections = ComfyExtraModelPaths.Parse(
        [
            "comfyui:",
            "    base_path: D:/Models/",
            "    custom_nodes: D:/Shared/custom_nodes",
        ]);

        sections.Single().Categories.Should().BeEmpty();
    }

    [Fact]
    public void Parse_QuotedValues_AreUnquoted()
    {
        var sections = ComfyExtraModelPaths.Parse(
        [
            "comfyui:",
            "    base_path: \"D:/My Models/\"",
            "    loras: 'Lora/'",
        ]);

        sections.Single().BasePath.Should().Be("D:/My Models/");
        sections.Single().Categories.Should().Equal(new ComfyCategoryPath("loras", "Lora/"));
    }

    [Fact]
    public void Parse_SectionWithoutABasePath_IsStillReturned()
    {
        // Absolute per-category paths with no base_path is a documented ComfyUI shape.
        var sections = ComfyExtraModelPaths.Parse(
        [
            "comfyui:",
            "    loras: E:/Elsewhere/MyLoras",
        ]);

        sections.Single().BasePath.Should().BeNull();
        sections.Single().Categories.Should().Equal(new ComfyCategoryPath("loras", "E:/Elsewhere/MyLoras"));
    }

    [Fact]
    public void CategoriesFor_MatchesOnPathIdentity_NotStringEquality()
    {
        // The yaml writes D:/Models/ and the database stores D:\Models — the same folder.
        var sections = ComfyExtraModelPaths.Parse(RealWorldYaml);

        var categories = ComfyExtraModelPaths.CategoriesFor(sections, @"D:\Models");

        categories.Should().Contain(new ComfyCategoryPath("text_encoders", "TextEncoders/"));
    }

    [Fact]
    public void CategoriesFor_TwoInstallsDeclaringTheSameLibrary_CollapsesIdenticalEntries()
    {
        // Otherwise two installs agreeing on "loras: Lora/" would read as one category mapped onto
        // two folders, and the engine's file would list it twice.
        var sections = ComfyExtraModelPaths.Parse(
        [
            "comfyui:",
            "    base_path: D:/Models/",
            "    loras: Lora/",
            "another:",
            "    base_path: D:\\Models",
            "    loras: Lora/",
            "    vae: VAE/",
        ]);

        ComfyExtraModelPaths.CategoriesFor(sections, @"D:\Models").Should().Equal(
            new ComfyCategoryPath("loras", "Lora/"),
            new ComfyCategoryPath("vae", "VAE/"));
    }

    [Fact]
    public void CategoriesFor_UnrelatedRoot_IsEmpty()
    {
        var sections = ComfyExtraModelPaths.Parse(RealWorldYaml);

        ComfyExtraModelPaths.CategoriesFor(sections, @"E:\AI\ComfyUI\models").Should().BeEmpty();
    }

    [Fact]
    public void ParseFile_MissingOrBlankPath_IsEmpty()
    {
        ComfyExtraModelPaths.ParseFile(null).Should().BeEmpty();
        ComfyExtraModelPaths.ParseFile("   ").Should().BeEmpty();
        ComfyExtraModelPaths.ParseFile(Path.Combine(Path.GetTempPath(), "dn-no-such-dir-" + Guid.NewGuid()))
            .Should().BeEmpty();
    }

    [Fact]
    public void ParseFile_ReadsTheFileFromTheRepositoryRoot()
    {
        var dir = Directory.CreateTempSubdirectory("dn-comfy-yaml-");
        try
        {
            File.WriteAllLines(Path.Combine(dir.FullName, ComfyExtraModelPaths.FileName), RealWorldYaml);

            ComfyExtraModelPaths.ParseFile(dir.FullName).Should().HaveCount(2);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch { /* best effort */ }
        }
    }
}
