using Avalonia.Threading;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>#667: the Recipes tab's lists, counter, filters, groups and selection actions.</summary>
[Collection(DispatcherDrainCollection.Name)]
public sealed class RecipesTabViewModelTests : IDisposable
{
    // Wednesday 2026-10-28; Monday of that week is the 26th.
    private static readonly DateTime Now = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Local);
    private static readonly DateTimeOffset Today = new(new DateTime(2026, 10, 28, 9, 0, 0, DateTimeKind.Local));
    private static readonly DateTimeOffset ThisWeek = new(new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Local));
    private static readonly DateTimeOffset EarlierOctober = new(new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Local));

    private readonly string _root = Path.Combine(Directory.CreateTempSubdirectory("dn-recipes-tab").FullName, "Recipes");
    private readonly RecipeStore _store;
    private readonly Mock<IRecipeHost> _host = new();
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly Mock<IUnifiedLogger> _logger = new();
    private CivitaiInstalledIndex _installed = CivitaiInstalledIndex.Empty;

    public RecipesTabViewModelTests()
    {
        _store = new RecipeStore(_root, null);
        _host.SetupGet(h => h.Installed).Returns(() => _installed);
        _host.Setup(h => h.CreateRecipeCard(It.IsAny<CivitaiModel>())).Returns<CivitaiModel>(m => new CivitaiResultViewModel(m, true));
        _host.Setup(h => h.EnqueueRecipeAsync(It.IsAny<List<(CivitaiResultViewModel, CivitaiVersionPickItemViewModel)>>())).Returns(Task.CompletedTask);
        _dialogs.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); } catch { /* best-effort */ }
    }

    private RecipesTabViewModel Create()
    {
        var vm = new RecipesTabViewModel(_store, _host.Object, _dialogs.Object, _logger.Object, () => Now);
        return vm;
    }

    private static void Pump() => Dispatcher.UIThread.RunJobs();

    private static CivitaiRecipeResource Lora(int versionId, int modelId, string name)
    {
        var version = new CivitaiModelVersion { Id = versionId, ModelId = modelId, Name = "v1", BaseModel = "Krea 2" };
        var model = new CivitaiModel { Id = modelId, Name = name, Type = CivitaiModelType.LORA, ModelVersions = [version] };
        return new CivitaiRecipeResource(versionId, version, model, null, null);
    }

    private static CivitaiImageRecipe Recipe(long id, int rating = 1, string? prompt = "a cat", string creator = "someone",
        IReadOnlyList<CivitaiRecipeResource>? resources = null, IReadOnlyList<RecipeLoraUse>? unlinked = null) =>
        new(id,
            new CivitaiModelImage { Id = id, Url = $"https://image.civitai.com/x/{id}.jpeg", Type = "image", BrowsingLevel = rating, Username = creator },
            rating, null,
            new ImageRecipeData { Format = RecipeFileFormat.Png, Source = RecipeDataSource.SettingsText, Prompt = prompt },
            resources ?? [], unlinked ?? []);

    private void Save(CivitaiImageRecipe recipe, DateTimeOffset pastedAt, bool kept = false)
    {
        _store.Save(recipe, pastedAt);
        if (kept) _store.SetKept([recipe.ImageId], true);
    }

    private void Broken(long id)
    {
        var folder = _store.FolderFor(id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "recipe.json"), "{ not json");
    }

    /// <summary>Saves <paramref name="recipe"/> and rewrites its json through <paramref name="edit"/>, as a hand edit or a newer build would.</summary>
    private void SaveAndEdit(CivitaiImageRecipe recipe, Action<System.Text.Json.Nodes.JsonObject> edit)
    {
        Save(recipe, Today);
        var path = Path.Combine(_store.FolderFor(recipe.ImageId), "recipe.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(json);
        File.WriteAllText(path, json.ToJsonString());
    }

    private static List<long> Ids(RecipesTabViewModel vm) =>
        vm.Groups.SelectMany(g => g.Cards).Select(c => c.ImageId).ToList();

    private static RecipeCardViewModel Card(RecipesTabViewModel vm, long id) =>
        vm.Groups.SelectMany(g => g.Cards).Single(c => c.ImageId == id);

    [Fact]
    public void History_GroupsByPasteDate()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), ThisWeek);
        Save(Recipe(3), EarlierOctober);

        var vm = Create();

        vm.Groups.Select(g => g.Header).Should().Equal("Today", "This week", "October");
        vm.Groups[0].CountText.Should().Be("1 recipe");
    }

    [Fact]
    public void KeptView_ShowsOnlyKept()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), Today, kept: true);
        var vm = Create();
        Pump();

        Ids(vm).Should().Equal(1);
        vm.ShowKeptCommand.Execute(null);

        vm.IsKeptView.Should().BeTrue();
        Ids(vm).Should().Equal(2);
        vm.ShowHistoryCommand.Execute(null);
        Ids(vm).Should().Equal(1);
    }

    [Fact]
    public void Counter_CountsBothLists()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), ThisWeek);
        Save(Recipe(3), Today, kept: true);
        var vm = Create();
        Pump();

        vm.CounterText.Should().StartWith("3 recipes · ").And.EndWith(" on disk (History 2, Kept 1)");
        vm.HistoryCount.Should().Be(2);
        vm.KeptCount.Should().Be(1);
    }

    [Fact]
    public void Counter_SaysRecipeForOne()
    {
        Save(Recipe(1), Today);
        Create().CounterText.Should().StartWith("1 recipe · ").And.EndWith("(History 1, Kept 0)");
    }

    [Fact]
    public void ShowNsfwOff_HidesNsfw_AndSaysSo()
    {
        Save(Recipe(1, rating: 1), Today);
        Save(Recipe(2, rating: 8), Today);
        var vm = Create();
        vm.HiddenText.Should().BeNull();

        vm.ShowNsfw = false;

        Ids(vm).Should().Equal(1);
        vm.HiddenText.Should().Be("1 NSFW recipe hidden in History");
        vm.CounterText.Should().StartWith("2 recipes · ");

        Save(Recipe(3, rating: 16), Today);
        Pump();
        vm.HiddenText.Should().Be("2 NSFW recipes hidden in History");
    }

    [Fact]
    public void FiltersHiding_UseTheFilterWording()
    {
        Save(Recipe(1, resources: [Lora(100, 10, "Foo")]), Today);
        Save(Recipe(2, rating: 8), Today);
        _installed = new CivitaiInstalledIndex([100], []);
        var vm = Create();

        vm.StatusFilter = RecipeStatusFilter.Missing;

        Ids(vm).Should().BeEmpty();
        vm.HiddenText.Should().Be("2 recipes hidden by filters in History");
        vm.ShowKeptCommand.Execute(null);
        vm.HiddenText.Should().BeNull();
    }

    [Fact]
    public void ReadyFilter_ShowsOnlyReady()
    {
        Save(Recipe(1, resources: [Lora(100, 10, "Foo")]), Today);
        Save(Recipe(2, resources: [Lora(200, 20, "Bar")]), Today);
        Save(Recipe(3), Today);
        _installed = new CivitaiInstalledIndex([100], []);
        var vm = Create();

        vm.StatusFilter = RecipeStatusFilter.Ready;

        Ids(vm).Should().Equal(1);
        Card(vm, 1).StatusText.Should().Be("Ready");
    }

    [Fact]
    public void MissingFilter_ShowsAmber()
    {
        Save(Recipe(1, resources: [Lora(100, 10, "Foo")]), Today);
        Save(Recipe(2, resources: [Lora(200, 20, "Bar")]), Today);
        _installed = new CivitaiInstalledIndex([100], []);
        var vm = Create();

        vm.StatusFilter = RecipeStatusFilter.Missing;

        Ids(vm).Should().Equal(2);
        Card(vm, 2).IsMissing.Should().BeTrue();
        Card(vm, 2).StatusText.Should().Be("1 of 1 LoRAs missing");
    }

    [Fact]
    public void Search_MatchesPromptCreatorIdAndLoraName()
    {
        Save(Recipe(1001, prompt: "A Red Dragon", creator: "alice"), Today);
        Save(Recipe(2002, prompt: "forest", creator: "Bob"), Today);
        Save(Recipe(3003, prompt: "city", resources: [Lora(100, 10, "Pixel Style")]), Today);
        var vm = Create();

        vm.SearchText = "dragon";
        Ids(vm).Should().Equal(1001);
        vm.SearchText = "BOB";
        Ids(vm).Should().Equal(2002);
        vm.SearchText = "3003";
        Ids(vm).Should().Equal(3003);
        vm.SearchText = "pixel";
        Ids(vm).Should().Equal(3003);
        vm.SearchText = "nothing like this";
        Ids(vm).Should().BeEmpty();
        vm.HiddenText.Should().Be("3 recipes hidden by filters in History");
    }

    [Fact]
    public void InstalledIndexChanged_UpdatesStatusAndFilters()
    {
        Save(Recipe(1, resources: [Lora(100, 10, "Foo")]), Today);
        var vm = Create();
        vm.StatusFilter = RecipeStatusFilter.Missing;
        Ids(vm).Should().Equal(1);

        _installed = new CivitaiInstalledIndex([100], []);
        _host.Raise(h => h.InstalledIndexChanged += null, EventArgs.Empty);

        Ids(vm).Should().BeEmpty();
        vm.StatusFilter = RecipeStatusFilter.Ready;
        Card(vm, 1).StatusText.Should().Be("Ready");
    }

    [Fact]
    public void KeepSelected_MovesToKept_AndLogs()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), Today);
        var vm = Create();
        vm.Select(Card(vm, 1), additive: false, range: false);
        vm.SelectedCount.Should().Be(1);

        vm.KeepSelectedCommand.Execute(null);
        Pump();

        _store.TryLoad(1)!.Saved!.Kept.Should().BeTrue();
        Ids(vm).Should().Equal(2);
        vm.HistoryCount.Should().Be(1);
        vm.KeptCount.Should().Be(1);
        _logger.Verify(l => l.Info(It.IsAny<LogCategory>(), "CivitaiRecipe", It.Is<string>(m => m.Contains("1 recipe") && m.Contains("kept")), It.IsAny<string?>()), Times.AtLeastOnce);
    }

    [Fact]
    public void Reload_KeepsSelectionAndFilters()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), Today);
        var vm = Create();
        vm.SearchText = "cat";
        vm.Select(Card(vm, 2), additive: false, range: false);

        Save(Recipe(3), Today);
        Pump();

        vm.SearchText.Should().Be("cat");
        Card(vm, 2).IsSelected.Should().BeTrue();
        vm.SelectedCount.Should().Be(1);
    }

    [Fact]
    public void ToggleKept_FlipsOneCard()
    {
        Save(Recipe(1), Today);
        var vm = Create();

        vm.ToggleKeptCommand.Execute(Card(vm, 1));
        Pump();

        vm.KeptCount.Should().Be(1);
        vm.ShowKeptCommand.Execute(null);
        vm.ToggleKeptCommand.Execute(Card(vm, 1));
        Pump();
        vm.KeptCount.Should().Be(0);
        vm.HistoryCount.Should().Be(1);
    }

    [Fact]
    public void Select_CtrlAndShift()
    {
        for (long i = 1; i <= 4; i++) Save(Recipe(i), Today.AddMinutes(i));
        var vm = Create();
        var ids = Ids(vm);

        vm.Select(Card(vm, ids[0]), false, false);
        vm.Select(Card(vm, ids[2]), true, false);
        vm.SelectedCount.Should().Be(2);
        vm.Select(Card(vm, ids[3]), false, true);
        vm.SelectedCount.Should().Be(2); // anchor ids[2] .. ids[3]
        vm.ClearSelectionCommand.Execute(null);
        vm.SelectedCount.Should().Be(0);
    }

    [Fact]
    public void QueueMissing_DedupesAcrossRecipes()
    {
        Save(Recipe(1, resources: [Lora(100, 10, "Foo"), Lora(200, 20, "Bar")]), Today);
        Save(Recipe(2, resources: [Lora(100, 10, "Foo")]), Today);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);
        vm.Select(Card(vm, 2), true, false);

        vm.QueueButtonText.Should().Be("Queue missing LoRAs (2)");
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>? pairs = null;
        _host.Setup(h => h.EnqueueRecipeAsync(It.IsAny<List<(CivitaiResultViewModel, CivitaiVersionPickItemViewModel)>>()))
            .Callback<List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>>(p => pairs = p)
            .Returns(Task.CompletedTask);

        vm.QueueMissingCommand.Execute(null);

        pairs.Should().NotBeNull();
        pairs!.Select(p => p.Pick.Version.Id).Should().BeEquivalentTo([100, 200]);
        _host.Verify(h => h.CreateRecipeCard(It.IsAny<CivitaiModel>()), Times.Exactly(2));
    }

    [Fact]
    public void QueueTooltip_NamesWhatCannotBeQueued()
    {
        var failed = new CivitaiRecipeResource(300, null, null, null, "429 Too Many Requests");
        Save(Recipe(1, resources: [Lora(100, 10, "Foo"), failed], unlinked: [new RecipeLoraUse("x", "x.safetensors", 1, null)]), Today);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);

        vm.QueueButtonText.Should().Be("Queue missing LoRAs (1)");
        vm.QueueTooltip.Should().Be("1 not on Civitai, 1 could not be loaded");

        vm.ClearSelectionCommand.Execute(null);
        Save(Recipe(2, resources: [Lora(100, 10, "Foo")]), Today);
        Pump();
        vm.Select(Card(vm, 2), false, false);
        vm.QueueTooltip.Should().Be("Queues the LoRAs these recipes are missing, each once.");
    }

    [Fact]
    public async Task DeleteSelected_AsksFirst_NoDeleteOnNo()
    {
        Save(Recipe(1), Today);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);
        _dialogs.Setup(d => d.ShowConfirmAsync("Delete recipes", It.Is<string>(m => m.StartsWith("Delete 1 recipe? This frees ") && m.EndsWith(". The images are removed from disk."))))
            .ReturnsAsync(false);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Directory.Exists(_store.FolderFor(1)).Should().BeTrue();
        _dialogs.Verify(d => d.ShowConfirmAsync("Delete recipes", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task DeleteSelected_Yes_RemovesFolders()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), Today);
        Broken(3);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);
        vm.Select(Card(vm, 3), true, false);

        await vm.DeleteSelectedCommand.ExecuteAsync(null);
        Pump();

        Directory.Exists(_store.FolderFor(1)).Should().BeFalse();
        Directory.Exists(_store.FolderFor(3)).Should().BeFalse();
        Directory.Exists(_store.FolderFor(2)).Should().BeTrue();
        Ids(vm).Should().Equal(2);
        vm.SelectedCount.Should().Be(0);
        _logger.Verify(l => l.Info(It.IsAny<LogCategory>(), "CivitaiRecipe", It.Is<string>(m => m.Contains("deleted") && m.Contains("2 recipes")), It.IsAny<string?>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task ClearHistory_LeavesKeptAndBroken()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), Today);
        Save(Recipe(3), Today, kept: true);
        Broken(4);
        var vm = Create();

        await vm.ClearHistoryCommand.ExecuteAsync(null);
        Pump();

        _dialogs.Verify(d => d.ShowConfirmAsync("Clear history", It.Is<string>(m => m.StartsWith("Delete all 2 recipes in History? This frees ") && m.EndsWith(". Kept recipes and 1 unreadable recipe are not touched."))), Times.Once);
        Directory.Exists(_store.FolderFor(1)).Should().BeFalse();
        Directory.Exists(_store.FolderFor(2)).Should().BeFalse();
        Directory.Exists(_store.FolderFor(3)).Should().BeTrue();
        Directory.Exists(_store.FolderFor(4)).Should().BeTrue();
    }

    [Fact]
    public void BrokenFolder_ShowsInHistory_AsBrokenCard()
    {
        Broken(9);
        Save(Recipe(1), Today);
        var vm = Create();

        var card = Card(vm, 9);
        card.IsBroken.Should().BeTrue();
        card.Title.Should().Be("Recipe could not be read");
        card.IsReady.Should().BeFalse();
        card.IsMissing.Should().BeFalse();
        vm.HistoryCount.Should().Be(2);
        vm.CounterText.Should().StartWith("2 recipes · ");

        vm.StatusFilter = RecipeStatusFilter.Ready;
        Ids(vm).Should().NotContain(9);
        vm.StatusFilter = RecipeStatusFilter.Missing;
        Ids(vm).Should().NotContain(9);
        vm.StatusFilter = RecipeStatusFilter.All;
        Ids(vm).Should().Contain(9);
    }

    [Fact]
    public void Title_Byline_AndRatingBadge()
    {
        Save(Recipe(1, rating: 8, prompt: "\n  first line  \nsecond"), Today);
        Save(Recipe(2, prompt: null), ThisWeek);
        Save(Recipe(3, prompt: new string('x', 90)), Today.AddMinutes(-1));
        var vm = Create();

        Card(vm, 1).Title.Should().Be("first line");
        Card(vm, 1).Byline.Should().Be("someone · 09:00");
        Card(vm, 1).RatingBadge.Should().Be("NSFW · X");
        Card(vm, 1).IsSafe.Should().BeFalse();
        Card(vm, 2).Title.Should().Be("Image 2");
        Card(vm, 2).Byline.Should().Be("someone · Tue · no prompt");
        Card(vm, 3).Title.Should().HaveLength(60);
    }

    [Fact]
    public async Task OpenOnCivitai_MoreThanFive_AsksFirst()
    {
        for (long i = 1; i <= 6; i++) Save(Recipe(i), Today);
        var vm = Create();
        foreach (var id in Ids(vm)) vm.Select(Card(vm, id), true, false);
        _dialogs.Setup(d => d.ShowConfirmAsync("Open on Civitai", "Open 6 pages in your browser?")).ReturnsAsync(false);

        await vm.OpenOnCivitaiCommand.ExecuteAsync(null);
        _host.Verify(h => h.OpenUrl(It.IsAny<string>()), Times.Never);

        _dialogs.Setup(d => d.ShowConfirmAsync("Open on Civitai", "Open 6 pages in your browser?")).ReturnsAsync(true);
        await vm.OpenOnCivitaiCommand.ExecuteAsync(null);
        _host.Verify(h => h.OpenUrl(It.Is<string>(u => u.StartsWith("https://civitai.com/images/"))), Times.Exactly(6));
    }

    [Fact]
    public async Task OpenOnCivitai_UpToFive_DoesNotAsk()
    {
        Save(Recipe(1, rating: 8), Today);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);

        await vm.OpenOnCivitaiCommand.ExecuteAsync(null);

        _host.Verify(h => h.OpenUrl("https://civitai.red/images/1"), Times.Once);
        _dialogs.Verify(d => d.ShowConfirmAsync("Open on Civitai", It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Queue_ModelThatDoesNotListTheUsedVersion_IsNeitherCountedNorQueued()
    {
        var version = new CivitaiModelVersion { Id = 100, ModelId = 10, Name = "v1", BaseModel = "Krea 2" };
        var model = new CivitaiModel { Id = 10, Name = "Foo", Type = CivitaiModelType.LORA, ModelVersions = [new CivitaiModelVersion { Id = 101, ModelId = 10, Name = "v2" }] };
        Save(Recipe(1, resources: [new CivitaiRecipeResource(100, version, model, null, null), Lora(200, 20, "Bar")]), Today);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>? pairs = null;
        _host.Setup(h => h.EnqueueRecipeAsync(It.IsAny<List<(CivitaiResultViewModel, CivitaiVersionPickItemViewModel)>>()))
            .Callback<List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>>(p => pairs = p)
            .Returns(Task.CompletedTask);

        vm.QueueButtonText.Should().Be("Queue missing LoRAs (1)");
        vm.QueueMissingCommand.Execute(null);

        pairs!.Select(p => p.Pick.Version.Id).Should().Equal(200);
    }

    [Fact]
    public void QueueTooltip_CountsEachUnlinkedAndFailedLoraOnce()
    {
        var failed = new CivitaiRecipeResource(300, null, null, null, "429 Too Many Requests");
        var same = new RecipeLoraUse("x", "x.safetensors", 1, null);
        Save(Recipe(1, resources: [failed], unlinked: [same]), Today);
        Save(Recipe(2, resources: [failed], unlinked: [same]), Today);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);
        vm.Select(Card(vm, 2), true, false);

        vm.QueueTooltip.Should().Be("1 not on Civitai, 1 could not be loaded");
    }

    [Fact]
    public async Task ClearHistory_RecipeKeptWhileTheDialogIsOpen_Survives()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), Today);
        var vm = Create();
        _dialogs.Setup(d => d.ShowConfirmAsync("Clear history", It.IsAny<string>()))
            .Returns<string, string>((_, _) => { _store.SetKept([1], true); return Task.FromResult(true); });

        await vm.ClearHistoryCommand.ExecuteAsync(null);
        Pump();

        Directory.Exists(_store.FolderFor(1)).Should().BeTrue();
        Directory.Exists(_store.FolderFor(2)).Should().BeFalse();
    }

    [Fact]
    public async Task ClearHistory_WithBrokenRecipes_SaysTheyAreNotTouched()
    {
        Save(Recipe(1), Today);
        Broken(8);
        Broken(9);
        var vm = Create();

        await vm.ClearHistoryCommand.ExecuteAsync(null);

        _dialogs.Verify(d => d.ShowConfirmAsync("Clear history", It.Is<string>(m =>
            m.StartsWith("Delete all 1 recipe in History? This frees ") && m.EndsWith(". Kept recipes and 2 unreadable recipes are not touched."))), Times.Once);
    }

    [Fact]
    public void Open_ShowsRecipeInsideTheTab_BackRestoresListAndSelection()
    {
        Save(Recipe(1), Today);
        Save(Recipe(2), Today);
        Save(Recipe(3), Today, kept: true);
        var vm = Create();
        vm.SearchText = "cat";
        vm.Select(Card(vm, 2), false, false);

        vm.OpenCommand.Execute(Card(vm, 1));

        vm.IsRecipeOpen.Should().BeTrue();
        vm.OpenRecipe!.Recipe.ImageId.Should().Be(1);
        vm.BackText.Should().Be("← Back to History");

        vm.BackCommand.Execute(null);

        vm.IsRecipeOpen.Should().BeFalse();
        vm.OpenRecipe.Should().BeNull();
        vm.SearchText.Should().Be("cat");
        vm.IsKeptView.Should().BeFalse();
        Card(vm, 2).IsSelected.Should().BeTrue();
        vm.SelectedCount.Should().Be(1);

        vm.ShowKeptCommand.Execute(null);
        vm.OpenCommand.Execute(Card(vm, 3));
        vm.BackText.Should().Be("← Back to Kept");
        _logger.Verify(l => l.Info(It.IsAny<LogCategory>(), "CivitaiRecipe", It.Is<string>(m => m.Contains("opened")), It.IsAny<string?>()), Times.AtLeastOnce);
    }

    [Fact]
    public void Open_BrokenCard_DoesNothing()
    {
        Broken(9);
        var vm = Create();

        vm.OpenCommand.Execute(Card(vm, 9));

        vm.IsRecipeOpen.Should().BeFalse();
        vm.OpenRecipe.Should().BeNull();
    }

    [Fact]
    public void Open_InstalledIndexChanged_ReachesTheOpenRecipe()
    {
        Save(Recipe(1, resources: [Lora(100, 10, "Foo")]), Today);
        var vm = Create();
        vm.OpenCommand.Execute(Card(vm, 1));
        var row = vm.OpenRecipe!.Rows.Single();

        _installed = new CivitaiInstalledIndex([100], []);
        _host.Raise(h => h.InstalledIndexChanged += null, EventArgs.Empty);

        row.IsInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task Open_RetriesFailedResourceOnce_AndSaves()
    {
        var client = new Mock<DiffusionNexus.Civitai.ICivitaiClient>();
        var version = new CivitaiModelVersion { Id = 100, ModelId = 10, Name = "v1", BaseModel = "Krea 2" };
        client.Setup(c => c.GetModelVersionAsync(100, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(version);
        client.Setup(c => c.GetModelAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = 10, Name = "Foo", Type = CivitaiModelType.LORA, ModelVersions = [version] });
        _host.Setup(h => h.GetApiKeyAsync()).ReturnsAsync((string?)null);
        Save(Recipe(1, resources: [new CivitaiRecipeResource(100, null, null, null, "429 Too Many Requests")]), Today);
        var vm = new RecipesTabViewModel(_store, _host.Object, _dialogs.Object, _logger.Object, () => Now, null,
            () => new CivitaiImageRecipeService(client.Object, new HttpClient(), Path.Combine(_root, "cache"), null));

        vm.OpenCommand.Execute(Card(vm, 1));
        await vm.RetryTask!;
        Pump();

        _store.TryLoad(1)!.Saved!.Recipe.Resources.Single().LoadError.Should().BeNull();
        vm.OpenRecipe!.Recipe.Resources.Single().Model.Should().NotBeNull();

        vm.BackCommand.Execute(null);
        vm.OpenCommand.Execute(Card(vm, 1));
        if (vm.RetryTask is { } again) await again;

        client.Verify(c => c.GetModelVersionAsync(100, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static bool IsCancelled(CivitaiResultViewModel card) =>
        ((CancellationTokenSource)typeof(CivitaiResultViewModel)
            .GetField("_cts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(card)!)
        .IsCancellationRequested;

    private RecipesTabViewModel CreateWithRetry(Mock<DiffusionNexus.Civitai.ICivitaiClient> client)
    {
        _host.Setup(h => h.GetApiKeyAsync()).ReturnsAsync((string?)null);
        return new RecipesTabViewModel(_store, _host.Object, _dialogs.Object, _logger.Object, () => Now, null,
            () => new CivitaiImageRecipeService(client.Object, new HttpClient(), Path.Combine(_root, "cache"), null));
    }

    /// <summary>A client whose version 100 loads once <paramref name="gate"/> is released.</summary>
    private static Mock<DiffusionNexus.Civitai.ICivitaiClient> GatedClient(TaskCompletionSource gate)
    {
        var client = new Mock<DiffusionNexus.Civitai.ICivitaiClient>();
        var version = new CivitaiModelVersion { Id = 100, ModelId = 10, Name = "v1", BaseModel = "Krea 2" };
        client.Setup(c => c.GetModelVersionAsync(100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await gate.Task; return version; });
        client.Setup(c => c.GetModelAsync(10, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = 10, Name = "Foo", Type = CivitaiModelType.LORA, ModelVersions = [version] });
        return client;
    }

    private static CivitaiRecipeResource Failed429() => new(100, null, null, null, "429 Too Many Requests");

    [Fact]
    public void Back_CancelsTheCardsOfTheClosedRecipe()
    {
        var cards = new List<CivitaiResultViewModel>();
        _host.Setup(h => h.CreateRecipeCard(It.IsAny<CivitaiModel>()))
            .Returns<CivitaiModel>(m => { var c = new CivitaiResultViewModel(m, true); cards.Add(c); return c; });
        Save(Recipe(1, resources: [Lora(100, 10, "Foo")]), Today);
        var vm = Create();

        vm.OpenCommand.Execute(Card(vm, 1));
        cards.Should().ContainSingle();
        IsCancelled(cards[0]).Should().BeFalse();

        vm.BackCommand.Execute(null);

        IsCancelled(cards[0]).Should().BeTrue();
    }

    [Fact]
    public async Task Reopen_DuringTheRetry_ShowsTheRetriedResource()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Save(Recipe(1, resources: [Failed429()]), Today);
        var vm = CreateWithRetry(GatedClient(gate));

        vm.OpenCommand.Execute(Card(vm, 1));
        var retry = vm.RetryTask!;
        vm.BackCommand.Execute(null);
        vm.OpenCommand.Execute(Card(vm, 1));
        vm.OpenRecipe!.Recipe.Resources.Single().LoadError.Should().NotBeNull();

        gate.SetResult();
        await retry;
        Pump();

        vm.OpenRecipe!.Recipe.Resources.Single().LoadError.Should().BeNull();
    }

    [Fact]
    public async Task RetryEndingAfterDelete_DoesNotBringTheRecipeBack()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Save(Recipe(1, resources: [Failed429()]), Today);
        var vm = CreateWithRetry(GatedClient(gate));

        vm.OpenCommand.Execute(Card(vm, 1));
        var retry = vm.RetryTask!;
        vm.BackCommand.Execute(null);
        _store.Delete([1]);

        gate.SetResult();
        await retry;
        Pump();

        Directory.Exists(_store.FolderFor(1)).Should().BeFalse();
        Ids(vm).Should().BeEmpty();
    }

    [Fact]
    public async Task Open_RetryThatFailsAgain_IsNotRetriedOnReopen()
    {
        var client = new Mock<DiffusionNexus.Civitai.ICivitaiClient>();
        client.Setup(c => c.GetModelVersionAsync(100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("429 Too Many Requests"));
        Save(Recipe(1, resources: [Failed429()]), Today);
        var vm = CreateWithRetry(client);

        vm.OpenCommand.Execute(Card(vm, 1));
        await vm.RetryTask!;
        Pump();
        vm.BackCommand.Execute(null);
        vm.OpenCommand.Execute(Card(vm, 1));
        if (vm.RetryTask is { } again) await again;

        client.Verify(c => c.GetModelVersionAsync(100, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Open_RetryThatThrows_IsLoggedNotRaised()
    {
        _host.Setup(h => h.GetApiKeyAsync()).ThrowsAsync(new InvalidOperationException("boom"));
        Save(Recipe(1, resources: [new CivitaiRecipeResource(100, null, null, null, "429")]), Today);
        var vm = new RecipesTabViewModel(_store, _host.Object, _dialogs.Object, _logger.Object, () => Now, null,
            () => new CivitaiImageRecipeService(new Mock<DiffusionNexus.Civitai.ICivitaiClient>().Object, new HttpClient(), _root, null));

        vm.OpenCommand.Execute(Card(vm, 1));
        await vm.RetryTask!;

        vm.IsRecipeOpen.Should().BeTrue();
        _logger.Verify(l => l.Warn(It.IsAny<LogCategory>(), "CivitaiRecipe", It.Is<string>(m => m.Contains("boom")), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task ClearHistory_OneBroken_UsesSingular()
    {
        Save(Recipe(1), Today);
        Broken(8);
        var vm = Create();

        await vm.ClearHistoryCommand.ExecuteAsync(null);

        _dialogs.Verify(d => d.ShowConfirmAsync("Clear history", It.Is<string>(m =>
            m.EndsWith(". Kept recipes and 1 unreadable recipe are not touched."))), Times.Once);
    }

    [Fact]
    public void EmptyText_NamesWhyTheListIsEmpty()
    {
        var vm = Create();
        vm.EmptyText.Should().Be("No recipes yet. Paste a Civitai image link into the search box in Browse Civitai and it lands here.");

        vm.ShowKeptCommand.Execute(null);
        vm.EmptyText.Should().Be("Nothing kept yet. Use ★ on a recipe to keep it.");
    }

    [Fact]
    public void RecipeWithoutData_IsABrokenCard_AndTheTabStillBuilds()
    {
        Save(Recipe(1), Today);
        SaveAndEdit(Recipe(2), json => json["Recipe"]!["Data"] = null);

        var vm = Create();

        Card(vm, 2).IsBroken.Should().BeTrue();
        Card(vm, 1).IsBroken.Should().BeFalse();
        vm.HistoryCount.Should().Be(2);
    }

    [Fact]
    public void KeepSelected_NewerFormatRecipe_IsLeftAlone_AndTheStatusSaysSo()
    {
        Save(Recipe(1), Today);
        SaveAndEdit(Recipe(2), json => json["FormatVersion"] = SavedRecipe.CurrentFormat + 1);
        var vm = Create();
        vm.Select(Card(vm, 1), false, false);
        vm.Select(Card(vm, 2), true, false);

        vm.KeepSelectedCommand.Execute(null);
        Pump();

        _store.TryLoad(1)!.Saved!.Kept.Should().BeTrue();
        vm.StatusMessage.Should().Be("1 recipe made by a newer version of DiffusionNexus was left as it is.");
    }

    [Fact]
    public void EmptyText_WhenFiltersHideEverything_NamesTheHiddenOnes()
    {
        Save(Recipe(1, resources: [Lora(100, 10, "Foo")]), Today);
        Save(Recipe(2, rating: 8), Today);
        _installed = new CivitaiInstalledIndex([100], []);
        var vm = Create();
        vm.EmptyText.Should().BeNull();

        vm.StatusFilter = RecipeStatusFilter.Missing;

        vm.EmptyText.Should().Be("No recipe matches. 2 recipes hidden by filters in History");
        vm.StatusFilter = RecipeStatusFilter.All;
        vm.EmptyText.Should().BeNull();
    }
}
