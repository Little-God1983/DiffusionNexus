using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>#667: one folder per recipe — recipe.json next to the original — that survives restarts and moved profiles.</summary>
public sealed class RecipeStoreTests : IDisposable
{
    private static readonly DateTimeOffset PastedAt = new(2026, 10, 28, 9, 30, 0, TimeSpan.FromHours(2));

    private readonly string _root = Path.Combine(Directory.CreateTempSubdirectory("dn-recipe-store").FullName, "Recipes");
    private readonly RecipeStore _store;

    public RecipeStoreTests() => _store = new RecipeStore(_root, null);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best-effort */ }
    }

    private static CivitaiImageRecipe Recipe(long id, string? originalPath = null)
    {
        var version = new CivitaiModelVersion
        {
            Id = 100, ModelId = 10, Name = "v1", BaseModel = "Krea 2",
            Files = [new CivitaiModelFile { Name = "foo.safetensors", Primary = true, Hashes = new CivitaiFileHashes { AutoV2 = "ABC1234567", SHA256 = "FFEE" } }],
        };
        var model = new CivitaiModel { Id = 10, Name = "Foo", Type = CivitaiModelType.LORA, ModelVersions = [version] };
        var use = new RecipeLoraUse("foo", "foo.safetensors", 0.7, "ABC1234567", 100, [0.7, 0.3]);
        return new CivitaiImageRecipe(
            id,
            new CivitaiModelImage { Id = id, Url = $"https://image.civitai.com/x/{id}.jpeg", Type = "image", BrowsingLevel = 8, Width = 1256, Height = 1672, Username = "purplelady", ModelVersionIds = [100] },
            8,
            originalPath,
            new ImageRecipeData { Format = RecipeFileFormat.Png, Source = RecipeDataSource.SettingsText, Prompt = "a cat", Seed = 832350830334358, Steps = 20, Loras = [use] },
            [new CivitaiRecipeResource(100, version, model, use, null), new CivitaiRecipeResource(200, null, null, null, CivitaiImageRecipeService.NotFoundText)],
            [new RecipeLoraUse("private", null, 0.3, null)],
            HasApiKey: true);
    }

    private string WriteFile(long id, string name, int length = 10)
    {
        var folder = _store.FolderFor(id);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    [Fact]
    public void SaveThenLoadAll_RoundTripsTheWholeRecipe()
    {
        var recipe = Recipe(5);

        _store.Save(recipe, PastedAt);
        var saved = new RecipeStore(_root, null).LoadAll().Single().Saved!;

        saved.Recipe.Should().BeEquivalentTo(recipe, o => o.Excluding(r => r.OriginalFilePath));
        saved.Recipe.Data.Seed.Should().Be(832350830334358);
        saved.Recipe.Resources[0].Model!.ModelVersions[0].Files[0].Hashes!.AutoV2.Should().Be("ABC1234567");
        saved.Recipe.Resources[0].Use!.AllWeights.Should().Equal(0.7, 0.3);
        saved.Kept.Should().BeFalse();
        saved.PastedAt.Should().Be(PastedAt);
        saved.FormatVersion.Should().Be(SavedRecipe.CurrentFormat);
    }

    [Fact]
    public void Save_KeepsExistingKeptFlag()
    {
        _store.Save(Recipe(5), PastedAt);
        _store.SetKept([5], true);

        _store.Save(Recipe(5), PastedAt.AddHours(1));

        _store.TryLoad(5)!.Saved!.Kept.Should().BeTrue();
    }

    [Fact]
    public void Update_KeepsPastedAtAndKept()
    {
        _store.Save(Recipe(5), PastedAt);
        _store.SetKept([5], true);

        _store.Update(Recipe(5) with { Rating = 1 });

        var saved = _store.TryLoad(5)!.Saved!;
        saved.PastedAt.Should().Be(PastedAt);
        saved.Kept.Should().BeTrue();
        saved.Recipe.Rating.Should().Be(1);
    }

    [Fact]
    public void SetKept_False_MovesBackToHistory()
    {
        _store.Save(Recipe(5), PastedAt);
        _store.SetKept([5], true);

        _store.SetKept([5], false);

        _store.TryLoad(5)!.Saved!.Kept.Should().BeFalse();
    }

    [Fact]
    public void LoadAll_FolderWithoutJson_IsBroken()
    {
        WriteFile(42, "original.png", length: 123);

        var entry = _store.LoadAll().Single();

        entry.ImageId.Should().Be(42);
        entry.IsBroken.Should().BeTrue();
        entry.ReadError.Should().NotBeNullOrEmpty();
        entry.SizeBytes.Should().Be(123);
    }

    [Fact]
    public void LoadAll_UnreadableJson_IsBroken()
    {
        File.WriteAllText(WriteFile(43, "recipe.json", 0), "{");

        var entry = _store.LoadAll().Single();

        entry.IsBroken.Should().BeTrue();
        entry.ReadError.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void LoadAll_IgnoresFoldersThatAreNotImageIds()
    {
        Directory.CreateDirectory(Path.Combine(_root, "notes"));

        _store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_NoRootYet_IsEmpty() => _store.LoadAll().Should().BeEmpty();

    [Fact]
    public void LoadAll_OriginalPathPointsIntoTheFolder()
    {
        var original = WriteFile(6, "original.png");
        _store.Save(Recipe(6, @"C:\elsewhere\x.png"), PastedAt);

        _store.LoadAll().Single().Saved!.Recipe.OriginalFilePath.Should().Be(original);
    }

    [Fact]
    public void LoadAll_NoOriginal_PathIsNull()
    {
        _store.Save(Recipe(6, @"C:\elsewhere\x.png"), PastedAt);

        _store.LoadAll().Single().Saved!.Recipe.OriginalFilePath.Should().BeNull();
    }

    [Fact]
    public void SizeIsTheSumOfTheFolder()
    {
        WriteFile(6, "original.png", length: 1000);

        var stored = _store.Save(Recipe(6), PastedAt);

        stored.SizeBytes.Should().Be(1000 + new FileInfo(Path.Combine(_store.FolderFor(6), "recipe.json")).Length);
        Directory.GetFiles(_store.FolderFor(6)).Should().NotContain(f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void Delete_ReturnsBytesFreed_AndRemovesFolder()
    {
        WriteFile(6, "original.png", length: 1000);
        var stored = _store.Save(Recipe(6), PastedAt);

        var freed = _store.Delete([6]);

        freed.Should().Be(stored.SizeBytes);
        Directory.Exists(_store.FolderFor(6)).Should().BeFalse();
    }

    [Fact]
    public void Delete_MissingFolder_ReturnsZero() => _store.Delete([999]).Should().Be(0);

    [Fact]
    public void Save_SameImageTwice_OneFolder()
    {
        _store.Save(Recipe(7), PastedAt);
        _store.Save(Recipe(7), PastedAt);

        _store.LoadAll().Should().ContainSingle();
    }

    [Fact]
    public void Changed_RaisedOnSaveKeptAndDelete()
    {
        var raised = 0;
        _store.Changed += (_, _) => raised++;

        _store.Save(Recipe(8), PastedAt);
        _store.SetKept([8], true);
        _store.Delete([8]);

        raised.Should().Be(3);
    }

    [Fact]
    public void InFlightDownload_IsNotTheOriginal_NorCounted()
    {
        var original = WriteFile(6, "original.png", length: 100);
        WriteFile(6, "original.0123abcd.download", length: 5000);
        _store.Save(Recipe(6), PastedAt);

        var stored = _store.TryLoad(6)!;

        stored.Saved!.Recipe.OriginalFilePath.Should().Be(original);
        stored.SizeBytes.Should().Be(100 + new FileInfo(Path.Combine(_store.FolderFor(6), "recipe.json")).Length);
    }

    [Fact]
    public void InFlightDownloadOnly_MeansNoOriginal()
    {
        WriteFile(6, "original.0123abcd.download");
        _store.Save(Recipe(6), PastedAt);

        _store.TryLoad(6)!.Saved!.Recipe.OriginalFilePath.Should().BeNull();
    }

    [Fact]
    public void Save_WhenTheJsonIsLocked_LogsAndThrows_AndLeavesNoTempFile()
    {
        var logger = new Mock<IUnifiedLogger>();
        var store = new RecipeStore(_root, logger.Object);
        store.Save(Recipe(9), PastedAt);
        var json = Path.Combine(store.FolderFor(9), "recipe.json");

        using (new FileStream(json, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var act = () => store.Save(Recipe(9), PastedAt);
            act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
        }

        logger.Verify(l => l.Warn(LogCategory.FileSystem, "CivitaiRecipe", It.Is<string>(m => m.Contains("Recipe 9 could not be written")), It.IsAny<string?>()), Times.Once);
        Directory.GetFiles(store.FolderFor(9)).Should().NotContain(f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void SetKept_WhenALaterOneFails_StillRaisesChanged_ForTheOnesWritten()
    {
        _store.Save(Recipe(1), PastedAt);
        _store.Save(Recipe(2), PastedAt);
        var raised = 0;
        _store.Changed += (_, _) => raised++;

        using (new FileStream(Path.Combine(_store.FolderFor(2), "recipe.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var act = () => _store.SetKept([1, 2], true);
            act.Should().Throw<Exception>().Which.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
        }

        raised.Should().Be(1);
        _store.TryLoad(1)!.Saved!.Kept.Should().BeTrue();
    }

    [Fact]
    public void IsDefiniteFailure_OnlyForGoneResources()
    {
        new CivitaiRecipeResource(1, null, null, null, CivitaiImageRecipeService.NotFoundText).IsDefiniteFailure.Should().BeTrue();
        new CivitaiRecipeResource(1, null, null, null, CivitaiImageRecipeService.NoLongerAvailableText).IsDefiniteFailure.Should().BeTrue();
        new CivitaiRecipeResource(1, null, null, null, "429 Too Many Requests").IsDefiniteFailure.Should().BeFalse();
        new CivitaiRecipeResource(1, null, null, null, null).IsDefiniteFailure.Should().BeFalse();
    }
}
