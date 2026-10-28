using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>#667: pasting an image again refreshes the saved recipe but never makes it worse.</summary>
public sealed class RecipeMergeTests
{
    private static CivitaiRecipeResource Loaded(int versionId, double? weight, string name = "foo")
    {
        var version = new CivitaiModelVersion { Id = versionId, ModelId = 10, Name = "v1", BaseModel = "Krea 2" };
        var model = new CivitaiModel { Id = 10, Name = name, Type = CivitaiModelType.LORA, ModelVersions = [version] };
        return new CivitaiRecipeResource(versionId, version, model, new RecipeLoraUse(name, name + ".safetensors", weight, null, versionId), null);
    }

    private static CivitaiRecipeResource Failed(int versionId, string error, double? weight = null) =>
        new(versionId, null, null, weight is null ? null : new RecipeLoraUse("foo", "foo.safetensors", weight, null, versionId), error);

    private static CivitaiImageRecipe Recipe(IReadOnlyList<CivitaiRecipeResource> resources, bool hasApiKey = true,
        ImageRecipeData? data = null, IReadOnlyList<RecipeLoraUse>? unlinked = null) =>
        new(1, new CivitaiModelImage { Id = 1, Url = "https://image.civitai.com/x/1.jpeg", Type = "image" }, 8, null,
            data ?? new ImageRecipeData { Format = RecipeFileFormat.Png, Source = RecipeDataSource.SettingsText, Prompt = "a cat", Seed = 5 },
            resources, unlinked ?? [], hasApiKey);

    [Fact]
    public void TransientFailure_KeepsSavedResource()
    {
        var saved = Recipe([Loaded(100, 0.7)]);
        var fresh = Recipe([Failed(100, "429 Too Many Requests")]);

        var merged = RecipeMerge.Refresh(saved, fresh);

        merged.Resources.Should().ContainSingle().Which.Should().Be(saved.Resources[0]);
    }

    [Fact]
    public void DefiniteFailure_ReplacesSavedResource()
    {
        var fresh = Recipe([Failed(100, CivitaiImageRecipeService.NoLongerAvailableText)]);

        var merged = RecipeMerge.Refresh(Recipe([Loaded(100, 0.7)]), fresh);

        merged.Resources.Should().Equal(fresh.Resources);
    }

    [Fact]
    public void MissingWeight_KeepsSavedWeight()
    {
        var saved = Recipe([Loaded(100, 0.65)]);
        var fresh = Recipe([Loaded(100, null)]);

        var merged = RecipeMerge.Refresh(saved, fresh);

        merged.Resources.Single().Use!.Weight.Should().Be(0.65);
        merged.Resources.Single().Model.Should().BeSameAs(fresh.Resources[0].Model);
    }

    [Fact]
    public void NoKeyThisTime_KeepsResourcesOnlyTheSiteListed()
    {
        var saved = Recipe(Enumerable.Range(1, 6).Select(i => Loaded(i, 0.5)).ToList(), hasApiKey: true);
        var fresh = Recipe([], hasApiKey: false);

        var merged = RecipeMerge.Refresh(saved, fresh);

        merged.Resources.Select(r => r.VersionId).Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public void KeyBothTimes_ResourceMissingFromFresh_IsDropped()
    {
        var saved = Recipe([Loaded(1, 0.5), Loaded(2, 0.5)]);
        var fresh = Recipe([Loaded(1, 0.5)]);

        RecipeMerge.Refresh(saved, fresh).Resources.Select(r => r.VersionId).Should().Equal(1);
    }

    [Fact]
    public void FreshWithoutData_KeepsSavedData()
    {
        var saved = Recipe([], data: new ImageRecipeData { Format = RecipeFileFormat.Png, Source = RecipeDataSource.SettingsText, Prompt = "a cat", Seed = 832350830334358 });
        var fresh = Recipe([], data: ImageRecipeData.Empty(RecipeFileFormat.Jpeg, "download failed"));

        var merged = RecipeMerge.Refresh(saved, fresh);

        merged.Data.Should().BeEquivalentTo(saved.Data, o => o.Excluding(d => d.Format));
        merged.Data.Format.Should().Be(RecipeFileFormat.Jpeg);
    }

    [Fact]
    public void UnlinkedLoras_ComeFromFresh_MinusThoseNowMatchedByAKeptResource()
    {
        var saved = Recipe([Loaded(100, 0.7, "foo")]);
        var fresh = Recipe([Failed(100, "429 Too Many Requests")],
            unlinked: [new RecipeLoraUse("foo", null, 0.7, null), new RecipeLoraUse("private", null, 0.3, null)]);

        var merged = RecipeMerge.Refresh(saved, fresh);

        merged.UnlinkedLoras.Select(l => l.Name).Should().Equal("private");
    }

    [Fact]
    public void FreshBetter_WinsEverywhere()
    {
        var saved = Recipe([Loaded(100, 0.5)], data: new ImageRecipeData { Format = RecipeFileFormat.Png, Source = RecipeDataSource.SettingsText, Prompt = "old" });
        var fresh = Recipe([Loaded(100, 0.9)], data: new ImageRecipeData { Format = RecipeFileFormat.Png, Source = RecipeDataSource.ComfyUiWorkflow, Prompt = "new" });

        RecipeMerge.Refresh(saved, fresh).Should().BeEquivalentTo(fresh);
    }
}
