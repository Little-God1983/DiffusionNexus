using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>#667: a card never says Ready while a LoRA is missing or a resource could not be checked.</summary>
public sealed class RecipeStatusTests
{
    private static CivitaiRecipeResource Resource(int versionId, CivitaiModelType type = CivitaiModelType.LORA)
    {
        var version = new CivitaiModelVersion { Id = versionId, ModelId = 10, Name = "v1", BaseModel = "Krea 2" };
        var model = new CivitaiModel { Id = 10, Name = "m" + versionId, Type = type, ModelVersions = [version] };
        return new CivitaiRecipeResource(versionId, version, model, null, null);
    }

    private static CivitaiRecipeResource Failed(int versionId) =>
        new(versionId, null, null, null, "429 Too Many Requests");

    private static CivitaiImageRecipe Recipe(IReadOnlyList<CivitaiRecipeResource> resources,
        IReadOnlyList<RecipeLoraUse>? unlinked = null) =>
        new(1, new CivitaiModelImage { Id = 1, Url = "https://image.civitai.com/x/1.jpeg", Type = "image" }, 8, null,
            new ImageRecipeData { Format = RecipeFileFormat.Png, Source = RecipeDataSource.SettingsText, Prompt = "a cat", Seed = 5 },
            resources, unlinked ?? []);

    private static IEnumerable<CivitaiRecipeResource> Loras(int from, int count) =>
        Enumerable.Range(from, count).Select(i => Resource(i));

    /// <summary>
    /// A 429 leaves the version unknown, so the file's entry for that LoRA cannot be linked and sits
    /// in UnlinkedLoras. It may well be on Civitai: the card must not say it is not.
    /// </summary>
    [Fact]
    public void UnlinkedWhileAResourceFailed_IsNotCalledNotOnCivitai()
    {
        var recipe = Recipe([Failed(7)], [new RecipeLoraUse("style", null, 1, null)]);

        var status = RecipeStatus.Compute(recipe, CivitaiInstalledIndex.Empty);

        status.MissingNotOnCivitai.Should().Be(0);
        status.Text.Should().Be("1 of 1 LoRAs missing · 1 resource could not be loaded");
    }

    [Fact]
    public void AllLorasInstalled_IsReady()
    {
        var recipe = Recipe([.. Loras(1, 2), Resource(900, CivitaiModelType.Checkpoint)]);

        var status = RecipeStatus.Compute(recipe, new CivitaiInstalledIndex([1, 2], []));

        status.IsReady.Should().BeTrue();
        status.IsMissing.Should().BeFalse();
        status.Text.Should().Be("Ready");
        status.Loras.Should().Be(2);
    }

    [Fact]
    public void TwoOfFiveMissing_Text()
    {
        var status = RecipeStatus.Compute(Recipe([.. Loras(1, 5)]), new CivitaiInstalledIndex([1, 2, 3], []));

        status.Missing.Should().Be(2);
        status.IsReady.Should().BeFalse();
        status.IsMissing.Should().BeTrue();
        status.Text.Should().Be("2 of 5 LoRAs missing");
    }

    [Fact]
    public void UnlinkedInstalledByAutoV2_CountsInstalled()
    {
        var recipe = Recipe([], [new RecipeLoraUse("x", "x.safetensors", 1, "ABCDEF0123")]);
        var installed = new CivitaiInstalledIndex([], ["abcdef0123456789"]);

        var status = RecipeStatus.Compute(recipe, installed);

        status.Loras.Should().Be(1);
        status.IsReady.Should().BeTrue();
    }

    [Fact]
    public void UnlinkedWithAutoV3Hash_IsMissing()
    {
        var recipe = Recipe([], [new RecipeLoraUse("x", "x.safetensors", 1, "abc123def456")]);

        var status = RecipeStatus.Compute(recipe, new CivitaiInstalledIndex([], ["ffffffffffffffff"]));

        status.Text.Should().Be("1 of 1 LoRAs missing (1 not on Civitai)");
    }

    [Fact]
    public void UnlinkedWithoutHash_IsMissingNotOnCivitai()
    {
        var recipe = Recipe([], [new RecipeLoraUse("x", "x.safetensors", 1, null)]);

        var status = RecipeStatus.Compute(recipe, CivitaiInstalledIndex.Empty);

        status.MissingNotOnCivitai.Should().Be(1);
        status.IsReady.Should().BeFalse();
    }

    [Fact]
    public void EarlyAccessNotInstalled_IsMissing()
    {
        var status = RecipeStatus.Compute(Recipe([Resource(5)]), CivitaiInstalledIndex.Empty);

        status.IsMissing.Should().BeTrue();
        status.IsReady.Should().BeFalse();
    }

    [Fact]
    public void FailedResource_NeverReady()
    {
        var status = RecipeStatus.Compute(Recipe([Failed(7)]), CivitaiInstalledIndex.Empty);

        status.Text.Should().Be("1 resource could not be loaded");
        status.IsReady.Should().BeFalse();
        status.IsMissing.Should().BeTrue();
    }

    [Fact]
    public void FailedAndMissing_JoinsBoth()
    {
        var status = RecipeStatus.Compute(
            Recipe([.. Loras(1, 2), Failed(8), Failed(9)]), new CivitaiInstalledIndex([1], []));

        status.Text.Should().Be("1 of 2 LoRAs missing · 2 resources could not be loaded");
    }

    [Fact]
    public void NoLoras_SaysSo()
    {
        var status = RecipeStatus.Compute(Recipe([Resource(900, CivitaiModelType.Checkpoint)]), CivitaiInstalledIndex.Empty);

        status.Text.Should().Be("No LoRAs");
        status.IsReady.Should().BeFalse();
        status.IsMissing.Should().BeFalse();
    }
}
