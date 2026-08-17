using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

/// <summary>
/// Covers <see cref="EngineModelPathsFile"/> — the engine's <c>extra_model_paths.yaml</c> content.
/// The critical property is that the file it produces is readable back by the same parsers the
/// workload check uses, so "the engine can load this" and "the check says it is installed" cannot
/// disagree.
/// </summary>
public sealed class EngineModelPathsFileTests
{
    [Fact]
    public void Compose_RootWithoutAMapping_GetsComfyUIsStandardFolderNames()
    {
        var yaml = EngineModelPathsFile.Compose([new EngineModelRoot(@"E:\AI\ComfyUI\models", [])]);

        var section = ComfyExtraModelPaths.Parse(yaml.Split('\n')).Single();
        section.BasePath.Should().Be("E:/AI/ComfyUI/models");
        section.Categories.Select(c => c.Category)
            .Should().BeEquivalentTo(EngineModelPathsFile.DefaultCategories);
        section.Categories.Should().Contain(new ComfyCategoryPath("loras", "loras/"));
    }

    [Fact]
    public void Compose_RootWithAMapping_UsesThatMappingInsteadOfTheDefaults()
    {
        // The whole reason this class exists: D:\Models names its folders TextEncoders/,
        // DiffusionModels/, ESRGAN/ — wiring it with ComfyUI's default names finds nothing.
        var roots = new[]
        {
            new EngineModelRoot(@"D:\Models",
            [
                new ComfyCategoryPath("text_encoders", "TextEncoders/"),
                new ComfyCategoryPath("unet", "DiffusionModels/"),
                new ComfyCategoryPath("upscale_models", "ESRGAN/"),
            ]),
        };

        var section = ComfyExtraModelPaths.Parse(EngineModelPathsFile.Compose(roots).Split('\n')).Single();

        section.Categories.Should().Equal(
            new ComfyCategoryPath("text_encoders", "TextEncoders/"),
            new ComfyCategoryPath("unet", "DiffusionModels/"),
            new ComfyCategoryPath("upscale_models", "ESRGAN/"));
        section.Categories.Should().NotContain(c => c.Value == "text_encoders/");
    }

    [Fact]
    public void Compose_KeepsRootsInTheGivenOrder()
    {
        // ComfyUI searches the sections in file order, so the starred default must stay first.
        var yaml = EngineModelPathsFile.Compose(
        [
            new EngineModelRoot(@"D:\Models", []),
            new EngineModelRoot(@"E:\AI\ComfyUI\models", []),
        ]);

        ComfyExtraModelPaths.Parse(yaml.Split('\n')).Select(s => s.BasePath)
            .Should().Equal("D:/Models", "E:/AI/ComfyUI/models");
    }

    [Fact]
    public void Compose_CategoryWithSeveralPaths_KeepsEveryOneReadable()
    {
        // A source yaml can map one category onto several folders via a block scalar. Re-emitting
        // a block scalar would work for ComfyUI but our own line-based readers would drop every
        // path under it, so the check would report models missing that the engine can load.
        var yaml = EngineModelPathsFile.Compose(
        [
            new EngineModelRoot(@"D:\Models",
            [
                new ComfyCategoryPath("loras", "Lora/"),
                new ComfyCategoryPath("loras", "_Lora/"),
                new ComfyCategoryPath("vae", "VAE/"),
            ]),
        ]);

        var sections = ComfyExtraModelPaths.Parse(yaml.Split('\n'));

        sections.SelectMany(s => s.Categories).Where(c => c.Category == "loras").Select(c => c.Value)
            .Should().BeEquivalentTo("Lora/", "_Lora/");
        sections.Should().AllSatisfy(s => s.BasePath.Should().Be("D:/Models"));
        sections.Select(s => s.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Compose_IsDeterministic()
    {
        // No timestamp: callers compare the composed text against the file on disk and only write
        // when something actually changed, so a per-run header would mean a write on every start.
        var roots = new[] { new EngineModelRoot(@"D:\Models", []) };

        EngineModelPathsFile.Compose(roots).Should().Be(EngineModelPathsFile.Compose(roots));
    }

    [Fact]
    public void Compose_SaysItIsGeneratedAndPointsAtSettings()
    {
        var yaml = EngineModelPathsFile.Compose([new EngineModelRoot(@"D:\Models", [])]);

        yaml.Should().Contain("DO NOT EDIT").And.Contain("Settings");
    }

    [Fact]
    public void Compose_BlankRoot_IsSkipped()
    {
        var yaml = EngineModelPathsFile.Compose(
        [
            new EngineModelRoot("   ", []),
            new EngineModelRoot(@"D:\Models", []),
        ]);

        ComfyExtraModelPaths.Parse(yaml.Split('\n')).Should().HaveCount(1);
    }

    [Fact]
    public void Compose_NoRoots_IsHeaderOnly()
    {
        ComfyExtraModelPaths.Parse(EngineModelPathsFile.Compose([]).Split('\n')).Should().BeEmpty();
    }
}
